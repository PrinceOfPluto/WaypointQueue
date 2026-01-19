using HarmonyLib;
using Model;
using Model.AI;
using System;
using System.Collections.Generic;
using System.Reflection;
using Track;
using Track.Search;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.Services;
using WaypointQueue.UUM;
using static Model.AI.AutoEngineer;

namespace WaypointQueue
{
    [HarmonyPatch(typeof(AutoEngineerPlanner))]
    internal class PatchAutoEngineerPlanner
    {
        [HarmonyPostfix]
        [HarmonyPatch("UpdateTargets")]
        static void UpdateTargetsPostfix(AutoEngineerPlanner __instance, float direction, ref AutoEngineer ____engineer)
        {
            AutoEngineerService autoEngineerService = Loader.ServiceProvider.GetService<AutoEngineerService>();
            ICarService carService = Loader.ServiceProvider.GetService<ICarService>();
            try
            {
                if (____engineer == null || ____engineer?.Locomotive == null)
                {
                    return;
                }

                BaseLocomotive loco = ____engineer.Locomotive;

                AutoEngineerOrdersHelper ordersHelper = autoEngineerService.GetOrdersHelper(loco);
                if (!ordersHelper.Orders.Waypoint.HasValue)
                {
                    return;
                }

                if (!WaypointQueueController.Shared.WaypointStateMap.TryGetValue(loco.id, out LocoWaypointState waypointState) || !waypointState.HasAnyWaypoints() || waypointState.UnresolvedWaypoint == null)
                {
                    Loader.LogDebug($"Update targets {loco.Ident} has no managed waypoints");
                    return;
                }

                if (waypointState.UnresolvedWaypoint?.LocationString != ordersHelper.Orders.Waypoint?.LocationString)
                {
                    Loader.LogDebug($"Loco {loco.Ident} unresolved waypoint to {waypointState.UnresolvedWaypoint?.LocationString} did not match {ordersHelper.Orders.Waypoint?.LocationString}");
                }

                // Only update targets if we are not stopping
                if (waypointState.UnresolvedWaypoint.StopAtWaypoint)
                {
                    return;
                }

                Targets targets = (Targets)AccessTools.Field(typeof(AutoEngineer), "_targets")?.GetValue(____engineer);

                if (targets == null)
                {
                    Loader.LogDebug($"Targets object was null");
                    return;
                }

                List<Targets.Target> updatedTargets = [.. targets.AllTargets];

                float targetSpeed = ordersHelper.Orders.MaxSpeedMph;
                if (waypointState.UnresolvedWaypoint.WillLimitPassingSpeed)
                {
                    targetSpeed = waypointState.UnresolvedWaypoint.WaypointTargetSpeed;
                }

                float maxSafeSpeed = Mathf.Abs(targets.MaxSpeedMph);
                float speedToSet = Mathf.Clamp(targetSpeed, 0, maxSafeSpeed);
                float signedSpeedToSet = speedToSet * Mathf.Sign(direction);

                if (updatedTargets != null && updatedTargets.Count > 0)
                {
                    int indexOfWaypoint = updatedTargets.FindIndex(t => t.Reason == "Running to waypoint" || t.Reason == "At waypoint");
                    if (indexOfWaypoint != -1)
                    {
                        (Location closestTrainEnd, Location _) = carService.GetTrainEndLocations(waypointState.UnresolvedWaypoint, out float closestDistance, out var _, out var _);

                        GraphRouteSearchExtension.TryFindDistance(Graph.Shared, closestTrainEnd, waypointState.UnresolvedWaypoint.Location, out float totalDistance, out float traverseTimeSeconds);

                        Targets.Target t = updatedTargets[indexOfWaypoint];
                        if (Mathf.Abs(totalDistance - t.Distance) < 5f)
                        {
                            updatedTargets[indexOfWaypoint] = new Targets.Target(signedSpeedToSet, t.Distance, t.Reason);
                        }
                    }
                    else
                    {
                        Loader.LogDebug($"Did not find waypoint target");
                    }
                }

                FieldInfo allTargetsFI = AccessTools.Field(typeof(Targets), "AllTargets");
                if (allTargetsFI != null)
                {
                    allTargetsFI.SetValue(targets, updatedTargets);
                }
                else
                {
                    Loader.LogDebug($"AllTargetsFI was null");
                }
            }
            catch (Exception e)
            {
                Loader.LogError($"UpdateTargetsPostfix exception: {e}");
            }
        }

        private const float HighSpeedMphThreshold = 35f;
        private const float HighSpeedWaypointRadiusMeters = 25f;

        [HarmonyPostfix]
        [HarmonyPatch("IsWaypointSatisfied")]
        private static void IsWaypointSatisfiedPostfix(
            AutoEngineerPlanner __instance,
            OrderWaypoint waypoint,
            ref bool __result,
            BaseLocomotive ____locomotive,
            Graph ____graph,
            ref Location? ____routeTargetLocation
        )
        {
            try
            {

                if (__result)
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
                    Loader.LogError($"High-speed waypoint distance calc failed: {ex}");
                    return;
                }

                if (bestDistance <= HighSpeedWaypointRadiusMeters)
                {
                    __result = true;
                }
            }
            catch (Exception ex)
            {
                Loader.LogError($"IsWaypointSatisfiedPostfix failed: {ex}");
            }
        }
    }
}