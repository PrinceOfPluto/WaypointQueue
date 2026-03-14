using Game.Messages;
using Game.State;
using HarmonyLib;
using Model;
using Network;
using System.Collections.Generic;
using System.Linq;
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
                        if (ModStateManager.Shared.LocoIdsWithActiveWaypointQueues.Contains(carId))
                        {
                            ModStateManager.Shared.RemoveLocoWaypointState(carId);
                        }
                    }
                }
            }

            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(TrainController), "HandleCreateCarsAsTrain")]
        static void HandleAddCarsPostfix(TrainController __instance, List<Car> __result)
        {
            if (Multiplayer.IsHost)
            {
                using (StateManager.TransactionScope())
                {
                    foreach (var car in __result)
                    {
                        if (car is BaseLocomotive locomotive)
                        {
                            ModStateManager.Shared.RegisterObserversForLoco(locomotive);
                        }
                    }
                }
            }
        }
    }
}
