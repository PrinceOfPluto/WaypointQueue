using HarmonyLib;
using Helpers;
using Model;
using System;
using System.Collections.Generic;
using System.Reflection;
using Track;
using UI;
using UnityEngine;
using UnityEngine.Pool;

namespace WaypointQueue
{
    [HarmonyPatch]
    internal class PatchAutoEngineerDestinationPicker
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AutoEngineerDestinationPicker), "HitLocation")]
        static bool HitLocationPrefix(ref object __result, ref Camera ____camera, ref Graph ____graph, ref HashSet<Car> ____dontSnapToCars)
        {
            if (!MainCameraHelper.TryGetIfNeeded(ref ____camera))
            {
                __result = null;
                return false;
            }

            Type destinationPickerType = typeof(AutoEngineerDestinationPicker);
            Type hitType = destinationPickerType.GetNestedType("Hit", BindingFlags.NonPublic);

            ConstructorInfo hitConstructor = hitType.GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                [typeof(Location), typeof((Car, Car.End)?)],
                null);

            Location? location = ____graph.LocationFromMouse(____camera);
            if (location.HasValue)
            {
                Location valueOrDefault = location.GetValueOrDefault();
                TrainController shared = TrainController.Shared;
                Vector3 position = ____graph.GetPosition(valueOrDefault);
                float num = 2f;

                // Instead of assigning result to null by default, assign it to the value
                __result = hitConstructor.Invoke([valueOrDefault, null]);

                using (CollectionPool<HashSet<Car>, Car>.Get(out HashSet<Car> value))
                {
                    shared.CheckForCarsAtPoint(position, 2f, value, valueOrDefault);
                    foreach (Car item in value)
                    {
                        if (____dontSnapToCars.Contains(item))
                        {
                            continue;
                        }

                        if (!item[item.EndToLogical(Car.End.F)].IsCoupled)
                        {
                            Location location2 = ____graph.LocationByMoving(item.LocationF, 0.5f, checkSwitchAgainstMovement: false, stopAtEndOfTrack: true);
                            float distanceBetweenClose = ____graph.GetDistanceBetweenClose(valueOrDefault, location2);
                            if (distanceBetweenClose < num)
                            {
                                num = distanceBetweenClose;
                                __result = hitConstructor.Invoke([location2, (item, Car.End.F)]);
                            }
                        }

                        if (!item[item.EndToLogical(Car.End.R)].IsCoupled)
                        {
                            Location location3 = ____graph.LocationByMoving(item.LocationR, -0.5f, checkSwitchAgainstMovement: false, stopAtEndOfTrack: true).Flipped();
                            float distanceBetweenClose2 = ____graph.GetDistanceBetweenClose(valueOrDefault, location3);
                            if (distanceBetweenClose2 < num)
                            {
                                num = distanceBetweenClose2;
                                __result = hitConstructor.Invoke([location3, (item, Car.End.R)]);
                            }
                        }
                    }

                    if (value.Count > 0)
                    {
                        return false;
                    }
                }

                return false;
            }

            __result = null;
            return false;
        }
    }
}
