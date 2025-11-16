using HarmonyLib;
using Model;
using Model.AI;
using System;
using System.Collections.Generic;
using System.Reflection;
using UI.EngineControls;
using WaypointQueue.UUM;
using static Model.AI.AutoEngineer;

namespace WaypointQueue
{
    [HarmonyPatch(typeof(AutoEngineerPlanner))]
    internal class PatchAutoEngineerPlanner
    {
        [HarmonyPostfix]
        [HarmonyPatch("UpdateTargets")]
        static void UpdateTargetsPostfix(AutoEngineerPlanner __instance, ref AutoEngineer ____engineer)
        {
            try
            {
                if (____engineer == null)
                {
                    Loader.LogDebug($"Update targets engineer was null");
                    return;
                }

                BaseLocomotive loco = ____engineer.Locomotive;

                if (loco == null)
                {
                    Loader.LogDebug($"Update targets loco was null");
                    return;
                }

                AutoEngineerOrdersHelper ordersHelper = WaypointQueueController.Shared.GetOrdersHelper(loco);
                if (!ordersHelper.Orders.Waypoint.HasValue)
                {
                    Loader.LogDebug($"Update targets has no current order waypoint");
                    return;
                }

                LocoWaypointState waypointState = WaypointQueueController.Shared.GetLocoWaypointState(loco);

                if (waypointState == null || !waypointState.HasAnyWaypoints())
                {
                    Loader.LogDebug($"Update targets {loco.Ident} has no managed waypoints");
                    return;
                }

                if (waypointState.UnresolvedWaypoint?.LocationString != ordersHelper.Orders.Waypoint?.LocationString)
                {
                    Loader.LogDebug($"Loco {loco.Ident} unresolved waypoint to {waypointState.UnresolvedWaypoint?.LocationString} did not match {ordersHelper.Orders.Waypoint?.LocationString}");
                }

                // Only update targets if we are not stopping
                if (!waypointState.UnresolvedWaypoint.DoNotStop)
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

                if (updatedTargets != null && updatedTargets.Count > 0)
                {
                    int indexOfWaypoint = updatedTargets.FindIndex(t => t.Reason == "Running to waypoint" || t.Reason == "At waypoint");
                    if (indexOfWaypoint != -1)
                    {
                        Targets.Target t = updatedTargets[indexOfWaypoint];
                        updatedTargets[indexOfWaypoint] = new Targets.Target(targets.MaxSpeedMph, t.Distance, t.Reason);
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
                Loader.Log($"UpdateTargetsPostfix exception: {e}");
            }
        }
    }
}