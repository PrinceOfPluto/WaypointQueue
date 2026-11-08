using HarmonyLib;
using Model;
using System;
using System.Collections.Generic;
using static Model.Car;

namespace WaypointQueue.Patches
{
    [HarmonyPatch(typeof(Car))]
    internal class PatchCar
    {
        [HarmonyPatch(typeof(Car), nameof(Car.EnumerateCoupled), [typeof(LogicalEnd)])]
        [HarmonyPrefix]
        private static bool EnumerateCoupledPrefix(Car __instance, LogicalEnd fromEnd, ref IEnumerable<Car> __result)
        {
            // Prevent warning log spam for can't enumerate car that has no integration set
            if (__instance.set == null)
            {
                __result = Array.Empty<Car>();
                return false;
            }

            __result = __instance.set.EnumerateCoupledTo(__instance, fromEnd);
            return false;
        }
    }
}
