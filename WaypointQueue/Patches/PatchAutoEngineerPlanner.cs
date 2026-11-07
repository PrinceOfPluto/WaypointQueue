using Game.State;
using HarmonyLib;
using Model;
using Model.AI;
using System;
using System.Collections.Generic;
using Track;
using UnityEngine;
using WaypointQueue.State;

namespace WaypointQueue
{
    [HarmonyPatch(typeof(AutoEngineerPlanner))]
    internal class PatchAutoEngineerPlanner
    {
        private const float HighSpeedMphThreshold = 35f;
        private const float HighSpeedWaypointRadiusMeters = 25f;

        [HarmonyPostfix]
        [HarmonyPatch("CheckWaypointArrival")]
        private static void CheckWaypointArrivalPostfix(
            AutoEngineerPlanner __instance,
            OrderWaypoint waypoint,
            ref WaypointArrival __result,
            BaseLocomotive ____locomotive,
            Graph ____graph,
            ref Location? ____routeTargetLocation
        )
        {
            try
            {

                if (__result == WaypointArrival.Arrived)
                    return;

                if (____locomotive == null)
                    return;

                if (!WaypointQueueController.Shared.TryGetActiveWaypointFor(____locomotive, out ManagedWaypoint managed))
                    return;

                if (managed.StopAtWaypoint || managed.WaypointTargetSpeed <= 0)
                    return;

                float speedMph = ____locomotive.VelocityMphAbs;
                if (speedMph < HighSpeedMphThreshold)
                    return;

                if (!____routeTargetLocation.HasValue || ____graph == null)
                    return;

                Location targetLoc = ____routeTargetLocation.Value;

                float bestDistance;
                try
                {
                    Location locF = ____locomotive.LocationF;
                    Location locR = ____locomotive.LocationR;

                    float dF = Mathf.Abs(____graph.GetDistanceBetweenClose(targetLoc, locF));
                    float dR = Mathf.Abs(____graph.GetDistanceBetweenClose(targetLoc, locR));
                    bestDistance = Mathf.Min(dF, dR);
                }
                catch (Exception ex)
                {
                    ModLocator.Loader.LogError($"High-speed waypoint distance calc failed: {ex}");
                    return;
                }

                if (bestDistance <= HighSpeedWaypointRadiusMeters)
                {
                    __result = WaypointArrival.Arrived;
                }
            }
            catch (Exception ex)
            {
                ModLocator.Loader.LogError($"IsWaypointSatisfiedPostfix failed: {ex}");
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch("RestoreAllRemainingSwitches")]
        private static void RestoreAllRemainingSwitchesPrefix(
                Dictionary<string, bool> ____routeSwitchesToRestore,
                out Dictionary<string, bool> __state
            )
        {
            __state = ____routeSwitchesToRestore != null && ____routeSwitchesToRestore.Count > 0
                ? new Dictionary<string, bool>(____routeSwitchesToRestore)
                : null;
        }

        [HarmonyPostfix]
        [HarmonyPatch("RestoreAllRemainingSwitches")]
        private static void RestoreAllRemainingSwitchesPostfix(
            AutoEngineerPlanner __instance,
            BaseLocomotive ____locomotive,
            Graph ____graph,
            Dictionary<string, bool> __state
        )
        {
            if (!StateManager.IsHost) return;

            try
            {
                KeepUnrestored(__instance, ____locomotive, ____graph, __state, "with the train on it");
            }
            catch (Exception ex)
            {
                ModLocator.Loader.LogError($"RestoreAllRemainingSwitches postfix failed: {ex}");
            }
        }

        // Runs before ClearRoute empties the list (after RestoreAllRemainingSwitches it's already empty).
        [HarmonyPrefix]
        [HarmonyPatch("ClearRoute")]
        private static void ClearRoutePrefix(
            AutoEngineerPlanner __instance,
            BaseLocomotive ____locomotive,
            Graph ____graph,
            Dictionary<string, bool> ____routeSwitchesToRestore,
            Orders ____orders
        )
        {
            if (!StateManager.IsHost) return;

            try
            {
                if (____orders.Mode == Game.Messages.AutoEngineerMode.Waypoint && ____routeSwitchesToRestore != null && ____routeSwitchesToRestore.Count > 0)
                    KeepUnrestored(__instance, ____locomotive, ____graph, new Dictionary<string, bool>(____routeSwitchesToRestore), "when its route was cleared");
            }
            catch (Exception ex)
            {
                ModLocator.Loader.LogError($"ClearRoute prefix failed: {ex}");
            }
        }

        // The AE records where a switch was when it first throws it, as the position to put it back to. A switch an
        // earlier route left thrown, still waiting in the restorer, isn't where it belongs, so recording it there made
        // the new route put it back the wrong way. The restorer saw the switch "back" and dropped it, and from then on
        // the thrown position was the one restored (a switch left reversed for good). A route that throws a switch the
        // restorer is holding takes it over with the restorer's position.
        [HarmonyPrefix]
        [HarmonyPatch("TrySetSwitch")]
        private static void TrySetSwitchPrefix(TrackNode node, Dictionary<string, bool> ____routeSwitchesToRestore, out bool __state)
        {
            __state = node != null && ____routeSwitchesToRestore != null && !____routeSwitchesToRestore.ContainsKey(node.id);
        }

        [HarmonyPostfix]
        [HarmonyPatch("TrySetSwitch")]
        private static void TrySetSwitchPostfix(TrackNode node, Dictionary<string, bool> ____routeSwitchesToRestore, BaseLocomotive ____locomotive, bool __state)
        {
            if (!StateManager.IsHost) return;

            var restorer = ModLocator.Loader.PendingSwitchRestorer;
            try
            {
                if (__state && ____routeSwitchesToRestore.ContainsKey(node.id) && restorer.TryTake(node.id, out bool originalThrown))
                {
                    ____routeSwitchesToRestore[node.id] = originalThrown;
                    ModLocator.Loader.LogDebug($"Loco {____locomotive?.Ident}: route takes over switch {node.id} from the restorer; it goes back to {(originalThrown ? "reverse" : "normal")}");
                }
            }
            catch (Exception ex)
            {
                ModLocator.Loader.LogError($"TrySetSwitch postfix failed: {ex}");
            }
        }

        // The switches in the list still away from where the AE found them go to the restorer.
        private static void KeepUnrestored(AutoEngineerPlanner planner, BaseLocomotive locomotive, Graph graph, Dictionary<string, bool> switches, string why)
        {
            if (switches == null || locomotive == null || graph == null || !StateManager.IsHost)
                return;

            var restorer = ModLocator.Loader.PendingSwitchRestorer;

            foreach (KeyValuePair<string, bool> entry in switches)
            {
                TrackNode node = graph.GetNode(entry.Key);
                if (node == null || node.isThrown == entry.Value)
                    continue;

                restorer.Add(new PendingSwitchRestorer.PendingSwitch
                {
                    NodeId = entry.Key,
                    OriginalThrown = entry.Value,
                    LeftThrown = node.isThrown,
                    LocomotiveId = locomotive.id,
                    LocomotiveIdent = locomotive.Ident.ToString(),
                    Position = node.Position,
                    AddedAt = Time.unscaledTime
                });
                ModLocator.Loader.LogDebug($"Loco {locomotive.Ident} couldn't reset switch {entry.Key} {why}; will reset it once the train is clear");
            }
        }
    }
}