using Game.Messages;
using Game.State;
using HarmonyLib;
using Network;
using WaypointQueue.State;

namespace WaypointQueue.Patches
{
    [HarmonyPatch(typeof(TrainController))]
    internal class PatchTrainController
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(TrainController.HandleRemoveCars))]
        static bool HandleRemoveCarsPrefix(TrainController __instance, RemoveCars message)
        {
            if (Multiplayer.IsHost)
            {
                using (StateManager.TransactionScope())
                {
                    foreach (var carId in message.CarIds)
                    {
                        if (ModStateManager.Shared.LocoWaypointStates.ContainsKey(carId))
                        {
                            ModStateManager.Shared.RemoveLocoWaypointState(carId);
                        }
                    }
                }
            }

            return true;
        }
    }
}
