using System;
using System.Collections.Generic;
using HarmonyLib;
using Game.Messages;
using Model.AI;
using RollingStock;
using UnityEngine;
using WaypointQueue.UUM;
using Model;
using System.IO;
using System.Text;
using Track;

namespace WaypointQueue
{
    [HarmonyPatch(typeof(AutoEngineerPlanner))]
    internal static class PatchAutoEngineerPlanner
    {

        [HarmonyPostfix]
        [HarmonyPatch("UpdateTargets")]
        private static void UpdateTargetsPostfix(
            AutoEngineerPlanner __instance,
            float direction,
            int enabledMaxSpeedMph,
            AutoEngineer ____engineer,
            Orders ____orders
        )
        {
            try
            {
                if (____engineer == null)
                    return;

                if (____orders.Mode != AutoEngineerMode.Waypoint)
                    return;

                var loco = ____engineer.Locomotive;
                if (loco == null)
                    return;


                if (!WaypointQueueController.TryGetActiveWaypointFor(loco, out ManagedWaypoint managed))
                    return;

                var engType = ____engineer.GetType();
                var targetsField = AccessTools.Field(engType, "_targets");
                if (targetsField == null)
                    return;

                var targets = (AutoEngineer.Targets)targetsField.GetValue(____engineer);
                var allTargets = targets.AllTargets;
                if (allTargets == null || allTargets.Count == 0)
                    return;

                float rollThroughSpeedMph = managed.WaypointTargetSpeed;
                if (rollThroughSpeedMph <= 0f)
                    return;

                float signedRoll = rollThroughSpeedMph * Mathf.Sign(direction);

                for (int i = 0; i < allTargets.Count; i++)
                {
                    var t = allTargets[i];

                    bool isWaypointManualTarget =
                        (t.Reason == "Running to waypoint" || t.Reason == "At waypoint") &&
                        Mathf.Abs(t.SpeedMph) < 0.01f;

                    if (isWaypointManualTarget)
                    {
                        t.SpeedMph = signedRoll;
                        allTargets[i] = t;
                    }
                }
                targetsField.SetValue(____engineer, targets);

                //DumpTargetsToFile(loco, targets, allTargets);
            }
            catch (Exception ex)
            {
                Loader.Log($"PatchAutoEngineerPlannerUpdateTargets postfix failed: {ex}");
            }
        }

        [HarmonyPatch(typeof(AutoEngineerPlanner))]
        internal static class PatchAutoEngineerPlannerIsWaypointSatisfied
        {

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


                    if (!WaypointQueueController.TryGetActiveWaypointFor(____locomotive, out ManagedWaypoint managed))
                        return;


                    if (managed.WaypointTargetSpeed <= 0f)
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
                        Loader.LogDebug($"[WaypointQueue] High-speed waypoint distance calc failed: {ex.Message}");
                        return;
                    }

                    if (bestDistance <= HighSpeedWaypointRadiusMeters)
                    {
                        __result = true;
                    }
                }
                catch (Exception ex)
                {
                    Loader.Log($"[WaypointQueue] IsWaypointSatisfiedPostfix failed: {ex}");
                }
            }
        }

        private static void DumpTargetsToFile(
            Car loco,
            object targetsObj,
            List<AutoEngineer.Targets.Target> allTargets
        )
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== AutoEngineerPlanner.UpdateTargets POSTFIX DUMP ===");
                sb.AppendLine($"Time: {DateTime.Now:O}");
                sb.AppendLine($"Loco: {(loco != null ? loco.Ident.ToString() : "null")}");
                sb.AppendLine($"Loco ID: {(loco != null ? loco.id.ToString() : "null")}");
                sb.AppendLine($"Targets type: {targetsObj.GetType().FullName}");

                if (allTargets != null && allTargets.Count > 0)
                {
                    for (int i = 0; i < allTargets.Count; i++)
                    {
                        var t = allTargets[i];
                        sb.AppendLine($"[{i}] speed={t.SpeedMph} distance={t.Distance} reason={t.Reason}");
                    }
                }
                else
                {
                    sb.AppendLine("No target list found or empty.");
                }

                var dumpDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "AE_Dumps"
                );
                Directory.CreateDirectory(dumpDir);


                string locoName = loco != null ? loco.Ident.ToString() : "UnknownLoco";
                string safeName = MakeSafeFileName(locoName);

                var fileName = Path.Combine(dumpDir, $"{safeName}.txt");


                File.WriteAllText(fileName, sb.ToString());
            }
            catch (Exception ex)
            {
                Loader.Log($"AE dump write failed: {ex.Message}");
            }
        }

        private static string MakeSafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);

            foreach (char c in name)
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }

            return sb.ToString();
        }
    }
}
