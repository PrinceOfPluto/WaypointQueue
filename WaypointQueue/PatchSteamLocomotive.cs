using HarmonyLib;
using Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Model.Car;

namespace WaypointQueue
{
    [HarmonyPatch]
    internal class PatchSteamLocomotive
    {
        [HarmonyReversePatch]
        [HarmonyPatch(typeof(SteamLocomotive), "TryGetTender")]
        public static bool TryGetTender(object instance, out Car tender) => throw new NotImplementedException();
    }
}
