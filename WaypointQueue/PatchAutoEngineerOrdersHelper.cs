using HarmonyLib;
using Model;
using Model.AI;
using Track;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    [HarmonyPatch(typeof(AutoEngineerOrdersHelper))]
    internal class PatchAutoEngineerOrdersHelper
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(AutoEngineerOrdersHelper.SetWaypoint))]
        static bool SetWaypointPrefix(Location location, string coupleToCarId, ref Car ____locomotive, ref AutoEngineerPersistence ____persistence)
        {
            Loader.LogDebug($"SetWaypoint prefix");

            OrderWaypoint? existingWaypoint = ____persistence.Orders.Waypoint;
            bool isAppendingWaypoint = Input.GetKey(Loader.Settings.queuedWaypointModeKey.keyCode);
            bool isReplacingWaypoint = Input.GetKey(Loader.Settings.replaceWaypointModeKey.keyCode);

            // Setting a waypoint without one of the modifiers will reset the locomotive's waypoint list
            if (!isAppendingWaypoint && !isReplacingWaypoint)
            {
                WaypointQueueController.Shared.ClearWaypointState(____locomotive);
            }
            if (existingWaypoint != null)
            {
                Loader.LogDebug($"Current waypoint for {____locomotive.Ident} is {existingWaypoint.Value.LocationString}");
            }

            // Always add the waypoint to the queue
            if (location != null)
            {
                WaypointQueueController.Shared.AddWaypoint(____locomotive, location, coupleToCarId, isReplacingWaypoint);
            }

            // Skip original since we need to manage waypoints to keep track of any orders that need to be resolved
            return false;
        }
    }
}
