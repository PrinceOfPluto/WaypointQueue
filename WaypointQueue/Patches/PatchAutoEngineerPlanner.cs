using HarmonyLib;
using Model;
using Model.AI;
using System;
using Track;
using UnityEngine;

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
    }
}