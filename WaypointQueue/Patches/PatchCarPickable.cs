using HarmonyLib;
using Model;
using RollingStock;

namespace WaypointQueue
{
    [HarmonyPatch]
    internal class PatchCarPickable
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CarPickable), nameof(CarPickable.Activate))]
        static bool Activate(PickableActivateEvent evt, ref Car ___car)
        {
            if (!WaypointCarPicker.Shared.IsListeningForCarClick)
            {
                return true;
            }

            if (evt.Activation == PickableActivation.Primary || evt.Activation == PickableActivation.Secondary)
            {
                WaypointCarPicker.Shared.PickCar(___car);
            }

            return true;
        }
    }
}
