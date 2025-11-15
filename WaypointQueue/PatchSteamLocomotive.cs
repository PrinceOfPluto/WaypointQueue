using HarmonyLib;
using Model;
using System;

namespace WaypointQueue
{
    [HarmonyPatch]
    internal class PatchSteamLocomotive
    {
        [HarmonyReversePatch]
        [HarmonyPatch(typeof(SteamLocomotive), "TryGetTender")]
        public static bool TryGetTender(object instance, out Car tender) => throw new NotImplementedException();

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(SteamLocomotive), "FuelCar")]
        public static Car FuelCar(object instance) => throw new NotImplementedException();


    }
}
