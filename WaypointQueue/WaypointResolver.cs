using Game;
using Game.Messages;
using Game.State;
using Helpers;
using Model;
using Model.Definition.Data;
using Model.Ops;
using Model.Ops.Definition;
using Model.Ops.Timetable;
using RollingStock;
using System;
using System.Collections.Generic;
using System.Linq;
using Track;
using UI.Common;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.UUM;
using static Model.Car;

namespace WaypointQueue
{
    internal static class WaypointResolver
    {
        /**
         * Returns false when the waypoint is not yet resolved (i.e. needs to continue)
         */
        public static bool TryHandleUnresolvedWaypoint(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper, Action onWaypointsUpdated)
        {
            // Loader.LogDebug($"Trying to handle unresolved waypoint for {wp.Locomotive.Ident}:\n {wp.ToString()}");
            if (!wp.StopAtWaypoint)
            {
                // Uncoupling orders are the only orders should get resolved if we are not stopping
                ResolveUncouplingOrders(wp);
                return true;
            }

            // Check if done waiting
            if (wp.CurrentlyWaiting)
            {
                // We don't want to start waiting until after we resolve the current waypoint orders,
                // but we also don't want that resolving logic to run again after we are finished waiting
                return TryEndWaiting(wp);
            }

            // Begin refueling
            if (wp.WillRefuel && !wp.CurrentlyRefueling)
            {
                OrderToRefuel(wp, ordersHelper);
                return false;
            }

            // Check if done refueling
            if (wp.CurrentlyRefueling)
            {
                if (IsDoneRefueling(wp))
                {
                    CleanupAfterRefuel(wp, ordersHelper);
                }
                else
                {
                    //Loader.LogDebug($"Still refueling");
                    return false;
                }
            }

            /*
             * Unless explicitly not stopping, loco needs a complete stop before resolving coupling or uncoupling orders.
             * Otherwise, some cars may be uncoupled and then recoupled if the train still has momentum.
             */
            if (wp.StopAtWaypoint && Math.Abs(wp.Locomotive.velocity) > 0)
            {
                Loader.LogDebug($"Locomotive not stopped, continuing");
                return false;
            }

            ResolveCouplingOrders(wp);

            ResolveUncouplingOrders(wp);

            if (TryBeginWaiting(wp, onWaypointsUpdated))
            {
                return false;
            }

            return true;
        }

        private static bool TryEndWaiting(ManagedWaypoint wp)
        {
            if (TimeWeather.Now.TotalSeconds >= wp.WaitUntilGameTotalSeconds)
            {
                Loader.Log($"Loco {wp.Locomotive.Ident} done waiting");
                wp.ClearWaiting();
                return true;
            }
            //Loader.LogDebug($"Loco {wp.Locomotive.Ident} still waiting");
            return false;
        }

        private static bool TryBeginWaiting(ManagedWaypoint wp, Action onWaypointsUpdated)
        {
            return wp.WillWait && (TryBeginWaitingDuration(wp, onWaypointsUpdated) || TryBeginWaitingUntilTime(wp, onWaypointsUpdated));
        }

        private static bool TryBeginWaitingDuration(ManagedWaypoint wp, Action onWaypointsUpdated)
        {
            if (wp.DurationOrSpecificTime == ManagedWaypoint.WaitType.Duration && wp.WaitForDurationMinutes > 0)
            {
                GameDateTime waitUntilTime = TimeWeather.Now.AddingMinutes(wp.WaitForDurationMinutes);
                wp.WaitUntilGameTotalSeconds = waitUntilTime.TotalSeconds;
                wp.CurrentlyWaiting = true;
                Loader.Log($"Loco {wp.Locomotive.Ident} waiting {wp.WaitForDurationMinutes}m until {waitUntilTime}");
                onWaypointsUpdated?.Invoke();
                return true;
            }
            return false;
        }

        private static bool TryBeginWaitingUntilTime(ManagedWaypoint wp, Action onWaypointsUpdated)
        {
            if (wp.DurationOrSpecificTime == ManagedWaypoint.WaitType.SpecificTime)
            {
                if (TimetableReader.TryParseTime(wp.WaitUntilTimeString, out TimetableTime time))
                {
                    wp.SetWaitUntilByMinutes(time.Minutes, out GameDateTime waitUntilTime);
                    wp.CurrentlyWaiting = true;
                    Loader.Log($"Loco {wp.Locomotive.Ident} waiting until {waitUntilTime}");
                    onWaypointsUpdated?.Invoke();
                    return true;
                }
                else
                {
                    Loader.Log($"Error parsing time: \"{wp.WaitUntilTimeString}\"");
                    Toast.Present("Waypoint wait time must be in HH:MM 24-hour format.");
                    return false;
                }
            }
            return false;
        }

        public static void OrderToRefuel(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.Log($"Beginning order to refuel {waypoint.Locomotive.Ident}");
            waypoint.CurrentlyRefueling = true;
            // maybe in the future, support refueling multiple locomotives if they are MU'd

            waypoint.MaxSpeedAfterRefueling = ordersHelper.Orders.MaxSpeedMph;
            // Set max speed of 5 to help prevent train from overrunning waypoint
            int speedWhileRefueling = 5;
            ordersHelper.SetOrdersValue(null, null, maxSpeedMph: speedWhileRefueling, null, null);

            Location locationToMove = GetRefuelLocation(waypoint, ordersHelper);
            SetCarLoadTargetLoaderCanLoad(waypoint, true);
            WaypointQueueController.Shared.SendToWaypointForRefuel(waypoint, locationToMove, ordersHelper);
        }

        private static void CleanupAfterRefuel(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.Log($"Done refueling {wp.Locomotive.Ident}");
            wp.WillRefuel = false;
            wp.CurrentlyRefueling = false;
            SetCarLoadTargetLoaderCanLoad(wp, false);

            int maxSpeed = wp.MaxSpeedAfterRefueling;
            if (maxSpeed == 0) maxSpeed = 35;
            ordersHelper.SetOrdersValue(null, null, maxSpeedMph: maxSpeed, null, null);
        }

        private static void SetCarLoadTargetLoaderCanLoad(ManagedWaypoint waypoint, bool value)
        {
            CarLoadTargetLoader loaderTarget = FindCarLoadTargetLoader(waypoint);
            if (loaderTarget != null)
            {
                loaderTarget.keyValueObject[loaderTarget.canLoadBoolKey] = value;
            }
        }

        private static CarLoadTargetLoader FindCarLoadTargetLoader(ManagedWaypoint waypoint)
        {
            WaypointQueueController.Shared.InitCarLoaders();
            Vector3 worldPosition = WorldTransformer.GameToWorld(waypoint.RefuelPoint);
            //Loader.LogDebug($"Starting search for target loader matching world point {worldPosition}");
            CarLoadTargetLoader loader = WaypointQueueController.Shared.CarLoadTargetLoaders.Find(l => l.transform.position == worldPosition);
            //Loader.LogDebug($"Found matching {loader.load.name} loader at game point {waypoint.RefuelPoint}");
            return loader;
        }

        private static Location GetRefuelLocation(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.LogDebug($"Finding refuel location");

            Car fuelCar = GetFuelCar((BaseLocomotive)waypoint.Locomotive);

            LoadSlot loadSlot = fuelCar.Definition.LoadSlots.Find(slot => slot.RequiredLoadIdentifier == waypoint.RefuelLoadName);

            waypoint.RefuelMaxCapacity = loadSlot.MaximumCapacity;

            int loadSlotIndex = fuelCar.Definition.LoadSlots.IndexOf(loadSlot);

            List<CarLoadTarget> carLoadTargets = fuelCar.GetComponentsInChildren<CarLoadTarget>().ToList();
            CarLoadTarget loadTarget = carLoadTargets.Find(clt => clt.slotIndex == loadSlotIndex);

            Vector3 loadTargetPosition = GetPositionFromLoadTarget(fuelCar, loadTarget);

            if (!Graph.Shared.TryGetLocationFromGamePoint(waypoint.RefuelPoint, 10f, out Location targetLoaderLocation))
            {
                throw new InvalidOperationException($"Cannot refuel at waypoint, failed to get graph location from refuel game point {waypoint.RefuelPoint}");
            }
            Loader.LogDebug($"Target {waypoint.RefuelLoadName} loader location is {targetLoaderLocation}");

            LogicalEnd closestEnd = ClosestLogicalEndTo(fuelCar, targetLoaderLocation);
            Car nearestCar = fuelCar.EnumerateCoupled(closestEnd).First();
            Location closestTrainEndLocation = nearestCar.LocationFor(closestEnd);
            Loader.LogDebug($"Nearest car to {waypoint.RefuelLoadName} loader is {nearestCar.Ident} at {closestTrainEndLocation} with logical end {(closestEnd == LogicalEnd.A ? "A" : "B")}");

            LogicalEnd furthestEnd = closestEnd == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;
            Car furthestCar = fuelCar.EnumerateCoupled(furthestEnd).First();
            Location furthestTrainEndLocation = nearestCar.LocationFor(furthestEnd);

            // Unclear how accurate this is when the train is on segment of curved track since the distance
            // would be a straight line between the points and wouldn't account for the curve.
            // May have to calculate distances based on the track segments rather than Vector3 points
            float distanceFromEndToSlot = Vector3.Distance(closestTrainEndLocation.GetPosition().ZeroY(), loadTargetPosition.ZeroY());
            Loader.LogDebug($"Distance from end of train to locomotive's {waypoint.RefuelLoadName} slot is {distanceFromEndToSlot}");

            Location orientedTargetLocation = Graph.Shared.LocationOrientedToward(targetLoaderLocation, closestTrainEndLocation);

            // If the end of the train is already past the waypoint, then it would incorrectly orient the distance
            if (IsTargetInMiddle(targetLoaderLocation, closestTrainEndLocation, furthestTrainEndLocation))
            {
                distanceFromEndToSlot = -distanceFromEndToSlot;
            }

            Location locationToMove = Graph.Shared.LocationByMoving(orientedTargetLocation, distanceFromEndToSlot, true, true);

            Loader.LogDebug($"Location to refuel {waypoint.RefuelLoadName} is {locationToMove}");
            return locationToMove;
        }

        private static bool IsTargetInMiddle(Location targetLoaderLocation, Location closestTrainEndLocation, Location furthestTrainEndLocation)
        {
            // If target is in the middle, the distance between either end to the target will always be less than the length from one end to the other
            float distanceCloseToTarget = Vector3.Distance(closestTrainEndLocation.GetPosition().ZeroY(), targetLoaderLocation.GetPosition().ZeroY());

            float distanceFarToTarget = Vector3.Distance(furthestTrainEndLocation.GetPosition().ZeroY(), targetLoaderLocation.GetPosition().ZeroY());

            float distanceEndToEnd = Vector3.Distance(closestTrainEndLocation.GetPosition().ZeroY(), furthestTrainEndLocation.GetPosition().ZeroY());

            if (distanceCloseToTarget < distanceEndToEnd && distanceFarToTarget < distanceEndToEnd)
            {
                return true;
            }
            return false;
        }

        private static Vector3 GetPositionFromLoadTarget(Car fuelCar, CarLoadTarget loadTarget)
        {
            // This logic is based on CarLoadTargetLoader.LoadSlotFromCar
            Matrix4x4 transformMatrix = fuelCar.GetTransformMatrix(Graph.Shared);
            Vector3 point2 = fuelCar.transform.InverseTransformPoint(loadTarget.transform.position);
            Vector3 vector = transformMatrix.MultiplyPoint3x4(point2);
            return vector;
        }

        private static List<string> GetValidLoadsForLoco(BaseLocomotive locomotive)
        {
            if (locomotive.Archetype == Model.Definition.CarArchetype.LocomotiveSteam)
            {
                return new List<string> { "water", "coal" };
            }
            if (locomotive.Archetype == Model.Definition.CarArchetype.LocomotiveDiesel)
            {
                return new List<string> { "diesel-fuel" };
            }
            return null;
        }

        internal static void CheckNearbyFuelLoaders(ManagedWaypoint waypoint)
        {
            WaypointQueueController.Shared.InitCarLoaders();
            List<string> validLoads = GetValidLoadsForLoco((BaseLocomotive)waypoint.Locomotive);
            CarLoadTargetLoader closestLoader = null;
            float shortestDistance = 0;
            Loader.LogDebug($"Checking for nearby fuel loaders");

            foreach (CarLoadTargetLoader targetLoader in WaypointQueueController.Shared.CarLoadTargetLoaders)
            {
                if (!validLoads.Contains(targetLoader.load?.name?.ToLower()))
                {
                    continue;
                }
                //Loader.LogDebug($"Checking if target {targetLoader.load?.name} loader transform is null");

                bool hasLocation = false;
                Location loaderLocation;
                try
                {
                    hasLocation = Graph.Shared.TryGetLocationFromWorldPoint(targetLoader.transform.position, 10f, out loaderLocation);
                }
                catch (NullReferenceException)
                {
                    continue;
                }
                //Loader.LogDebug($"Target {targetLoader.load?.name} loader transform was not null");

                if (hasLocation)
                {
                    float distanceFromWaypointToLoader = Vector3.Distance(waypoint.Location.GetPosition(), loaderLocation.GetPosition());

                    float radiusToSearch = 5f;

                    if (distanceFromWaypointToLoader < radiusToSearch)
                    {
                        Loader.LogDebug($"Found {targetLoader.load.name} loader within {distanceFromWaypointToLoader}");
                        if (closestLoader == null)
                        {
                            shortestDistance = distanceFromWaypointToLoader;
                            closestLoader = targetLoader;
                        }
                        if (distanceFromWaypointToLoader < shortestDistance)
                        {
                            shortestDistance = distanceFromWaypointToLoader;
                            closestLoader = targetLoader;
                        }
                    }
                }
            }

            if (closestLoader != null)
            {
                Loader.Log($"Using {closestLoader.load.name} loader at {closestLoader.transform?.position}");
                Vector3 loaderPosition = WorldTransformer.WorldToGame(closestLoader.transform.position);
                // CarLoadTargetLoader uses game position for loading logic, not graph Location
                waypoint.SerializableRefuelPoint = new SerializableVector3(loaderPosition.x, loaderPosition.y, loaderPosition.z);
                // Water towers will have a null source industry
                waypoint.RefuelIndustryId = closestLoader.sourceIndustry?.identifier;
                waypoint.RefuelLoadName = closestLoader.load.name;
                waypoint.WillRefuel = true;
            }
            else
            {
                Loader.LogDebug($"No result found for fuel loader");
            }
        }

        private static void ResolveWaypointOrders(ManagedWaypoint waypoint)
        {
            Loader.LogDebug($"Resolving loco {waypoint.Locomotive.Ident} waypoint to {waypoint.Location}");

        }

        private static Car GetFuelCar(BaseLocomotive locomotive)
        {
            Car fuelCar = locomotive;
            if (locomotive.Archetype == Model.Definition.CarArchetype.LocomotiveSteam)
            {
                fuelCar = PatchSteamLocomotive.FuelCar(locomotive);
            }
            return fuelCar;
        }

        private static bool IsDoneRefueling(ManagedWaypoint waypoint)
        {
            return IsLocoFull(waypoint) || IsLoaderEmpty(waypoint);
        }

        private static bool IsLocoFull(ManagedWaypoint waypoint)
        {
            Car fuelCar = GetFuelCar((BaseLocomotive)waypoint.Locomotive);
            CarLoadInfo? carLoadInfo = fuelCar.GetLoadInfo(waypoint.RefuelLoadName, out int slotIndex);

            double refillThreshold = 25;
            if (waypoint.RefuelMaxCapacity - carLoadInfo.Value.Quantity < refillThreshold)
            {
                Loader.Log($"Fuel car {fuelCar.Ident} is full");
                return true;
            }
            //Loader.LogDebug($"Loco is not full yet");
            return false;
        }

        private static bool IsLoaderEmpty(ManagedWaypoint waypoint)
        {
            string industryId = waypoint.RefuelIndustryId;
            string loadId = waypoint.RefuelLoadName;

            Industry industry = OpsController.Shared.AllIndustries.ToList().Find(x => x.identifier == industryId);
            if (industry == null)
            {
                // Water towers are unlimited and have a null industry
                return false;
            }

            if (industry.Storage == null)
            {
                Loader.Log($"Storage is null for {industry.name}");
                return true;
            }

            Load matchingLoad = industry.Storage.Loads().ToList().Find(l => l.name == loadId);

            if (matchingLoad == null)
            {
                Loader.Log($"Industry {industry.name} is empty, did not find matching load for {loadId}");
                return true;
            }

            if (industry.Storage.QuantityInStorage(matchingLoad) <= 0f)
            {
                Loader.Log($"Industry {industry.name} is empty, quantity in storage was zero");
                return true;
            }
            return false;
        }

        private static void ResolveCouplingOrders(ManagedWaypoint waypoint)
        {
            if (!waypoint.IsCoupling) return;
            Loader.Log($"Resolving coupling orders for loco {waypoint.Locomotive.Ident}");
            foreach (Car car in waypoint.Locomotive.EnumerateCoupled())
            {
                //Loader.LogDebug($"Resolving coupling orders on {car.Ident}");
                if (waypoint.ConnectAirOnCouple)
                {
                    ConnectAir(car);
                }

                if (waypoint.ReleaseHandbrakesOnCouple)
                {
                    //Loader.LogDebug($"Releasing handbrake on {car.Ident}");
                    car.SetHandbrake(false);
                }
            }

            if (waypoint.NumberOfCarsToCut > 0 && TrainController.Shared.TryGetCarForId(waypoint.CoupleToCarId, out Car carCoupledTo))
            {
                bool isTake = waypoint.TakeOrLeaveCut == ManagedWaypoint.PostCoupleCutType.Take;
                Loader.Log($"Resolving post-coupling cut of type: " + (isTake ? "Take" : "Leave"));
                LogicalEnd closestEnd = ClosestLogicalEndTo(carCoupledTo, waypoint.Location);
                LogicalEnd furthestEnd = FurthestLogicalEndFrom(carCoupledTo, waypoint.Location);

                // If we're taking cars, we are counting cars past the waypoint
                // If we're leaving cars, we are counting cars before the waypoint
                // Direction to count cars is relative to the car coupled to
                LogicalEnd directionToCountCars = isTake ? furthestEnd : closestEnd;
                LogicalEnd oppositeDirection = directionToCountCars == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;

                List<Car> coupledToOriginal = EnumerateCoupledToEnd(carCoupledTo, directionToCountCars);

                // If we are taking, then we need to include original at index 0
                if (isTake)
                {
                    coupledToOriginal.Insert(0, carCoupledTo);
                }

                int clampedNumberOfCarsToCut = Mathf.Clamp(waypoint.NumberOfCarsToCut, 0, coupledToOriginal.Count);

                Loader.Log($"Cars coupled to original coupled car: " + String.Join("-", coupledToOriginal.Select(c => $"[{c.Ident}]")));
                Car targetCar = coupledToOriginal.ElementAt(clampedNumberOfCarsToCut - 1);
                Loader.Log($"Target car is {targetCar.Ident}");

                List<Car> carsLeftBehind = [];
                if (!isTake)
                {
                    carsLeftBehind.Add(targetCar);
                }

                // This is always furthest end because it is still relative to the original car coupled to
                // On takes, we add the remaining cars further from the waypoint than the target car
                // On leaves, we find our target car by stepping through the end closest to the waypoint,
                // but then have to reverse direction to furthest end in order to add the rest of the cut
                carsLeftBehind.AddRange(EnumerateCoupledToEnd(targetCar, furthestEnd));

                carsLeftBehind = FilterAnySplitLocoTenderPairs(carsLeftBehind);

                Loader.Log("Post-couple cutting " + String.Join("-", carsLeftBehind.Select(c => $"[{c.Ident}]")) + " from " + String.Join("-", waypoint.Locomotive.EnumerateCoupled(directionToCountCars).Select(c => $"[{c.Ident}]")));

                // Only apply handbrakes and bleed air on cars we leave behind
                if (waypoint.ApplyHandbrakesOnUncouple)
                {
                    SetHandbrakes(carsLeftBehind);
                }

                Car carToUncouple = carsLeftBehind.FirstOrDefault();

                if (carToUncouple != null)
                {
                    Loader.Log($"Uncoupling {carToUncouple.Ident} for cut of {clampedNumberOfCarsToCut} cars with {carsLeftBehind.Count} total left behind ");
                    UncoupleCar(carToUncouple, closestEnd);

                    if (waypoint.BleedAirOnUncouple)
                    {
                        Loader.LogDebug($"Bleeding air on {carsLeftBehind.Count} cars");
                        foreach (Car car in carsLeftBehind)
                        {
                            car.air.BleedBrakeCylinder();
                        }
                    }
                }
            }
        }

        private static List<Car> EnumerateCoupledToEnd(Car car, LogicalEnd directionToCount)
        {
            List<Car> result = [];
            Car currentCar = car;
            while (currentCar.TryGetAdjacentCar(directionToCount, out Car nextCar))
            {
                result.Add(nextCar);
                currentCar = nextCar;
            }
            return result;
        }

        private static void ConnectAir(Car car)
        {
            if (StateManager.IsHost && car.set != null)
            {
                if (car.TryGetAdjacentCar(LogicalEnd.A, out var adjacent))
                {
                    //Loader.LogDebug($"Connecting air from {car.Ident} to {adjacent.Ident}");
                    adjacent.ApplyEndGearChange(LogicalEnd.B, EndGearStateKey.IsAirConnected, boolValue: true);
                    adjacent.ApplyEndGearChange(LogicalEnd.B, EndGearStateKey.Anglecock, f: 1.0f);
                }
                else
                {
                    // Close anglecock if not adjacent to a car
                    Loader.LogDebug($"Closing air on {car.Ident} end {LogicalEnd.A} without adjacent car");
                    car.ApplyEndGearChange(LogicalEnd.A, EndGearStateKey.Anglecock, f: 0.0f);
                }

                if (car.TryGetAdjacentCar(LogicalEnd.B, out var adjacent2))
                {
                    //Loader.LogDebug($"Connecting air from {car.Ident} to {adjacent2.Ident}");
                    adjacent2.ApplyEndGearChange(LogicalEnd.A, EndGearStateKey.IsAirConnected, boolValue: true);
                    adjacent2.ApplyEndGearChange(LogicalEnd.A, EndGearStateKey.Anglecock, f: 1.0f);
                }
                else
                {
                    // Close anglecock if not adjacent to a car
                    Loader.LogDebug($"Closing air on {car.Ident} end {LogicalEnd.B} without adjacent car");
                    car.ApplyEndGearChange(LogicalEnd.B, EndGearStateKey.Anglecock, f: 0.0f);
                }
            }
        }


        private static bool TryResolveUncoupleAll(
            ManagedWaypoint waypoint,
            out List<Car> allCarsFromSide,
            out List<Car> carsToCut,
            out Car seamFrontCar,
            out Car seamBackCar)
        {
            allCarsFromSide = null;
            carsToCut = null;
            seamFrontCar = null;
            seamBackCar = null;

            var loco = waypoint.Locomotive;
            if (loco == null)
            {
                Loader.Log("ResolveUncoupleAll: Locomotive is null, aborting.");
                return false;
            }


            End physicalEnd = waypoint.UncoupleAllDirectionSide == ManagedWaypoint.UncoupleAllDirection.Aft
                ? End.R   
                : End.F;  

            LogicalEnd fromEnd = loco.EndToLogical(physicalEnd);


            allCarsFromSide = EnumerateCoupledToEnd(loco, fromEnd);
            if (allCarsFromSide == null || allCarsFromSide.Count == 0)
            {
                Loader.Log("ResolveUncoupleAll: no cars on selected side; nothing to cut.");
                return false;
            }


            int firstNonPowerIndex = -1;
            for (int i = 0; i < allCarsFromSide.Count; i++)
            {
                if (!IsLocoOrTender(allCarsFromSide[i]))
                {
                    firstNonPowerIndex = i;
                    break;
                }
            }

            if (firstNonPowerIndex == -1)
            {
                Loader.Log("ResolveUncoupleAll: no non-loco/tender cars found on this side; nothing to cut.");
                return false;
            }


            int countToCut = allCarsFromSide.Count - firstNonPowerIndex;
            if (countToCut <= 0)
            {
                Loader.Log("ResolveUncoupleAll: computed empty cut; nothing to uncouple.");
                return false;
            }

            carsToCut = allCarsFromSide.GetRange(firstNonPowerIndex, countToCut);


            seamBackCar = carsToCut[0];
            seamFrontCar = (firstNonPowerIndex > 0)
                ? allCarsFromSide[firstNonPowerIndex - 1]
                : loco;

            return true;
        }



        private static void ResolveUncouplingOrders(ManagedWaypoint waypoint)
        {
            if (!waypoint.IsUncoupling) return;
            if (waypoint.Locomotive == null) return;

            Loader.Log($"Resolving uncoupling orders for {waypoint.Locomotive.Ident}");

            
            switch (waypoint.UncoupleByMode)
            {
                case ManagedWaypoint.UncoupleMode.ByDestination:
                    {
                        if (TryResolveUncoupleByDestination(
                                waypoint,
                                out Car carToUncouple,
                                out LogicalEnd endToUncouple,
                                out List<Car> inactiveCut))
                        {
                            Loader.Log(
                                $"UncoupleByDestination: Will uncouple {carToUncouple.Ident} on end {endToUncouple} " +
                                $"for destination '{waypoint.UncoupleDestinationId}' with {inactiveCut.Count} cars in the dropped cut");

                            if (waypoint.ApplyHandbrakesOnUncouple)
                            {
                                SetHandbrakes(inactiveCut);
                            }

                            Loader.Log($"Uncoupling {carToUncouple.Ident} on end {endToUncouple} for cut of {inactiveCut.Count} cars");
                            UncoupleCar(carToUncouple, endToUncouple);

                            if (waypoint.BleedAirOnUncouple)
                            {
                                Loader.LogDebug($"Bleeding air on {inactiveCut.Count} cars");
                                foreach (Car car in inactiveCut)
                                {
                                    car.air.BleedBrakeCylinder();
                                }
                            }
                        }
                        else
                        {
                            Loader.Log("UncoupleByDestination: nothing to uncouple (no valid block / safe seam).");
                        }

                        return;
                    }

                case ManagedWaypoint.UncoupleMode.All:
                case ManagedWaypoint.UncoupleMode.ByCount:
                default:
                    break;
            }

            List<Car> allCarsFromEnd;
            List<Car> carsToCut = null;

            Car seamFrontCarAll = null;
            Car seamBackCarAll = null;

            
            if (waypoint.UncoupleByMode == ManagedWaypoint.UncoupleMode.ByCount)
            {
                LogicalEnd directionToCountCars =
                    GetEndRelativeToWapoint(
                        waypoint.Locomotive,
                        waypoint.Location,
                        useFurthestEnd: !waypoint.CountUncoupledFromNearestToWaypoint);

                allCarsFromEnd = waypoint.Locomotive.EnumerateCoupled(directionToCountCars).ToList();
                carsToCut = allCarsFromEnd.Take(waypoint.NumberOfCarsToCut).ToList();

                
                if (carsToCut.Count == 0 && allCarsFromEnd.Count > 1)
                {
                    Car maybeLocoOrTenderA = allCarsFromEnd.ElementAtOrDefault(0);
                    Car maybeLocoOrTenderB = allCarsFromEnd.ElementAtOrDefault(1);
                    Car tender;

                    if (maybeLocoOrTenderA.Archetype == Model.Definition.CarArchetype.LocomotiveSteam &&
                        PatchSteamLocomotive.TryGetTender(maybeLocoOrTenderA, out tender) &&
                        tender.id == maybeLocoOrTenderB.id)
                    {
                        carsToCut.Add(maybeLocoOrTenderA);
                        carsToCut.Add(maybeLocoOrTenderB);
                    }
                    else if (maybeLocoOrTenderB.Archetype == Model.Definition.CarArchetype.LocomotiveSteam &&
                             PatchSteamLocomotive.TryGetTender(maybeLocoOrTenderB, out tender) &&
                             tender.id == maybeLocoOrTenderA.id)
                    {
                        carsToCut.Add(maybeLocoOrTenderA);
                        carsToCut.Add(maybeLocoOrTenderB);
                    }
                }
            }
            else 
            {
                if (!TryResolveUncoupleAll(
                        waypoint,
                        out allCarsFromEnd,  
                        out carsToCut,       
                        out seamFrontCarAll, 
                        out seamBackCarAll)) 
                {
                    
                    return;
                }
            }

            if (carsToCut == null)
            {
                Loader.Log("ResolveUncouplingOrders: carsToCut is null, aborting.");
                return;
            }

            if (carsToCut.Count == 0)
            {
                Loader.Log("No valid cars to cut found for uncoupling");
                return;
            }

            List<Car> carsRemaining = allCarsFromEnd.Where(c => !carsToCut.Contains(c)).ToList();

            bool allowActiveCutSwap = waypoint.UncoupleByMode == ManagedWaypoint.UncoupleMode.ByCount;

            List<Car> activeCut = carsRemaining;
            List<Car> inactiveCut2 = carsToCut;

            Loader.Log($"Seeking to uncouple {carsToCut.Count} cars from train of {allCarsFromEnd.Count} total cars with {carsRemaining.Count} cars left behind");
            Loader.Log($"TakeUncoupledCarsAsActiveCut is {waypoint.TakeUncoupledCarsAsActiveCut}");

            if (allowActiveCutSwap && waypoint.TakeUncoupledCarsAsActiveCut)
            {
                activeCut = carsToCut;
                inactiveCut2 = carsRemaining;
            }

            string carsToCutFormatted = String.Join("-", inactiveCut2.Select(c => $"[{c.Ident}]"));

            
            var fullTrain = waypoint.Locomotive
                .EnumerateCoupled()
                .Prepend(waypoint.Locomotive)
                .ToList();

            string fullTrainFormatted = String.Join("-", fullTrain.Select(c => $"[{c.Ident}]"));

            Loader.Log(
                $"Cutting {carsToCutFormatted} from {fullTrainFormatted} as " +
                $"{(allowActiveCutSwap && waypoint.TakeUncoupledCarsAsActiveCut ? "active cut" : "inactive cut")}");

            Car carToUncouple2 = null;
            LogicalEnd endToUncouple2 = LogicalEnd.A;

            if (waypoint.UncoupleByMode == ManagedWaypoint.UncoupleMode.All)
            {
                if (seamBackCarAll == null)
                {
                    Loader.Log("ResolveUncouplingOrders: [All] seamBackCar is null; aborting.");
                    return;
                }

                Car frontCar = seamFrontCarAll ?? waypoint.Locomotive;

                if (!TryFindEndConnectingTo(seamBackCarAll, frontCar, out endToUncouple2))
                {
                    Loader.Log("ResolveUncouplingOrders: [All] couldn't find adjacency between seam cars; aborting.");
                    return;
                }

                carToUncouple2 = seamBackCarAll;
            }
            else
            {
                var activeSet = new HashSet<Car>(activeCut);

                foreach (Car candidate in inactiveCut2)
                {
                    foreach (LogicalEnd end in new[] { LogicalEnd.A, LogicalEnd.B })
                    {
                        if (candidate.TryGetAdjacentCar(end, out Car adjacent) && activeSet.Contains(adjacent))
                        {
                            carToUncouple2 = candidate;
                            endToUncouple2 = end;
                            break;
                        }
                    }

                    if (carToUncouple2 != null)
                        break;
                }

                if (carToUncouple2 == null)
                {
                    Loader.Log("Failed to find a boundary between cars to cut and cars to keep; aborting uncouple.");
                    return;
                }
            }

            if (waypoint.ApplyHandbrakesOnUncouple)
            {
                SetHandbrakes(inactiveCut2);
            }

            Loader.Log($"Uncoupling {carToUncouple2.Ident} on end {endToUncouple2} for cut of {inactiveCut2.Count} cars");
            UncoupleCar(carToUncouple2, endToUncouple2);

            if (waypoint.BleedAirOnUncouple)
            {
                Loader.LogDebug($"Bleeding air on {inactiveCut2.Count} cars");
                foreach (Car car in inactiveCut2)
                {
                    car.air.BleedBrakeCylinder();
                }
            }
        }

        private static List<Car> FilterAnySplitLocoTenderPairs(List<Car> carsToCut)
        {
            if (carsToCut.Count == 0)
            {
                return carsToCut;
            }
            // Check first and last to make sure we aren't splitting a loco and tender
            List<Car> firstAndLastCars = [carsToCut.FirstOrDefault(), carsToCut.LastOrDefault()];
            foreach (Car car in firstAndLastCars)
            {
                if (car.Archetype == Model.Definition.CarArchetype.LocomotiveSteam && PatchSteamLocomotive.TryGetTender(car, out Car tender) && !carsToCut.Any(c => c.id == tender.id))
                {
                    // locomotive in cut without tender
                    carsToCut.Remove(car);
                }
                else if (car.Archetype == Model.Definition.CarArchetype.Tender && car.TryGetAdjacentCar(car.EndToLogical(End.F), out Car parentLoco) && !carsToCut.Any(c => c.id == parentLoco.id))
                {
                    // tender in cut without locomotive
                    carsToCut.Remove(car);
                }
            }
            return carsToCut;
        }

        private static LogicalEnd ClosestLogicalEndTo(Car car, Location location)
        {
            return car.ClosestLogicalEndTo(location, Graph.Shared);
        }

        private static LogicalEnd FurthestLogicalEndFrom(Car car, Location location)
        {
            LogicalEnd closestEnd = ClosestLogicalEndTo(car, location);
            LogicalEnd furthestEnd = closestEnd == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;
            return furthestEnd;
        }

        private static LogicalEnd GetEndRelativeToWapoint(Car car, Location waypointLocation, bool useFurthestEnd)
        {
            LogicalEnd closestEnd = car.ClosestLogicalEndTo(waypointLocation, Graph.Shared);
            LogicalEnd furthestEnd = closestEnd == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;
            return useFurthestEnd ? furthestEnd : closestEnd;
        }

        private static void UncoupleCar(Car car, LogicalEnd endToUncouple)
        {
            LogicalEnd oppositeEnd = endToUncouple == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;

            if (StateManager.IsHost && car.set != null)
            {
                if (car.TryGetAdjacentCar(endToUncouple, out var adjacent))
                {
                    // Close anglecocks on both sides to simplify uncoupling. Bleeding air is already a separate option
                    car.ApplyEndGearChange(endToUncouple, EndGearStateKey.Anglecock, f: 0f);
                    car.ApplyEndGearChange(endToUncouple, EndGearStateKey.IsCoupled, boolValue: false);
                    car.ApplyEndGearChange(endToUncouple, EndGearStateKey.IsAirConnected, boolValue: false);

                    adjacent.ApplyEndGearChange(oppositeEnd, EndGearStateKey.Anglecock, f: 0f);
                    adjacent.ApplyEndGearChange(oppositeEnd, EndGearStateKey.IsCoupled, boolValue: false);
                    adjacent.ApplyEndGearChange(oppositeEnd, EndGearStateKey.IsAirConnected, boolValue: false);
                }
            }
        }

        private static void SetHandbrakes(List<Car> cars)
        {
            if (cars.Count == 0)
            {
                return;
            }
            float handbrakePercentage = Loader.Settings.HandbrakePercentOnUncouple;
            float minimum = Loader.Settings.MinimumHandbrakesOnUncouple;

            int carsToTieDown = (int)Math.Max(minimum, Math.Ceiling(cars.Count * handbrakePercentage));
            if (carsToTieDown > cars.Count) carsToTieDown = cars.Count;

            Loader.Log($"Setting handbrakes on {carsToTieDown} uncoupled cars");
            for (int i = 0; i < carsToTieDown; i++)
            {
                //if (cars[i].Archetype.IsLocomotive())
                //{
                //    carsToTieDown++;
                //    continue;
                //}

                cars[i].SetHandbrake(true);
            }
        }

        internal static void ApplyTimetableSymbolIfRequested(ManagedWaypoint waypoint)
        {
            if (waypoint.TimetableSymbol == null) return;

            string valueToSet = string.IsNullOrEmpty(waypoint.TimetableSymbol) ? null : waypoint.TimetableSymbol;

            string crewId = waypoint.Locomotive?.trainCrewId;

            if (string.IsNullOrEmpty(crewId))
            {
                var ident = (waypoint.Locomotive != null)
                    ? waypoint.Locomotive.Ident.ToString()
                    : "This locomotive";

                ModalAlertController.Present(
                    "No crew assigned to train",
                    $"{ident} has no crew. Assign a crew to use timetable symbols.",
                    new (bool, string)[] { (true, "OK") },
                    _ => { }
                );
                return;
            }

            StateManager.ApplyLocal(new RequestSetTrainCrewTimetableSymbol(crewId, valueToSet));
            Loader.Log($"[Timetable] {(valueToSet ?? "None")} for {waypoint.Locomotive.Ident}");
        }

        private static bool IsLocoOrTender(Car car)
        {
            switch (car.Archetype)
            {
                case Model.Definition.CarArchetype.LocomotiveDiesel:
                case Model.Definition.CarArchetype.LocomotiveSteam:
                case Model.Definition.CarArchetype.Tender:
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryFindDestinationBlockOnSide(
            List<Car> sideCars,
            string destinationKey,
            out int startIndex,
            out int endIndex)
        {
            startIndex = -1;
            endIndex = -1;

            if (sideCars == null || sideCars.Count == 0 || string.IsNullOrEmpty(destinationKey))
                return false;

            for (int i = 0; i < sideCars.Count; i++)
            {
                Car car = sideCars[i];
                if (!car.Waybill.HasValue)
                {
                    if (startIndex != -1)
                        break;
                    continue;
                }

                var wb = car.Waybill.Value;
                string baseKey = GetDestinationBaseKeyFromWaybill(wb);

                if (!string.Equals(baseKey, destinationKey, StringComparison.Ordinal))
                {
                    if (startIndex != -1)
                        break; 
                    continue;
                }

                if (startIndex == -1)
                    startIndex = i;

                endIndex = i;
            }

            return startIndex != -1 && endIndex >= startIndex;
        }


        private static bool TryFindEndConnectingTo(Car car, Car adjacent, out LogicalEnd end)
        {
            foreach (LogicalEnd candidate in new[] { LogicalEnd.A, LogicalEnd.B })
            {
                if (car.TryGetAdjacentCar(candidate, out Car test) && test == adjacent)
                {
                    end = candidate;
                    return true;
                }
            }

            end = LogicalEnd.A;
            return false;
        }

        private static bool WouldSplitLocoTenderPair(Car carA, Car carB)
        {
            if (carA == null || carB == null)
                return false;

            bool adjacent = false;
            if (carA.TryGetAdjacentCar(LogicalEnd.A, out var adjA) && adjA == carB)
                adjacent = true;
            else if (carA.TryGetAdjacentCar(LogicalEnd.B, out adjA) && adjA == carB)
                adjacent = true;

            if (!adjacent)
                return false;

            // Check for loco → tender adjacency
            if (carA.Archetype == Model.Definition.CarArchetype.LocomotiveSteam &&
                PatchSteamLocomotive.TryGetTender(carA, out Car tender) &&
                tender == carB)
                return true;

            if (carB.Archetype == Model.Definition.CarArchetype.LocomotiveSteam &&
                PatchSteamLocomotive.TryGetTender(carB, out tender) &&
                tender == carA)
                return true;

            // Check tender → loco adjacency via "F" end
            if (carA.Archetype == Model.Definition.CarArchetype.Tender &&
                carA.TryGetAdjacentCar(carA.EndToLogical(End.F), out Car parentLoco) &&
                parentLoco == carB)
                return true;

            if (carB.Archetype == Model.Definition.CarArchetype.Tender &&
                carB.TryGetAdjacentCar(carB.EndToLogical(End.F), out parentLoco) &&
                parentLoco == carA)
                return true;

            return false;
        }

        private static bool TryResolveUncoupleByDestination(
            ManagedWaypoint waypoint,
            out Car carToUncouple,
            out LogicalEnd endToUncouple,
            out List<Car> inactiveCut)
        {
            carToUncouple = null;
            endToUncouple = LogicalEnd.A;
            inactiveCut = null;

            if (string.IsNullOrEmpty(waypoint.UncoupleDestinationId))
            {
                Loader.Log("ResolveUncoupleByDestination: no destination selected.");
                return false;
            }

            string destKey = waypoint.UncoupleDestinationId;   

            List<Car> sideA = EnumerateCoupledToEnd(waypoint.Locomotive, LogicalEnd.A);
            List<Car> sideB = EnumerateCoupledToEnd(waypoint.Locomotive, LogicalEnd.B);

            (List<Car> list, LogicalEnd side, int start, int end)? best = null;

            void ConsiderSide(List<Car> list, LogicalEnd side)
            {
                if (list == null || list.Count == 0) return;

                if (TryFindDestinationBlockOnSide(list, destKey, out int startIndex, out int endIndex))
                {
                    if (best == null || startIndex < best.Value.start)
                    {
                        best = (list, side, startIndex, endIndex);
                    }
                }
            }

            ConsiderSide(sideA, LogicalEnd.A);
            ConsiderSide(sideB, LogicalEnd.B);

            if (best == null)
            {
                Loader.Log($"ResolveUncoupleByDestination: no cars found for destination key='{destKey}'.");
                return false;
            }

            var (carsOnSide, workingSide, startIndex, endIndex) = best.Value;

            Loader.Log(
                $"ResolveUncoupleByDestination: key='{destKey}', side={workingSide}, " +
                $"block indices [{startIndex}, {endIndex}]");


            var dropList = new List<Car>();
            Car seamFrontCar = null; 
            Car seamBackCar = null; 

            if (!waypoint.KeepDestinationString)
            {
                
                int dropStart = startIndex;
                seamBackCar = carsOnSide[dropStart];
                seamFrontCar = dropStart == 0 ? null : carsOnSide[dropStart - 1];

                for (int i = dropStart; i < carsOnSide.Count; i++)
                    dropList.Add(carsOnSide[i]);
            }
            else
            {
                
                if (endIndex >= carsOnSide.Count - 1)
                {
                    Loader.Log("ResolveUncoupleByDestination: destination block is at the end; nothing to cut after it.");
                    return false;
                }

                int dropStart = endIndex + 1;
                seamFrontCar = carsOnSide[endIndex];
                seamBackCar = carsOnSide[dropStart];

                for (int i = dropStart; i < carsOnSide.Count; i++)
                    dropList.Add(carsOnSide[i]);
            }

            if (dropList.Count == 0)
            {
                Loader.Log("ResolveUncoupleByDestination: dropList is empty; nothing to uncouple.");
                return false;
            }

            if (WouldSplitLocoTenderPair(seamFrontCar, seamBackCar))
            {
                Loader.Log("ResolveUncoupleByDestination: seam would split a loco/tender pair; aborting.");
                return false;
            }

            
            if (seamFrontCar != null)
            {
                if (!TryFindEndConnectingTo(seamBackCar, seamFrontCar, out endToUncouple))
                {
                    Loader.Log("ResolveUncoupleByDestination: couldn't find adjacency between seam cars; aborting.");
                    return false;
                }
                carToUncouple = seamBackCar;
            }
            else
            {
                if (!TryFindEndConnectingTo(seamBackCar, waypoint.Locomotive, out endToUncouple))
                {
                    Loader.Log("ResolveUncoupleByDestination: couldn't find adjacency between first drop car and loco; aborting.");
                    return false;
                }
                carToUncouple = seamBackCar;
            }

            inactiveCut = dropList;
            return true;
        }

        private static string GetDestinationBaseKeyFromWaybill(Waybill wb)
        {
            string raw = wb.Destination.ToString();
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            int slashIndex = raw.IndexOf('/');
            string basePart = (slashIndex >= 0 ? raw.Substring(0, slashIndex) : raw).Trim();
            return string.IsNullOrWhiteSpace(basePart) ? null : basePart;
        }

    }
}
