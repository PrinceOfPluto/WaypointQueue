using Game.Events;
using Game.State;
using HarmonyLib;
using Helpers;
using Model;
using Model.AI;
using System;
using System.Collections;
using System.Collections.Generic;
using Track;
using Track.Search;
using UnityEngine;

namespace WaypointQueue
{
    // When the AE reaches a waypoint, RestoreAllRemainingSwitches resets the switches it threw and
    // then forgets them all, skipping any switch a car is still on. A train that stops on a switch
    // it threw therefore leaves it thrown after it moves off. Keep those switches and reset each one
    // once the train is clear of it.
    //
    // ClearRoute forgets them too, without resetting any. It runs as soon as the waypoint is cleared (the planner
    // handles order changes at once), so cancelling a waypoint and sending the next one straight away, as the queue
    // does, left every switch the old route had thrown and not yet reset. Those go to the same restorer, but only
    // while the AE stays in waypoint mode: a player who switches it to Road, Yard or Off takes the train on from
    // there, and the switches lined ahead of it stay as they are.
    [HarmonyPatch(typeof(AutoEngineerPlanner))]
    internal class PatchRestoreSwitchesAfterWaypoint
    {
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
            try
            {
                KeepUnrestored(__instance, ____locomotive, ____graph, __state, "with the train on it");
            }
            catch (Exception ex)
            {
                ModLocator.Loader.LogError($"PatchRestoreSwitchesAfterWaypoint postfix failed: {ex}");
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
            try
            {
                if (____orders.Mode == Game.Messages.AutoEngineerMode.Waypoint && ____routeSwitchesToRestore != null && ____routeSwitchesToRestore.Count > 0)
                    KeepUnrestored(__instance, ____locomotive, ____graph, new Dictionary<string, bool>(____routeSwitchesToRestore), "when its route was cleared");
            }
            catch (Exception ex)
            {
                ModLocator.Loader.LogError($"PatchRestoreSwitchesAfterWaypoint ClearRoute prefix failed: {ex}");
            }
        }

        // The AE records where a switch was when it first throws it, as the position to put it back to. A switch an
        // earlier route left thrown, still waiting in the restorer, isn't where it belongs, so recording it there made
        // the new route put it back the wrong way: the restorer saw the switch "back" and dropped it, and from then on
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
            try
            {
                if (__state && ____routeSwitchesToRestore.ContainsKey(node.id) && PendingSwitchRestorer.TryTake(node.id, out bool originalThrown))
                {
                    ____routeSwitchesToRestore[node.id] = originalThrown;
                    ModLocator.Loader.LogDebug($"Loco {____locomotive?.Ident}: route takes over switch {node.id} from the restorer; it goes back to {(originalThrown ? "reverse" : "normal")}");
                }
            }
            catch (Exception ex)
            {
                ModLocator.Loader.LogError($"PatchRestoreSwitchesAfterWaypoint TrySetSwitch postfix failed: {ex}");
            }
        }

        // The switches in the list still away from where the AE found them go to the restorer.
        private static void KeepUnrestored(AutoEngineerPlanner planner, BaseLocomotive locomotive, Graph graph, Dictionary<string, bool> switches, string why)
        {
            if (switches == null || locomotive == null || graph == null)
                return;

            foreach (KeyValuePair<string, bool> entry in switches)
            {
                TrackNode node = graph.GetNode(entry.Key);
                if (node == null || node.isThrown == entry.Value)
                    continue;

                PendingSwitchRestorer.Add(new PendingSwitchRestorer.PendingSwitch
                {
                    NodeId = entry.Key,
                    OriginalThrown = entry.Value,
                    LeftThrown = node.isThrown,
                    Planner = new WeakReference<AutoEngineerPlanner>(planner),
                    Locomotive = new WeakReference<BaseLocomotive>(locomotive),
                    LocomotiveIdent = locomotive.Ident.ToString(),
                    Position = node.Position,
                    AddedAt = Time.unscaledTime
                });
                ModLocator.Loader.LogDebug($"Loco {locomotive.Ident} couldn't reset switch {entry.Key} {why}; will reset it once the train is clear");
            }
        }
    }

    // Retries the switches every couple of seconds, on the host only.
    internal class PendingSwitchRestorer : MonoBehaviour
    {
        internal sealed class PendingSwitch
        {
            public string NodeId;

            // The position to put the switch back to, and the position the AE left it in.
            public bool OriginalThrown;
            public bool LeftThrown;

            public WeakReference<AutoEngineerPlanner> Planner;
            public WeakReference<BaseLocomotive> Locomotive;
            public string LocomotiveIdent;

            // Switch position (game space) and when it was added (unscaled time).
            public Vector3 Position;
            public float AddedAt;
        }

        private const float IntervalSeconds = 2f;

        // Reset a switch only while the train that left it is still close by, as the game does for
        // switches a train has just passed. A switch left longer, or by a train that has gone, may
        // now be relied on by another train, so it is dropped instead of thrown.
        private const float NearbyMeters = 200f;
        private const float MaxAgeSeconds = 600f;

        private static readonly Dictionary<string, PendingSwitch> _pending = [];
        private static PendingSwitchRestorer _instance;

        private static readonly AccessTools.FieldRef<AutoEngineerPlanner, List<RouteSearch.Step>> _route =
            AccessTools.FieldRefAccess<AutoEngineerPlanner, List<RouteSearch.Step>>("_route");

        private static readonly AccessTools.FieldRef<AutoEngineerPlanner, int> _startStepIndex =
            AccessTools.FieldRefAccess<AutoEngineerPlanner, int>("_startStepIndex");

        private Coroutine _checking;

        // Added to the mod's own game object by Loader. The check loop only runs while a switch is waiting.
        internal static void Add(PendingSwitch pending)
        {
            _pending[pending.NodeId] = pending;
            if (_instance == null)
            {
                ModLocator.Loader.LogError($"Switch {pending.NodeId} can't be reset: the switch restorer isn't loaded");
                return;
            }
            if (_instance._checking == null)
                _instance._checking = _instance.StartCoroutine(_instance.CheckWhilePending());
        }

        // Hands a waiting switch over to a route that is throwing it: the position to put it back to. It's no longer
        // retried here.
        internal static bool TryTake(string nodeId, out bool originalThrown)
        {
            originalThrown = false;
            if (!_pending.TryGetValue(nodeId, out PendingSwitch pending))
                return false;
            _pending.Remove(nodeId);
            originalThrown = pending.OriginalThrown;
            return true;
        }

        // Called when the mod unloads, so nothing is thrown after the patches are removed. The component goes with the
        // mod's game object.
        internal static void Shutdown()
        {
            _pending.Clear();
            if (_instance != null)
            {
                _instance.StopAllCoroutines();
                _instance._checking = null;
            }
        }

        private void Awake()
        {
            _instance = this;
            // Pending switches belong to the loaded save.
            EventBus.Subscribe<MapWillUnloadEvent>(this, _ => _pending.Clear());
        }

        private void OnDestroy()
        {
            EventBus.Unsubscribe(this);
            if (_instance == this)
                _instance = null;
        }

        // Checks the waiting switches every IntervalSeconds until none are left.
        private IEnumerator CheckWhilePending()
        {
            var wait = new WaitForSecondsRealtime(IntervalSeconds);
            while (_pending.Count > 0)
            {
                yield return wait;
                CheckPending();
            }
            _checking = null;
        }

        private void CheckPending()
        {
            if (_pending.Count == 0 || !StateManager.IsHost || TrainController.Shared == null)
                return;

            TrainController trainController = TrainController.Shared;
            Graph graph = Graph.Shared;
            List<string> done = [];
            foreach (PendingSwitch pending in _pending.Values)
            {
                try
                {
                    TrackNode node = graph.GetNode(pending.NodeId);
                    if (node == null || node.isThrown == pending.OriginalThrown)
                    {
                        // Gone, or already back where it was.
                        done.Add(pending.NodeId);
                        continue;
                    }

                    if (node.isThrown != pending.LeftThrown)
                    {
                        ModLocator.Loader.LogDebug($"Loco {pending.LocomotiveIdent}: switch {pending.NodeId} was changed by someone else; leaving it");
                        done.Add(pending.NodeId);
                        continue;
                    }

                    if (node.IsCTCSwitch && !node.IsCTCSwitchUnlocked)
                    {
                        ModLocator.Loader.LogDebug($"Loco {pending.LocomotiveIdent}: switch {pending.NodeId} is under CTC control; leaving it to the dispatcher");
                        done.Add(pending.NodeId);
                        continue;
                    }

                    if (Time.unscaledTime - pending.AddedAt > MaxAgeSeconds)
                    {
                        ModLocator.Loader.LogDebug($"Loco {pending.LocomotiveIdent}: switch {pending.NodeId} was left too long; leaving it");
                        done.Add(pending.NodeId);
                        continue;
                    }

                    if (IsOnCurrentRoute(pending, node) || !trainController.CanSetSwitch(node, pending.OriginalThrown, out _))
                        continue;

                    if (!IsTrainNearby(pending, graph))
                    {
                        ModLocator.Loader.LogDebug($"Loco {pending.LocomotiveIdent}: train is no longer near switch {pending.NodeId}; leaving it");
                        done.Add(pending.NodeId);
                        continue;
                    }

                    if (trainController.TrySetSwitch(pending.NodeId, pending.OriginalThrown, "AE " + pending.LocomotiveIdent, out string error))
                        ModLocator.Loader.LogDebug($"Loco {pending.LocomotiveIdent}: reset switch {pending.NodeId} now that the train is clear");
                    else
                        ModLocator.Loader.LogError($"Loco {pending.LocomotiveIdent}: couldn't reset switch {pending.NodeId}: {error}");

                    done.Add(pending.NodeId);
                }
                catch (Exception ex)
                {
                    ModLocator.Loader.LogError($"Restoring switch {pending.NodeId} failed: {ex}");
                    done.Add(pending.NodeId);
                }
            }

            foreach (string id in done)
                _pending.Remove(id);
        }

        // Whether any car of the train that left the switch is still within NearbyMeters of it.
        private static bool IsTrainNearby(PendingSwitch pending, Graph graph)
        {
            if (!pending.Locomotive.TryGetTarget(out BaseLocomotive locomotive) || locomotive == null)
                return false;

            foreach (Car car in locomotive.EnumerateCoupled())
            {
                if (Vector3.Distance(graph.GetPosition(car.LocationA), pending.Position) <= NearbyMeters
                    || Vector3.Distance(graph.GetPosition(car.LocationB), pending.Position) <= NearbyMeters)
                    return true;
            }
            return false;
        }

        // The AE's current route still needs the switch where it is, for example a move that comes
        // back over it.
        private static bool IsOnCurrentRoute(PendingSwitch pending, TrackNode node)
        {
            if (!pending.Planner.TryGetTarget(out AutoEngineerPlanner planner) || planner == null)
                return false;

            List<RouteSearch.Step> route = _route(planner);
            if (route == null)
                return false;

            for (int i = Math.Max(0, _startStepIndex(planner)); i < route.Count; i++)
            {
                if (route[i].Node == node)
                    return true;
            }
            return false;
        }
    }
}
