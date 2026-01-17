using Game.Messages;
using HarmonyLib;

namespace WaypointQueue.Patches
{
    [HarmonyPatch(typeof(TrainController))]
    internal class PatchTrainController
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(TrainController.HandleRemoveCars))]
        static bool HandleRemoveCarsPrefix(TrainController __instance, RemoveCars message)
        {
            foreach (var carId in message.CarIds)
            {
                if (WaypointQueueController.Shared.HasWaypointState(carId))
                {
                    WaypointQueueController.Shared.ClearWaypointState(carId);
                }
            }
            return true;
        }
    }
}
