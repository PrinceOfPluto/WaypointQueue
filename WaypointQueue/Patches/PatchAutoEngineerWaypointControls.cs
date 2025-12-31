using HarmonyLib;
using UI.EngineControls;

namespace WaypointQueue
{
    [HarmonyPatch(typeof(AutoEngineerWaypointControls))]
    internal class PatchAutoEngineerWaypointControls
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(AutoEngineerWaypointControls.DidClickStop))]
        static void DidClickStopPostfix(AutoEngineerWaypointControls __instance)
        {
            if (TrainController.Shared.SelectedLocomotive != null)
            {
                WaypointQueueController.Shared.RemoveCurrentWaypoint(TrainController.Shared.SelectedLocomotive);
            }
        }
    }
}
