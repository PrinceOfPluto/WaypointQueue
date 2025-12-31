using HarmonyLib;
using Model;
using Model.AI;
using System.Collections.Generic;
using UI.Common;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.UUM;
using Location = Track.Location;

namespace WaypointQueue
{
    [HarmonyPatch(typeof(AutoEngineerOrdersHelper))]
    internal class PatchAutoEngineerOrdersHelper
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(AutoEngineerOrdersHelper.SetWaypoint))]
        static bool SetWaypointPrefix(Location location, string coupleToCarId, ref Car ____locomotive, ref AutoEngineerPersistence ____persistence)
        {
            //Loader.LogDebug($"SetWaypoint prefix");

            OrderWaypoint? existingWaypoint = ____persistence.Orders.Waypoint;
            bool isAppendingWaypoint = Input.GetKey(Loader.Settings.queuedWaypointModeKey.keyCode);
            bool isReplacingWaypoint = Input.GetKey(Loader.Settings.replaceWaypointModeKey.keyCode);
            bool isInsertingNext = Input.GetKey(Loader.Settings.insertNextWaypointModeKey.keyCode);

            bool isAnyModifierPressed = isAppendingWaypoint || isReplacingWaypoint || isInsertingNext;
            //Loader.LogDebug($"Appending: {isAppendingWaypoint}, Replacing {isReplacingWaypoint}, Inserting {isInsertingNext}");

            Car loco = ____locomotive;

            // Setting a waypoint without one of the modifiers will reset the locomotive's waypoint list
            if (!isAnyModifierPressed)
            {
                List<ManagedWaypoint> waypoints = WaypointQueueController.Shared.GetWaypointList(____locomotive) ?? [];
                if (waypoints.Count > 1)
                {
                    ModalAlertController.Present($"{____locomotive.Ident} already has {waypoints.Count} waypoints.", "Are you sure you would like to overwrite the queue?\n\nThis cannot be undone.",
                    [
                        (true, "Overwrite"),
                        (false, "Cancel")
                    ], delegate (bool b)
                    {
                        if (b)
                        {
                            WaypointQueueController.Shared.ClearWaypointState(loco);
                            HandleAddingWaypoint(existingWaypoint, loco, location, coupleToCarId, isReplacingWaypoint, isInsertingNext);
                        }
                    });
                }
                else
                {
                    WaypointQueueController.Shared.ClearWaypointState(____locomotive);
                    HandleAddingWaypoint(existingWaypoint, loco, location, coupleToCarId, isReplacingWaypoint, isInsertingNext);
                }
            }
            else
            {
                HandleAddingWaypoint(existingWaypoint, loco, location, coupleToCarId, isReplacingWaypoint, isInsertingNext);
            }

            // Skip original since we need to manage waypoints to keep track of any orders that need to be resolved
            return false;
        }

        private static void HandleAddingWaypoint(OrderWaypoint? existingWaypoint, Car loco, Location location, string coupleToCarId, bool isReplacingWaypoint, bool isInsertingNext)
        {
            if (existingWaypoint != null)
            {
                //Loader.LogDebug($"Current waypoint for {loco.Ident} is {existingWaypoint.Value.LocationString}");
            }

            // Always add the waypoint to the queue
            if (location != null)
            {
                WaypointQueueController.Shared.AddWaypoint(loco, location, coupleToCarId, isReplacingWaypoint, isInsertingNext);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(AutoEngineerOrdersHelper.ClearWaypoint))]
        static bool ClearWaypointPrefix(ref Car ____locomotive, ref AutoEngineerPersistence ____persistence)
        {
            if (WaypointQueueController.Shared.TryGetActiveWaypointFor(____locomotive, out ManagedWaypoint waypoint))
            {
                WaypointResolver.CleanupBeforeRemovingWaypoint(waypoint);
            }
            return true;
        }
    }
}
