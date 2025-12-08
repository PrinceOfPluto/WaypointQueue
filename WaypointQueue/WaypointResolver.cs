using Game;
using Game.Messages;
using Game.State;
using HarmonyLib;
using Helpers;
using Model;
using Model.AI;
using Model.Definition.Data;
using Model.Ops;
using Model.Ops.Definition;
using Model.Ops.Timetable;
using RollingStock;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Track;
using Track.Search;
using UI.Common;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.UUM;
using static Model.Car;

namespace WaypointQueue
{
    internal static class WaypointResolver
    {
        private static readonly float WaitBeforeCuttingTimeout = 5f;

        /**
         * Returns false when the waypoint is not yet resolved (i.e. needs to continue)
         */
        public static bool TryHandleUnresolvedWaypoint(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper, Action<ManagedWaypoint> onWaypointDidUpdate)
        {
            // Loader.LogDebug($"Trying to handle unresolved waypoint for {wp.Locomotive.Ident}:\n {wp.ToString()}");
            if (!wp.StopAtWaypoint)
            {
                if (wp.MoveTrainPastWaypoint)
                {
                    if (OrderClearBeyondWaypoint(wp, ordersHelper))
                    {
                        return false;
                    }
                    else
                    {
                        Loader.Log($"Failed to move {wp.Locomotive.Ident} past the waypoint");
                    }
                }

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

            if (wp.MoveTrainPastWaypoint)
            {
                if (OrderClearBeyondWaypoint(wp, ordersHelper))
                {
                    return false;
                }
                else
                {
                    Loader.Log($"Failed to move {wp.Locomotive.Ident} past the waypoint");
                }
            }

            // Try to begin nearby coupling
            if (wp.SeekNearbyCoupling && !wp.CurrentlyCouplingNearby)
            {
                if (FindNearbyCoupling(wp, ordersHelper))
                {
                    return false;
                }
                else
                {
                    wp.SeekNearbyCoupling = false;
                    Toast.Present($"{wp.Locomotive.Ident} cannot find a nearby car to couple.");
                }
            }

            // Connecting air and releasing handbrakes may be done before being completely stopped
            if (wp.IsCoupling && !wp.CurrentlyRefueling && wp.TryResolveCoupleToCar(out Car _))
            {
                ResolveBrakeSystemOnCouple(wp);
            }

            try
            {
                if (!TryResolveFuelingOrders(wp, ordersHelper))
                {
                    return false;
                }
            }
            catch (Exception e)
            {
                Loader.LogError(e.Message);
                string errorModalTitle = "Refueling Error";
                string errorModalMessage = $"Waypoint Queue encountered an unexpected error and cannot refuel locomotive {wp.Locomotive.Ident}.";
                Loader.ShowErrorModal(errorModalTitle, errorModalMessage);
                wp.WillRefuel = false;
                wp.CurrentlyRefueling = false;
                WaypointQueueController.Shared.UpdateWaypoint(wp);
            }

            /*
             * Unless explicitly not stopping, loco needs a complete stop before resolving orders that would uncouple.
             * Otherwise, some cars may be uncoupled and then recoupled if the train still has momentum.
             */
            if (wp.StopAtWaypoint && !IsTrainStopped(wp) && wp.NumberOfCarsToCut > 0 && wp.SecondsSpentWaitingBeforeCut < WaitBeforeCuttingTimeout)
            {
                if (!wp.CurrentlyWaitingBeforeCutting)
                {
                    Loader.Log($"{wp.Locomotive.Ident} is waiting until train is at rest to resolve cut orders");
                    wp.CurrentlyWaitingBeforeCutting = true;
                    wp.StatusLabel = $"Waiting until train is at rest before cutting cars";
                    WaypointQueueController.Shared.UpdateWaypoint(wp);
                }

                if (Mathf.Floor(wp.Locomotive.VelocityMphAbs) == 0)
                {
                    wp.SecondsSpentWaitingBeforeCut += WaypointQueueController.WaypointTickInterval;
                }

                if (wp.SecondsSpentWaitingBeforeCut < WaitBeforeCuttingTimeout)
                {
                    return false;
                }
                else
                {
                    Loader.Log($"{wp.Locomotive.Ident} proceeding with cut after waiting {WaitBeforeCuttingTimeout} seconds from zero absolute velocity floor");
                }
            }

            if (wp.IsCoupling && wp.TryResolveCoupleToCar(out Car _))
            {
                ResolvePostCouplingCut(wp);
            }

            if (wp.IsUncoupling)
            {
                ResolveUncouplingOrders(wp);
            }

            if (TryBeginWaiting(wp, onWaypointDidUpdate))
            {
                wp.StatusLabel = "Waiting before continuing";
                WaypointQueueController.Shared.UpdateWaypoint(wp);
                return false;
            }

            return true;
        }

        public static bool CleanupBeforeRemovingWaypoint(ManagedWaypoint wp)
        {
            if (wp.RefuelLoaderAnimated)
            {
                SetCarLoaderSequencerWantsLoading(wp, false);
            }
            return true;
        }

        private static bool TryResolveFuelingOrders(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper)
        {
            // Reposition to refuel
            if (wp.WillRefuel && !wp.CurrentlyRefueling && !wp.IsCoupling && !wp.SeekNearbyCoupling && !wp.MoveTrainPastWaypoint)
            {
                OrderToRefuel(wp, ordersHelper);
                return false;
            }

            // Begin refueling with animations
            if (wp.CurrentlyRefueling && !wp.RefuelLoaderAnimated && Mathf.Floor(wp.Locomotive.VelocityMphAbs) == 0)
            {
                SetCarLoaderSequencerWantsLoading(wp, true);
                wp.RefuelLoaderAnimated = true;
                wp.StatusLabel = $"Refueling {wp.RefuelLoadName}";
                WaypointQueueController.Shared.UpdateWaypoint(wp);
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

            return true;
        }

        private static bool IsTrainStopped(ManagedWaypoint wp)
        {
            List<Car> coupled = [.. wp.Locomotive.EnumerateCoupled()];
            Car firstCar = coupled.First();
            Car lastCar = coupled.Last();
            Loader.LogDebug($"First car {firstCar.Ident} is {(firstCar.IsStopped(2) ? "stopped for 2" : "NOT stopped")} and last car {lastCar.Ident} is {(lastCar.IsStopped(2) ? "stopped for 2" : "NOT stopped")}");

            return firstCar.IsStopped(2) && lastCar.IsStopped(2);
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

        private static bool TryBeginWaiting(ManagedWaypoint wp, Action<ManagedWaypoint> onWaypointsUpdated)
        {
            return wp.WillWait && (TryBeginWaitingDuration(wp, onWaypointsUpdated) || TryBeginWaitingUntilTime(wp, onWaypointsUpdated));
        }

        private static bool TryBeginWaitingDuration(ManagedWaypoint wp, Action<ManagedWaypoint> onWaypointsUpdated)
        {
            if (wp.DurationOrSpecificTime == ManagedWaypoint.WaitType.Duration && wp.WaitForDurationMinutes > 0)
            {
                GameDateTime waitUntilTime = TimeWeather.Now.AddingMinutes(wp.WaitForDurationMinutes);
                wp.WaitUntilGameTotalSeconds = waitUntilTime.TotalSeconds;
                wp.CurrentlyWaiting = true;
                Loader.Log($"Loco {wp.Locomotive.Ident} waiting {wp.WaitForDurationMinutes}m until {waitUntilTime}");
                onWaypointsUpdated.Invoke(wp);
                return true;
            }
            return false;
        }

        private static bool TryBeginWaitingUntilTime(ManagedWaypoint wp, Action<ManagedWaypoint> onWaypointsUpdated)
        {
            if (wp.DurationOrSpecificTime == ManagedWaypoint.WaitType.SpecificTime)
            {
                if (TimetableReader.TryParseTime(wp.WaitUntilTimeString, out TimetableTime time))
                {
                    wp.SetWaitUntilByMinutes(time.Minutes, out GameDateTime waitUntilTime);
                    wp.CurrentlyWaiting = true;
                    Loader.Log($"Loco {wp.Locomotive.Ident} waiting until {waitUntilTime}");
                    onWaypointsUpdated.Invoke(wp);
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

        private static bool FindNearbyCoupling(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.LogDebug($"Starting search for nearby coupling");
            float searchRadius = Loader.Settings.NearbyCouplingSearchRadius;
            List<string> alreadyCoupledIds = [.. wp.Locomotive.EnumerateCoupled().Select(c => c.id)];
            List<string> nearbyCarIds = [.. TrainController.Shared
                .CarIdsInRadius(wp.Location.GetPosition(), searchRadius)
                .Where(cid => !alreadyCoupledIds.Contains(cid))];

            Car bestMatchCar = null;
            Location bestMatchLocation = default;
            float bestMatchDistance = 100000;

            foreach (var carId in nearbyCarIds)
            {
                if (TrainController.Shared.TryGetCarForId(carId, out Car car))
                {
                    if (car.EndGearA.IsCoupled && car.EndGearB.IsCoupled)
                    {
                        continue;
                    }

                    LogicalEnd nearestEnd = ClosestLogicalEndTo(car, wp.Location);
                    Graph.Shared.TryFindDistance(wp.Location, car.LocationFor(nearestEnd), out float totalDistance, out float traverseTimeSeconds);
                    if (totalDistance < bestMatchDistance)
                    {
                        bestMatchCar = car;
                        bestMatchLocation = car.LocationFor(nearestEnd);
                        bestMatchDistance = totalDistance;
                    }
                }
            }
            if (bestMatchCar != null)
            {
                wp.CoupleToCarId = bestMatchCar.id;
                wp.CurrentlyCouplingNearby = true;
                wp.StatusLabel = "Moving to couple nearby";

                Location orientedTargetLocation = Graph.Shared.LocationOrientedToward(bestMatchLocation, wp.Location);
                Location adjustedLocation = Graph.Shared.LocationByMoving(orientedTargetLocation, -0.5f, checkSwitchAgainstMovement: false, stopAtEndOfTrack: true);
                wp.OverwriteLocation(adjustedLocation);

                WaypointQueueController.Shared.UpdateWaypoint(wp);

                Loader.Log($"Sending nearby coupling waypoint for {wp.Locomotive.Ident} to {adjustedLocation} with coupling to {bestMatchCar.Ident}");
                WaypointQueueController.Shared.SendToWaypoint(ordersHelper, adjustedLocation, bestMatchCar.id);
                return true;
            }
            Loader.Log($"Found no nearby cars to couple for {wp.Locomotive.Ident}");

            return false;
        }

        private static bool OrderClearBeyondWaypoint(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            waypoint.StopAtWaypoint = true;
            waypoint.MoveTrainPastWaypoint = false;

            Loader.Log($"Beginning order to clear {waypoint.Locomotive.Ident} train past the waypoint");
            (_, Location furthestCarLocation) = GetTrainEndLocations(waypoint);

            float totalTrainLength = GetTrainLength(waypoint.Locomotive as BaseLocomotive);

            Location orientedLocation = Graph.Shared.LocationOrientedToward(waypoint.Location, furthestCarLocation);
            Location locationToMove;

            try
            {
                locationToMove = Graph.Shared.LocationByMoving(orientedLocation, totalTrainLength, checkSwitchAgainstMovement: false, stopAtEndOfTrack: false);
                locationToMove.AssertValid();
            }
            catch (Exception)
            {
                Toast.Present($"{waypoint.Locomotive.Ident} cannot fit train past the waypoint");

                WaypointQueueController.Shared.UpdateWaypoint(waypoint);
                return false;
            }

            waypoint.StatusLabel = "Sending train past waypoint";
            waypoint.OverwriteLocation(locationToMove);
            WaypointQueueController.Shared.UpdateWaypoint(waypoint);

            Loader.Log($"Sending train of {waypoint.Locomotive.Ident} to {locationToMove} past the waypoint");
            WaypointQueueController.Shared.SendToWaypoint(ordersHelper, locationToMove);
            return true;
        }

        private static void OrderToRefuel(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.Log($"Beginning order to refuel {waypoint.Locomotive.Ident}");
            waypoint.CurrentlyRefueling = true;
            // maybe in the future, support refueling multiple locomotives if they are MU'd

            waypoint.MaxSpeedAfterRefueling = ordersHelper.Orders.MaxSpeedMph;
            // Set speed limit to help prevent train from overrunning waypoint
            int speedWhileRefueling = waypoint.RefuelingSpeedLimit;
            ordersHelper.SetOrdersValue(null, null, maxSpeedMph: speedWhileRefueling, null, null);
            // Make sure AE knows how many cars in case we coupled just before this
            UpdateCarsAfterUncoupling(waypoint.Locomotive as BaseLocomotive);

            Location locationToMove = new();
            try
            {
                locationToMove = GetRefuelLocation(waypoint, ordersHelper);
            }
            catch (InvalidOperationException ex)
            {
                Loader.Log(ex.Message);
                Toast.Present($"Waypoint Queue encountered an error while refueling {waypoint.Locomotive.Ident}");
                waypoint.WillRefuel = false;
                waypoint.CurrentlyRefueling = false;
                ordersHelper.SetOrdersValue(null, null, maxSpeedMph: waypoint.MaxSpeedAfterRefueling, null, null);
                WaypointQueueController.Shared.UpdateWaypoint(waypoint);
            }

            Loader.Log($"Sending refueling waypoint for {waypoint.Locomotive.Ident} to {locationToMove}");
            waypoint.StatusLabel = $"Moving to refuel {waypoint.RefuelLoadName}";
            waypoint.OverwriteLocation(locationToMove);
            waypoint.StopAtWaypoint = true;
            WaypointQueueController.Shared.UpdateWaypoint(waypoint);
            WaypointQueueController.Shared.SendToWaypoint(ordersHelper, locationToMove);
        }

        private static void CleanupAfterRefuel(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.Log($"Done refueling {wp.Locomotive.Ident}");
            wp.WillRefuel = false;
            wp.CurrentlyRefueling = false;
            SetCarLoaderSequencerWantsLoading(wp, false);
            WaypointQueueController.Shared.UpdateWaypoint(wp);

            int maxSpeed = wp.MaxSpeedAfterRefueling;
            if (maxSpeed == 0) maxSpeed = 35;
            ordersHelper.SetOrdersValue(null, null, maxSpeedMph: maxSpeed, null, null);
        }

        private static void SetCarLoaderSequencerWantsLoading(ManagedWaypoint waypoint, bool value)
        {
            CarLoadTargetLoader loaderTarget = FindCarLoadTargetLoader(waypoint);
            CarLoaderSequencer sequencer = WaypointQueueController.Shared.CarLoaderSequencers.Find(x => x.keyValueObject.RegisteredId == loaderTarget.keyValueObject.RegisteredId);
            if (sequencer != null)
            {
                sequencer.keyValueObject[sequencer.readWantsLoadingKey] = value;
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
            Car fuelCar = GetFuelCar((BaseLocomotive)waypoint.Locomotive);

            Loader.LogDebug($"Finding {waypoint.RefuelLoadName} refuel location for {fuelCar.Ident}");

            Vector3 loadSlotPosition = GetFuelCarLoadSlotPosition(fuelCar, waypoint.RefuelLoadName, out float slotMaxCapacity);
            waypoint.RefuelMaxCapacity = slotMaxCapacity;

            if (!Graph.Shared.TryGetLocationFromGamePoint(waypoint.RefuelPoint, 10f, out Location targetLoaderLocation))
            {
                throw new InvalidOperationException($"Cannot refuel at waypoint, failed to get graph location from refuel game point {waypoint.RefuelPoint}");
            }

            (Location closestTrainEndLocation, Location furthestTrainEndLocation) = GetTrainEndLocations(waypoint);

            LogicalEnd furthestFuelCarEnd = ClosestLogicalEndTo(fuelCar, furthestTrainEndLocation);
            LogicalEnd closestFuelCarEnd = GetOppositeEnd(furthestFuelCarEnd);

            List<Car> coupledCarsToEnd = EnumerateCoupledToEnd(fuelCar, furthestFuelCarEnd, inclusive: true);
            float distanceFromFurthestEndOfTrainToFuelCarInclusive = CalculateTotalLength(coupledCarsToEnd);

            float distanceFromClosestFuelCarEndToSlot = Vector3.Distance(fuelCar.LocationFor(closestFuelCarEnd).GetPosition().ZeroY(), loadSlotPosition.ZeroY());

            float totalTrainLength = CalculateTotalLength([.. waypoint.Locomotive.EnumerateCoupled()]);

            Loader.LogDebug($"Total train length is {totalTrainLength}");
            Loader.LogDebug($"distanceFromFurthestEndOfTrainToFuelCarInclusive is {distanceFromFurthestEndOfTrainToFuelCarInclusive}");
            Loader.LogDebug($"distanceFromClosestFuelCarEndToSlot is {distanceFromClosestFuelCarEndToSlot}");

            Location locationToMoveToward = new();
            float distanceToMove = 0;

            //Loader.LogDebug($"Checking if slot is in between loader and closest end");
            if (IsTargetBetween(loadSlotPosition, targetLoaderLocation.GetPosition(), closestTrainEndLocation.GetPosition()))
            {
                // need to move toward the far end
                Loader.LogDebug($"{waypoint.RefuelLoadName} slot is between loader and closest end");
                distanceToMove = distanceFromFurthestEndOfTrainToFuelCarInclusive - distanceFromClosestFuelCarEndToSlot;
                locationToMoveToward = furthestTrainEndLocation;
            }

            //Loader.LogDebug($"Checking if slot is in between loader and furthest end");
            if (IsTargetBetween(loadSlotPosition, targetLoaderLocation.GetPosition(), furthestTrainEndLocation.GetPosition()))
            {
                // need to move toward the near end
                Loader.LogDebug($"{waypoint.RefuelLoadName} slot is between loader and furthest end");
                distanceToMove = totalTrainLength - distanceFromFurthestEndOfTrainToFuelCarInclusive + distanceFromClosestFuelCarEndToSlot;
                locationToMoveToward = closestTrainEndLocation;
            }

            //Loader.LogDebug($"Checking if loader is in closest end and furthest end");
            if (IsTargetBetween(targetLoaderLocation.GetPosition(), closestTrainEndLocation.GetPosition(), furthestTrainEndLocation.GetPosition()))
            {
                Loader.LogDebug($"{waypoint.RefuelLoadName} loader is between closest end and furthest end");
                distanceToMove = -distanceToMove;
            }

            Loader.LogDebug($"distanceToMove is {distanceToMove}");

            Location orientedTargetLocation = Graph.Shared.LocationOrientedToward(targetLoaderLocation, locationToMoveToward);

            Location locationToMove = Graph.Shared.LocationByMoving(orientedTargetLocation, distanceToMove, true, true);

            Loader.LogDebug($"Location to refuel {waypoint.RefuelLoadName} is {locationToMove}");
            return locationToMove;
        }

        private static bool TryGetOpenEndForCar(Car car, out LogicalEnd logicalEnd)
        {
            if (!car.TryGetAdjacentCar(LogicalEnd.A, out _))
            {
                logicalEnd = LogicalEnd.A;
                return true;
            }
            if (!car.TryGetAdjacentCar(LogicalEnd.B, out _))
            {
                logicalEnd = LogicalEnd.B;
                return true;
            }
            logicalEnd = LogicalEnd.A;
            return false;
        }

        private static (Location closest, Location furthest) GetTrainEndLocations(ManagedWaypoint waypoint)
        {
            List<Car> allCoupled = [.. waypoint.Locomotive.EnumerateCoupled()];

            if (allCoupled.Count == 1)
            {
                Car onlyCar = allCoupled[0];
                LogicalEnd closestEnd = ClosestLogicalEndTo(onlyCar, waypoint.Location);
                LogicalEnd furthestEnd = GetOppositeEnd(closestEnd);

                return (onlyCar.LocationFor(closestEnd), onlyCar.LocationFor(furthestEnd));
            }

            Car firstCar = allCoupled.First();
            Car lastCar = allCoupled.Last();

            if (!TryGetOpenEndForCar(firstCar, out LogicalEnd firstEnd))
            {
                throw new InvalidOperationException($"{firstCar.Ident} has no open end");
            }
            if (!TryGetOpenEndForCar(lastCar, out LogicalEnd lastEnd))
            {
                throw new InvalidOperationException($"{lastCar.Ident} has no open end");
            }

            Loader.LogDebug($"Furthest end on first is {(firstCar.LogicalToEnd(firstEnd) == End.R ? "R" : "F")}");
            Location firstLocation = firstCar.LocationFor(firstEnd);
            float firstDistance = Graph.Shared.GetDistanceBetweenClose(firstLocation, waypoint.Location);

            Loader.LogDebug($"Furthest end on last is {(lastCar.LogicalToEnd(lastEnd) == End.R ? "R" : "F")}");
            Location lastLocation = lastCar.LocationFor(lastEnd);
            float lastDistance = Graph.Shared.GetDistanceBetweenClose(lastLocation, waypoint.Location);

            Location closestLocation = firstLocation;
            Location furthestLocation = lastLocation;

            if (firstDistance > lastDistance)
            {
                closestLocation = lastLocation;
                furthestLocation = firstLocation;
                Loader.LogDebug($"Closest car is {lastCar.Ident}");
                Loader.LogDebug($"Furthest car is {firstCar.Ident}");
            }
            else
            {
                Loader.LogDebug($"Closest car is {firstCar.Ident}");
                Loader.LogDebug($"Furthest car is {lastCar.Ident}");
            }

            return (closestLocation, furthestLocation);
        }

        private static float GetTrainLength(BaseLocomotive locomotive)
        {
            MethodInfo calculateTotalLengthMI = AccessTools.Method(typeof(AutoEngineerPlanner), "CalculateTotalLength");
            float totalTrainLength = (float)calculateTotalLengthMI.Invoke(locomotive.AutoEngineerPlanner, []);
            return totalTrainLength;
        }

        // Copied from AutoEngineerPlanner.CalculateTotalLength
        private static float CalculateTotalLength(List<Car> cars)
        {
            float num = 0f;
            foreach (Car item in cars)
            {
                num += item.carLength;
            }

            return num + 1.04f * (float)(cars.Count - 1);
        }

        private static Vector3 GetFuelCarLoadSlotPosition(Car fuelCar, string refuelLoadName, out float maxCapacity)
        {
            LoadSlot loadSlot = fuelCar.Definition.LoadSlots.Find(slot => slot.RequiredLoadIdentifier == refuelLoadName);

            maxCapacity = loadSlot.MaximumCapacity;

            int loadSlotIndex = fuelCar.Definition.LoadSlots.IndexOf(loadSlot);

            List<CarLoadTarget> carLoadTargets = fuelCar.GetComponentsInChildren<CarLoadTarget>().ToList();
            CarLoadTarget loadTarget = carLoadTargets.Find(clt => clt.slotIndex == loadSlotIndex);

            Vector3 loadSlotPosition = CalculatePositionFromLoadTarget(fuelCar, loadTarget);

            return loadSlotPosition;
        }

        private static bool IsTargetBetween(Vector3 target, Vector3 positionA, Vector3 positionB)
        {
            // If target is in the middle, the distance between either end to the target will always be less than the length from one end to the other
            float distanceAToTarget = Vector3.Distance(positionA.ZeroY(), target.ZeroY());

            float distanceBToTarget = Vector3.Distance(positionB.ZeroY(), target.ZeroY());

            float distanceAtoB = Vector3.Distance(positionA.ZeroY(), positionB.ZeroY());

            //Loader.LogDebug($"Distance A to Target {distanceAToTarget}");
            //Loader.LogDebug($"Distance B to Target {distanceBToTarget}");
            //Loader.LogDebug($"Distance A to B {distanceAtoB}");

            if (distanceAToTarget < distanceAtoB && distanceBToTarget < distanceAtoB)
            {
                return true;
            }
            //Loader.LogDebug($"Target is NOT in between");
            return false;
        }

        private static Vector3 CalculatePositionFromLoadTarget(Car fuelCar, CarLoadTarget loadTarget)
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

        private static void ResolveBrakeSystemOnCouple(ManagedWaypoint waypoint)
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
        }

        private static void ResolvePostCouplingCut(ManagedWaypoint waypoint)
        {
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
                    UpdateCarsAfterUncoupling(waypoint.Locomotive as BaseLocomotive);

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

        private static List<Car> EnumerateCoupledToEnd(Car car, LogicalEnd directionToCount, bool inclusive = false)
        {
            List<Car> result = [];
            if (inclusive) result.Add(car);

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

        private static void ResolveUncouplingOrders(ManagedWaypoint waypoint)
        {
            if (!waypoint.IsUncoupling) return;
            Loader.Log($"Resolving uncoupling orders for {waypoint.Locomotive.Ident}");

            LogicalEnd directionToCountCars = GetEndRelativeToWapoint(waypoint.Locomotive, waypoint.Location, useFurthestEnd: !waypoint.CountUncoupledFromNearestToWaypoint);

            List<Car> allCarsFromEnd = waypoint.Locomotive.EnumerateCoupled(directionToCountCars).ToList();

            // Handling the direction ensures cars to cut are at the front of the list
            List<Car> carsToCut = allCarsFromEnd.Take(waypoint.NumberOfCarsToCut).ToList();

            carsToCut = FilterAnySplitLocoTenderPairs(carsToCut);

            // This can happen if the user selected counting from nearest to waypoint and the locomotive backed up to the waypoint
            if (carsToCut.Count == 0 && allCarsFromEnd.Count > 1)
            {
                // If the tender and locomotive are the "front" two cars that would be cut, we can treat that as 1 car to cut instead.
                Car maybeLocoOrTenderA = allCarsFromEnd.ElementAtOrDefault(0);
                Car maybeLocoOrTenderB = allCarsFromEnd.ElementAtOrDefault(1);
                Car tender;

                if (maybeLocoOrTenderA.Archetype == Model.Definition.CarArchetype.LocomotiveSteam && PatchSteamLocomotive.TryGetTender(maybeLocoOrTenderA, out tender) && tender.id == maybeLocoOrTenderB.id)
                {
                    carsToCut.Add(maybeLocoOrTenderA);
                    carsToCut.Add(maybeLocoOrTenderB);
                }
                if (maybeLocoOrTenderB.Archetype == Model.Definition.CarArchetype.LocomotiveSteam && PatchSteamLocomotive.TryGetTender(maybeLocoOrTenderB, out tender) && tender.id == maybeLocoOrTenderA.id)
                {
                    carsToCut.Add(maybeLocoOrTenderA);
                    carsToCut.Add(maybeLocoOrTenderB);
                }
            }

            List<Car> carsRemaining = allCarsFromEnd.Where(c => !carsToCut.Contains(c)).ToList();

            List<Car> activeCut = carsRemaining;
            List<Car> inactiveCut = carsToCut;

            Loader.Log($"Seeking to uncouple {waypoint.NumberOfCarsToCut} cars from train of {allCarsFromEnd.Count} total cars with {carsRemaining.Count} cars left behind");

            Loader.Log($"TakeUncoupledCarsAsActiveCut is {waypoint.TakeUncoupledCarsAsActiveCut}");
            if (waypoint.TakeUncoupledCarsAsActiveCut)
            {
                activeCut = carsToCut;
                inactiveCut = carsRemaining;
            }

            if (carsToCut.Count == 0)
            {
                Loader.Log($"No valid cars to cut found for uncoupling");
                // Should probably send an alert to the player
                return;
            }

            string carsToCutFormatted = String.Join("-", carsToCut.Select(c => $"[{c.Ident}]"));
            string allCarsFromEndFormatted = String.Join("-", allCarsFromEnd.Select(c => $"[{c.Ident}]"));
            Loader.Log($"Cutting {carsToCutFormatted} from {allCarsFromEndFormatted} as {(waypoint.TakeUncoupledCarsAsActiveCut ? "active cut" : "inactive cut")}");

            if (waypoint.ApplyHandbrakesOnUncouple)
            {
                SetHandbrakes(inactiveCut);
            }

            // The car from the cut that is adjacent to the rest of the uncut train
            Car carToUncouple = carsToCut.Last();

            /**
             * Uncouple 2 cars closest to the waypoint.
             * x is where we uncouple
             * v is the waypoint
             * A and B are the car logical ends
             * (AB) is the reference car to uncouple
             * [loco]-[AB]-[AB]-[AB]x(AB)-[AB] v
             * (AB)'s B end is closest to v, so uncouple on (AB)'s A end which is further from waypoint than B
             * 
             * Uncouple 2 cars furthest from the waypoint
             * [AB]-(AB)x[AB]-[AB]-[AB]-[loco] v
             * (AB)'s A end is closest to v, so uncouple on (AB)'s B end which is closer to waypoint than A
             */
            LogicalEnd endToUncouple = GetEndRelativeToWapoint(carToUncouple, waypoint.Location, useFurthestEnd: waypoint.CountUncoupledFromNearestToWaypoint);

            Loader.Log($"Uncoupling {carToUncouple.Ident} for cut of {carsToCut.Count} cars");
            UncoupleCar(carToUncouple, endToUncouple);
            UpdateCarsAfterUncoupling(waypoint.Locomotive as BaseLocomotive);

            if (waypoint.BleedAirOnUncouple)
            {
                Loader.LogDebug($"Bleeding air on {inactiveCut.Count} cars");
                foreach (Car car in inactiveCut)
                {
                    car.air.BleedBrakeCylinder();
                }
            }
        }

        private static void UpdateCarsAfterUncoupling(BaseLocomotive locomotive)
        {
            MethodInfo updateCarsMI = AccessTools.Method(typeof(AutoEngineerPlanner), "UpdateCars");
            object[] parameters = [null];
            updateCarsMI.Invoke(locomotive.AutoEngineerPlanner, parameters);
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

        private static LogicalEnd GetOppositeEnd(LogicalEnd logicalEnd)
        {
            return logicalEnd == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;
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
                    car.ApplyEndGearChange(endToUncouple, EndGearStateKey.CutLever, 1f);

                    adjacent.ApplyEndGearChange(oppositeEnd, EndGearStateKey.Anglecock, f: 0f);
                    adjacent.ApplyEndGearChange(oppositeEnd, EndGearStateKey.IsCoupled, boolValue: false);
                    adjacent.ApplyEndGearChange(oppositeEnd, EndGearStateKey.IsAirConnected, boolValue: false);
                    adjacent.ApplyEndGearChange(oppositeEnd, EndGearStateKey.CutLever, 1f);
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
    }
}
