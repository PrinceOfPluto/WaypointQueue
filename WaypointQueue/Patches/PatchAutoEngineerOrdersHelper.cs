using HarmonyLib;
using Model;
using Model.AI;
using System.Collections.Generic;
using UI.Common;
using UI.EngineControls;
using WaypointQueue.Services;
using WaypointQueue.State;

namespace WaypointQueue
{
    [HarmonyPatch(typeof(AutoEngineerOrdersHelper))]
    internal class PatchAutoEngineerOrdersHelper
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(AutoEngineerOrdersHelper.SetWaypointOrder))]
        static bool SetWaypointOrderPrefix(OrderWaypoint waypoint, int? maxSpeedMph, ref Car ____locomotive, ref AutoEngineerPersistence ____persistence)
        {
            AutoEngineerService autoEngineerService = ModLocator.ServiceProvider.GetService<AutoEngineerService>();
            var ordersHelper = autoEngineerService.GetOrdersHelper((BaseLocomotive)____locomotive);
            ordersHelper.SetOrdersValue(null, null, maxSpeedMph: maxSpeedMph, null, null);

            return SetWaypointPrefix(waypoint, ref ____locomotive, ref ____persistence);
        }


        [HarmonyPrefix]
        [HarmonyPatch(nameof(AutoEngineerOrdersHelper.SetWaypoint))]
        static bool SetWaypointPrefix(OrderWaypoint waypoint, ref Car ____locomotive, ref AutoEngineerPersistence ____persistence)
        {
            OrderWaypoint? existingWaypoint = ____persistence.Orders.Waypoint;
            bool isAppendingWaypoint = ModLocator.Settings.queuedWaypointModeKey.Pressed();
            bool isReplacingWaypoint = ModLocator.Settings.replaceWaypointModeKey.Pressed();
            bool isInsertingNext = ModLocator.Settings.insertNextWaypointModeKey.Pressed();

            bool isAnyModifierPressed = isAppendingWaypoint || isReplacingWaypoint || isInsertingNext;
            //Loader.LogDebug($"Appending: {isAppendingWaypoint}, Replacing {isReplacingWaypoint}, Inserting {isInsertingNext}");

            if (!isAnyModifierPressed && !ModLocator.Settings.UseLegacyHotkeyOptions)
            {
                isAppendingWaypoint = true;
                isAnyModifierPressed = true;
            }

            BaseLocomotive loco = (BaseLocomotive)____locomotive;

            // Setting a waypoint without one of the modifiers will reset the locomotive's waypoint list
            if (!isAnyModifierPressed)
            {
                LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(loco.id);
                List<ManagedWaypoint> waypoints = state.Waypoints;
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
                            ModStateManager.Shared.RemoveLocoWaypointState(loco.id);
                            HandleAddingWaypoint(loco, existingWaypoint, waypoint, isReplacingWaypoint, isInsertingNext);
                        }
                    });
                }
                else
                {
                    ModStateManager.Shared.RemoveLocoWaypointState(____locomotive.id);
                    HandleAddingWaypoint(loco, existingWaypoint, waypoint, isReplacingWaypoint, isInsertingNext);
                }
            }
            else
            {
                HandleAddingWaypoint(loco, existingWaypoint, waypoint, isReplacingWaypoint, isInsertingNext);
            }

            // Skip original since we need to manage waypoints to keep track of any orders that need to be resolved
            return false;
        }

        private static void HandleAddingWaypoint(BaseLocomotive loco, OrderWaypoint? existingWaypoint, OrderWaypoint newWaypoint, bool isReplacingWaypoint, bool isInsertingNext)
        {
            if (existingWaypoint != null)
            {
                //Loader.LogDebug($"Current waypoint for {loco.Ident} is {existingWaypoint.Value.LocationString}");
            }

            // Always add the waypoint to the queue
            if (!string.IsNullOrEmpty(newWaypoint.LocationString))
            {
                WaypointQueueController.Shared.AddWaypoint(loco, newWaypoint, isReplacingWaypoint, isInsertingNext);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(AutoEngineerOrdersHelper.ClearWaypoint))]
        static bool ClearWaypointPrefix(ref Car ____locomotive, ref AutoEngineerPersistence ____persistence)
        {
            WaypointResolver resolver = ModLocator.ServiceProvider.GetService<WaypointResolver>();
            if (WaypointQueueController.Shared.TryGetActiveWaypointFor((BaseLocomotive)____locomotive, out ManagedWaypoint waypoint))
            {
                resolver.CleanupBeforeRemovingWaypoint(waypoint);
            }
            return true;
        }
    }
}
