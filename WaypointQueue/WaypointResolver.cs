using Game;
using Game.Messages;
using Game.State;
using HarmonyLib;
using Helpers;
using Model;
using Model.AI;
using Model.Definition;
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
using static Model.Ops.OpsController;

namespace WaypointQueue
{
    internal static class WaypointResolver
    {
        private static readonly float WaitBeforeCuttingTimeout = 5f;
        private static readonly float AverageCarLengthMeters = 12.2f;
        public static readonly string NoDestinationString = "No destination";
        public static readonly string RemoveTrainSymbolString = "remove-train-symbol";

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

                // TODO: refactor TryHandleUnresolvedWaypoint to
                // avoid handling orders like this when not stopping at a waypoint

                // Try to begin nearby coupling
                if (wp.WillSeekNearestCoupling && !wp.CurrentlyCouplingNearby)
                {
                    if (FindNearbyCoupling(wp, ordersHelper))
                    {
                        return false;
                    }
                    else
                    {
                        wp.CouplingSearchMode = ManagedWaypoint.CoupleSearchMode.None;
                        Toast.Present($"{wp.Locomotive.Ident} cannot find a nearby car to couple.");
                    }
                }

                // Try coupling to target
                if (wp.WillSeekSpecificCarCoupling && !wp.CurrentlyCouplingSpecificCar)
                {
                    if (FindSpecificCouplingTarget(wp, ordersHelper))
                    {
                        return false;
                    }
                    else
                    {
                        wp.CouplingSearchMode = ManagedWaypoint.CoupleSearchMode.None;
                    }
                }

                ResolveUncouplingOrders(wp);
                if (wp.WillChangeMaxSpeed)
                {
                    ResolveChangeMaxSpeed(wp);
                }
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
            if (wp.WillSeekNearestCoupling && !wp.CurrentlyCouplingNearby)
            {
                if (FindNearbyCoupling(wp, ordersHelper))
                {
                    return false;
                }
                else
                {
                    wp.CouplingSearchMode = ManagedWaypoint.CoupleSearchMode.None;
                    Toast.Present($"{wp.Locomotive.Ident} cannot find a nearby car to couple.");
                }
            }

            // Try coupling to target
            if (wp.WillSeekSpecificCarCoupling && !wp.CurrentlyCouplingSpecificCar)
            {
                if (FindSpecificCouplingTarget(wp, ordersHelper))
                {
                    return false;
                }
                else
                {
                    wp.CouplingSearchMode = ManagedWaypoint.CoupleSearchMode.None;
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
            if (wp.StopAtWaypoint && !IsTrainStopped(wp) && wp.HasAnyCutOrders && wp.SecondsSpentWaitingBeforeCut < WaitBeforeCuttingTimeout)
            {
                if (!wp.CurrentlyWaitingBeforeCutting)
                {
                    Loader.Log($"{wp.Locomotive.Ident} is waiting until train is at rest to resolve cut orders");
                    wp.CurrentlyWaitingBeforeCutting = true;
                    wp.StatusLabel = $"Waiting until at rest to cut cars";
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

            if (wp.IsCoupling && wp.TryResolveCoupleToCar(out Car _) && wp.NumberOfCarsToCut > 0)
            {
                ResolvePostCouplingCut(wp);
            }
            else if (wp.HasAnyUncouplingOrders)
            {
                ResolveUncouplingOrders(wp);
            }

            if (wp.WillChangeMaxSpeed)
            {
                ResolveChangeMaxSpeed(wp);
            }

            if (TryBeginWaiting(wp, onWaypointDidUpdate))
            {
                wp.StatusLabel = "Waiting before continuing";
                WaypointQueueController.Shared.UpdateWaypoint(wp);
                return false;
            }

            return true;
        }

        private static void ResolveChangeMaxSpeed(ManagedWaypoint wp)
        {
            var ordersHelper = WaypointQueueController.Shared.GetOrdersHelper(wp.Locomotive);
            int maxSpeedToSet = Mathf.Clamp(wp.MaxSpeedForChange, 0, 45);
            ordersHelper.SetOrdersValue(null, null, maxSpeedMph: maxSpeedToSet, null, null);
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
            if (wp.WillRefuel && !wp.CurrentlyRefueling && !wp.HasAnyCouplingOrders && !wp.MoveTrainPastWaypoint)
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

        internal static bool IsTrainStopped(ManagedWaypoint wp)
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
            return wp.OnlySeekNearbyOnTrackAhead ? FindNearbyCouplingInStraightLine(wp, ordersHelper) : FindNearbyCouplingInRadius(wp, ordersHelper);
        }

        private static bool FindNearbyCouplingInStraightLine(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.LogDebug($"Starting search for nearby coupling in straight line");
            (Location closestTrainEnd, Location furthestTrainEnd) = GetTrainEndLocations(wp, out float closestDistance, out Car closestCar, out Car furthestCar);
            Location orientedClosestTrainEnd = Graph.Shared.LocationOrientedToward(closestTrainEnd, furthestTrainEnd);

            float checkDistanceInterval = AverageCarLengthMeters / 2;
            float totalDistanceChecked = 0;
            float searchRadius = Loader.Settings.NearbyCouplingSearchDistanceInCarLengths * AverageCarLengthMeters;
            List<Car> consist = wp.Locomotive.EnumerateCoupled().ToList();

            Car targetCar = null;
            while (totalDistanceChecked < searchRadius)
            {
                totalDistanceChecked += checkDistanceInterval;
                try
                {
                    Loader.LogDebug($"Checking for coupling ahead by moving {totalDistanceChecked}");
                    Location checkLocation = Graph.Shared.LocationByMoving(orientedClosestTrainEnd, totalDistanceChecked, checkSwitchAgainstMovement: false, stopAtEndOfTrack: true);
                    targetCar = TrainController.Shared.CheckForCarAtPoint(Graph.Shared.GetPosition(checkLocation));
                    if (targetCar != null && !consist.Contains(targetCar))
                    {
                        break;
                    }
                }
                catch (Exception)
                {
                    break;
                }
            }

            if (targetCar != null)
            {
                LogicalEnd nearestEnd = ClosestLogicalEndTo(targetCar, closestTrainEnd);
                Location bestLocation;
                if (!targetCar[nearestEnd].IsCoupled)
                {
                    Loader.Log($"Closest end of {targetCar.Ident} is available to couple");
                    bestLocation = GetCouplerLocation(targetCar, nearestEnd);
                }
                else
                {
                    Loader.LogError($"Closest end of {targetCar.Ident} is NOT available to couple");
                    return false;
                }
                wp.StatusLabel = $"Moving to couple {targetCar.Ident}";
                wp.CouplingSearchMode = ManagedWaypoint.CoupleSearchMode.None;
                wp.CurrentlyCouplingNearby = true;
                wp.StopAtWaypoint = true;
                wp.CoupleToCar = targetCar;
                wp.CoupleToCarId = targetCar.id;
                wp.OverwriteLocation(bestLocation);
                WaypointQueueController.Shared.UpdateWaypoint(wp);
                Loader.Log($"Sending target coupling waypoint for {wp.Locomotive.Ident} to {bestLocation} with coupling to {targetCar.Ident}");
                WaypointQueueController.Shared.SendToWaypoint(ordersHelper, bestLocation, targetCar.id);
                return true;
            }

            Loader.Log($"Found no nearby cars to couple for {wp.Locomotive.Ident}");

            return false;
        }

        private static bool FindNearbyCouplingInRadius(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.LogDebug($"Starting search for nearby coupling in radius");
            float searchRadius = Loader.Settings.NearbyCouplingSearchDistanceInCarLengths * AverageCarLengthMeters;
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
                wp.CouplingSearchMode = ManagedWaypoint.CoupleSearchMode.None;
                wp.CoupleToCarId = bestMatchCar.id;
                wp.StopAtWaypoint = true;
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

        private static bool FindSpecificCouplingTarget(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            Car targetCar = waypoint.CouplingSearchResultCar;
            if (targetCar == null && !waypoint.TryResolveCouplingSearchText(out targetCar))
            {
                Toast.Present($"Cannot find valid car matching \"{waypoint.CouplingSearchText}\" for {waypoint.Locomotive.Ident} to couple");
                return false;
            }

            LogicalEnd nearestEnd = ClosestLogicalEndTo(targetCar, waypoint.Locomotive.OpsLocation);

            Location bestLocation;

            if (!targetCar[nearestEnd].IsCoupled)
            {
                Loader.Log($"Closest end of {targetCar.Ident} is available to couple");
                bestLocation = GetCouplerLocation(targetCar, nearestEnd);
            }
            else if (!targetCar[GetOppositeEnd(nearestEnd)].IsCoupled)
            {
                Loader.Log($"Furthest end of {targetCar.Ident} is available to couple");
                bestLocation = GetCouplerLocation(targetCar, GetOppositeEnd(nearestEnd));
            }
            else
            {
                Loader.Log($"Both ends of {targetCar.Ident} are unavailable to couple");
                Toast.Present($"{waypoint.Locomotive.Ident} coupling to {targetCar.Ident} is blocked");
                return false;
            }

            if (bestLocation.IsValid)
            {

                waypoint.StatusLabel = $"Moving to couple {targetCar.Ident}";
                waypoint.CouplingSearchMode = ManagedWaypoint.CoupleSearchMode.None;
                waypoint.CurrentlyCouplingSpecificCar = true;
                waypoint.StopAtWaypoint = true;
                waypoint.CoupleToCar = targetCar;
                waypoint.CoupleToCarId = targetCar.id;
                waypoint.OverwriteLocation(bestLocation);
                WaypointQueueController.Shared.UpdateWaypoint(waypoint);
                Loader.Log($"Sending target coupling waypoint for {waypoint.Locomotive.Ident} to {bestLocation} with coupling to {targetCar.Ident}");
                WaypointQueueController.Shared.SendToWaypoint(ordersHelper, bestLocation, targetCar.id);
                return true;
            }

            Loader.LogError($"Location {bestLocation} was not valid for {waypoint.Locomotive.Ident} to couple to {targetCar.Ident}");

            Toast.Present($"{waypoint.Locomotive.Ident} failed to determine a valid location to couple {targetCar.Ident}");
            return false;
        }

        private static Location GetCouplerLocation(Car car, LogicalEnd logicalEnd)
        {
            End carEnd = car.LogicalToEnd(logicalEnd);
            if (carEnd == End.F)
            {
                return Graph.Shared.LocationByMoving(car.LocationF, 0.5f, checkSwitchAgainstMovement: false, stopAtEndOfTrack: true);
            }
            else
            {
                return Graph.Shared.LocationByMoving(car.LocationR, -0.5f, checkSwitchAgainstMovement: false, stopAtEndOfTrack: true).Flipped();
            }
        }

        private static bool OrderClearBeyondWaypoint(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            waypoint.StopAtWaypoint = true;
            waypoint.MoveTrainPastWaypoint = false;

            Loader.Log($"Beginning order to clear {waypoint.Locomotive.Ident} train past the waypoint");
            (_, Location furthestCarLocation) = GetTrainEndLocations(waypoint, out _, out _, out _);

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
            if (loaderTarget == null)
            {
                Loader.LogError($"Cannot find CarLoadTargetLoader at point {waypoint.RefuelPoint} for waypoint {waypoint.Id}");
                return;
            }
            CarLoaderSequencer sequencer = WaypointQueueController.Shared.CarLoaderSequencers.Find(x => x.keyValueObject.RegisteredId == loaderTarget.keyValueObject.RegisteredId);
            if (sequencer != null)
            {
                sequencer.keyValueObject[sequencer.readWantsLoadingKey] = value;
            }
            else
            {
                Loader.LogError($"Cannot find CarLoaderSequencer for loader target {loaderTarget.name} for waypoint {waypoint.Id}");
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

            (Location closestTrainEndLocation, Location furthestTrainEndLocation) = GetTrainEndLocations(waypoint, out _, out _, out _);

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

        internal static (Location closest, Location furthest) GetTrainEndLocations(ManagedWaypoint waypoint, out float closestDistance, out Car closestCar, out Car furthestCar)
        {
            Location closestLocation;
            Location furthestLocation;

            List<Car> allCoupled = [.. waypoint.Locomotive.EnumerateCoupled()];

            //Loader.Log("GetTrainEndLocations " + String.Join("-", allCoupled.Select(c => $"[{c.Ident}]")));

            if (allCoupled.Count == 1)
            {
                Car onlyCar = allCoupled[0];
                LogicalEnd closestEnd = ClosestLogicalEndTo(onlyCar, waypoint.Location);
                LogicalEnd furthestEnd = GetOppositeEnd(closestEnd);

                closestLocation = onlyCar.LocationFor(closestEnd);
                furthestLocation = onlyCar.LocationFor(furthestEnd);

                closestDistance = Graph.Shared.GetDistanceBetweenClose(closestLocation, waypoint.Location);
                closestCar = onlyCar;
                furthestCar = onlyCar;

                return (closestLocation, furthestLocation);
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

            closestDistance = firstDistance;
            closestLocation = firstLocation;
            furthestLocation = lastLocation;

            if (firstDistance > lastDistance)
            {
                closestDistance = lastDistance;
                closestLocation = lastLocation;
                furthestLocation = firstLocation;
                closestCar = lastCar;
                furthestCar = firstCar;
                Loader.LogDebug($"Closest car is {lastCar.Ident}");
                Loader.LogDebug($"Furthest car is {firstCar.Ident}");
            }
            else
            {
                closestCar = firstCar;
                furthestCar = lastCar;
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
            if (waypoint.WillUncoupleByCount && waypoint.NumberOfCarsToCut > 0)
            {
                UncoupleByCount(waypoint);
            }

            if (waypoint.WillUncoupleByDestination && !string.IsNullOrEmpty(waypoint.UncoupleDestinationId))
            {
                UncoupleByDestination(waypoint);
            }

            if (waypoint.WillUncoupleBySpecificCar)
            {
                UncoupleBySpecificCar(waypoint);
            }

            if (waypoint.WillUncoupleAllExceptLocomotives)
            {
                UncoupleAllExceptLocomotives(waypoint);
            }
        }

        private static void UncoupleAllExceptLocomotives(ManagedWaypoint waypoint)
        {
            Loader.Log($"Resolving uncoupling all except locomotives for {waypoint.Locomotive.Ident}");
            List<Car> consist = [.. waypoint.Locomotive.EnumerateCoupled(LogicalEnd.A)];
            List<CarArchetype> locoArchetypes = [CarArchetype.LocomotiveDiesel, CarArchetype.LocomotiveSteam, CarArchetype.Tender];

            List<List<Car>> listOfCarBlocks = [[]];

            for (int i = 0; i < consist.Count; i++)
            {
                Car currentCar = consist[i];
                List<Car> currentBlock = listOfCarBlocks.Last();
                currentBlock.Add(currentCar);

                if (i < consist.Count - 1)
                {
                    Car nextCar = consist[i + 1];
                    bool currentCarIsLocoType = locoArchetypes.Contains(currentCar.Archetype);
                    bool nextCarIsLocoType = locoArchetypes.Contains(nextCar.Archetype);

                    if ((currentCarIsLocoType && !nextCarIsLocoType) || (!currentCarIsLocoType && nextCarIsLocoType))
                    {
                        listOfCarBlocks.Add([]);
                    }
                }
            }

            foreach (List<Car> block in listOfCarBlocks)
            {
                if (block.Count > 0)
                {
                    Car lastCar = block.Last();
                    Loader.Log($"Uncoupling {lastCar.Ident} on logical end B for block {block.Count} cars");
                    UncoupleCar(lastCar, LogicalEnd.B);

                    if (!locoArchetypes.Contains(lastCar.Archetype))
                    {
                        if (waypoint.ApplyHandbrakesOnUncouple)
                        {
                            SetHandbrakes(block);
                        }

                        if (waypoint.BleedAirOnUncouple)
                        {
                            BleedAirOnCut(block);
                        }
                    }
                }
            }

            UpdateCarsAfterUncoupling(waypoint.Locomotive as BaseLocomotive);
        }

        private static void UncoupleByCount(ManagedWaypoint waypoint)
        {
            if (!waypoint.WillUncoupleByCount && waypoint.NumberOfCarsToCut <= 0) return;
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

            PerformCut(carsToCut, allCarsFromEnd, waypoint);
        }

        private static string CarListToString(List<Car> cars)
        {
            return String.Join("-", cars.Select(c => $"[{c.Ident}]"));
        }

        private static string LogicalEndToString(LogicalEnd logicalEnd)
        {
            return logicalEnd == LogicalEnd.A ? "A" : "B";
        }

        private static void UncoupleByDestination(ManagedWaypoint waypoint)
        {
            Loader.Log($"Resolving uncouple by destination for {waypoint.Locomotive.Ident}");
            LogicalEnd directionToCountCars = GetEndRelativeToWapoint(waypoint.Locomotive, waypoint.Location, useFurthestEnd: !waypoint.CountUncoupledFromNearestToWaypoint);

            List<Car> allCarsFromEnd = waypoint.Locomotive.EnumerateCoupled(directionToCountCars).ToList();
            Loader.LogDebug($"Enumerating all cars of {waypoint.Locomotive.Ident} from logical end {LogicalEndToString(directionToCountCars)}:\n{CarListToString(allCarsFromEnd)}");

            List<Car> carsToCut = [];
            if (waypoint.UncoupleDestinationId == NoDestinationString)
            {
                carsToCut = FindMatchingCarsByNoDestination(allCarsFromEnd, waypoint.ExcludeMatchingCarsFromCut);
            }
            if (waypoint.WillUncoupleByDestinationTrack)
            {
                try
                {
                    OpsCarPosition destinationMatch = OpsController.Shared.ResolveOpsCarPosition(waypoint.UncoupleDestinationId);
                    carsToCut = FindMatchingCarsByTrackDestination(allCarsFromEnd, destinationMatch, waypoint.ExcludeMatchingCarsFromCut);
                }
                catch (InvalidOpsCarPositionException)
                {
                    Toast.Present($"{waypoint.Locomotive.Ident} failed to resolve unknown track destination.");
                    Loader.LogError($"Failed to resolve track destination by id {waypoint.UncoupleDestinationId}");
                    return;
                }
            }
            if (waypoint.WillUncoupleByDestinationIndustry)
            {
                Industry industryMatch = OpsController.Shared.AllIndustries.Where(i => i.identifier == waypoint.UncoupleDestinationId).FirstOrDefault();
                if (industryMatch == null)
                {
                    Toast.Present($"{waypoint.Locomotive.Ident} failed to resolve unknown industry.");
                    Loader.LogError($"Failed to resolve industry by id {waypoint.UncoupleDestinationId}");
                    return;
                }
                carsToCut = FindMatchingCarsByIndustryDestination(allCarsFromEnd, industryMatch, waypoint.ExcludeMatchingCarsFromCut);
            }
            if (waypoint.WillUncoupleByDestinationArea)
            {
                Area areaMatch = OpsController.Shared.Areas.Where(i => i.identifier == waypoint.UncoupleDestinationId).FirstOrDefault();
                if (areaMatch == null)
                {
                    Toast.Present($"{waypoint.Locomotive.Ident} failed to resolve unknown area.");
                    Loader.LogError($"Failed to resolve area by id {waypoint.UncoupleDestinationId}");
                    return;
                }
                carsToCut = FindMatchingCarsByAreaDestination(allCarsFromEnd, areaMatch, waypoint.ExcludeMatchingCarsFromCut);
            }

            PerformCut(carsToCut, allCarsFromEnd, waypoint);
        }

        private static List<Car> FindMatchingCarsByNoDestination(List<Car> allCars, bool excludeMatchingCarsFromCut)
        {
            List<Car> carsToCut = [];

            bool foundBlock = false;
            for (int i = 0; i < allCars.Count; i++)
            {
                Car car = allCars[i];
                bool hasDestination = OpsController.Shared.TryGetDestinationInfo(car, out _, out _, out _, out _);

                bool carMatchesFilter = !hasDestination;

                Loader.LogDebug(carMatchesFilter ? $"Car {car.Ident} matches filter of no destination" : $"Car {car.Ident} does NOT match filter of no destination");

                if (foundBlock && !carMatchesFilter)
                {
                    break;
                }

                if (carMatchesFilter)
                {
                    foundBlock = true;
                    if (!excludeMatchingCarsFromCut)
                    {
                        Loader.LogDebug($"Adding matching {car.Ident} to cut list");
                        carsToCut.Add(car);
                    }
                }
                else
                {
                    Loader.LogDebug($"Adding non-matching {car.Ident} to cut list");
                    carsToCut.Add(car);
                }
            }

            return carsToCut;
        }

        private static List<Car> FindMatchingCarsByTrackDestination(List<Car> allCars, OpsCarPosition destinationMatch, bool excludeMatchingCarsFromCut)
        {
            List<Car> carsToCut = [];

            bool foundBlock = false;
            for (int i = 0; i < allCars.Count; i++)
            {
                Car car = allCars[i];
                bool hasDestination = OpsController.Shared.TryGetDestinationInfo(car, out _, out _, out _, out OpsCarPosition carDestination);

                bool carMatchesFilter = hasDestination && carDestination.DisplayName == destinationMatch.DisplayName;

                Loader.LogDebug(carMatchesFilter ? $"Car {car.Ident} matches filter of {destinationMatch.DisplayName}" : $"Car {car.Ident} does NOT match filter of {destinationMatch.DisplayName}");

                if (foundBlock && !carMatchesFilter)
                {
                    break;
                }

                if (carMatchesFilter)
                {
                    foundBlock = true;
                    if (!excludeMatchingCarsFromCut)
                    {
                        Loader.LogDebug($"Adding matching {car.Ident} to cut list");
                        carsToCut.Add(car);
                    }
                }
                else
                {
                    Loader.LogDebug($"Adding non-matching {car.Ident} to cut list");
                    carsToCut.Add(car);
                }
            }

            return carsToCut;
        }

        private static List<Car> FindMatchingCarsByIndustryDestination(List<Car> allCars, Industry destinationMatch, bool excludeMatchingCarsFromCut)
        {
            List<Car> carsToCut = [];

            bool foundBlock = false;
            for (int i = 0; i < allCars.Count; i++)
            {
                Car car = allCars[i];
                bool carMatchesFilter = false;

                if (OpsController.Shared.TryGetDestinationInfo(car, out _, out _, out _, out OpsCarPosition carDestination))
                {
                    IndustryComponent industryComponent = Traverse.Create(OpsController.Shared).Method("IndustryComponentForPosition", [typeof(OpsCarPosition)], [carDestination]).GetValue<IndustryComponent>();
                    carMatchesFilter = industryComponent?.Industry?.identifier == destinationMatch.identifier;
                }

                Loader.LogDebug(carMatchesFilter ? $"Car {car.Ident} matches filter of {destinationMatch.name}" : $"Car {car.Ident} does NOT match filter of {destinationMatch.name}");

                if (foundBlock && !carMatchesFilter)
                {
                    break;
                }

                if (carMatchesFilter)
                {
                    foundBlock = true;
                    if (!excludeMatchingCarsFromCut)
                    {
                        Loader.LogDebug($"Adding matching {car.Ident} to cut list");
                        carsToCut.Add(car);
                    }
                }
                else
                {
                    Loader.LogDebug($"Adding non-matching {car.Ident} to cut list");
                    carsToCut.Add(car);
                }
            }

            return carsToCut;
        }

        private static List<Car> FindMatchingCarsByAreaDestination(List<Car> allCars, Area destinationMatch, bool excludeMatchingCarsFromCut)
        {
            List<Car> carsToCut = [];

            bool foundBlock = false;
            for (int i = 0; i < allCars.Count; i++)
            {
                Car car = allCars[i];
                bool carMatchesFilter = false;

                if (OpsController.Shared.TryGetDestinationInfo(car, out _, out _, out _, out OpsCarPosition carDestination))
                {
                    Area carArea = OpsController.Shared.AreaForCarPosition(carDestination);
                    carMatchesFilter = carArea?.identifier == destinationMatch.identifier;
                }

                Loader.LogDebug(carMatchesFilter ? $"Car {car.Ident} matches filter of {destinationMatch.name}" : $"Car {car.Ident} does NOT match filter of {destinationMatch.name}");

                if (foundBlock && !carMatchesFilter)
                {
                    break;
                }

                if (carMatchesFilter)
                {
                    foundBlock = true;
                    if (!excludeMatchingCarsFromCut)
                    {
                        Loader.LogDebug($"Adding matching {car.Ident} to cut list");
                        carsToCut.Add(car);
                    }
                }
                else
                {
                    Loader.LogDebug($"Adding non-matching {car.Ident} to cut list");
                    carsToCut.Add(car);
                }
            }

            return carsToCut;
        }

        private static void UncoupleBySpecificCar(ManagedWaypoint waypoint)
        {
            if (!waypoint.TryResolveUncouplingSearchText(out Car carToUncouple))
            {
                Toast.Present($"Cannot find valid car matching \"{waypoint.UncouplingSearchText}\" for {waypoint.Locomotive.Ident} to uncouple");
                Loader.LogError($"Cannot find valid car matching \"{waypoint.UncouplingSearchText}\" for {waypoint.Locomotive.Ident} to uncouple");
                return;
            }

            LogicalEnd closestEnd = ClosestLogicalEndTo(carToUncouple, waypoint.Location);
            List<Car> consist = [.. waypoint.Locomotive.EnumerateCoupled(closestEnd)];
            if (!consist.Any(c => c.id == carToUncouple.id))
            {
                Toast.Present($"{carToUncouple.Ident} cannot be uncoupled because it is not part of {waypoint.Locomotive.Ident}'s consist");
                return;
            }

            LogicalEnd endToUncouple = waypoint.CountUncoupledFromNearestToWaypoint ? closestEnd : GetOppositeEnd(closestEnd);

            List<Car> carsToCut = EnumerateCoupledToEnd(carToUncouple, endToUncouple, !waypoint.ExcludeMatchingCarsFromCut);
            // Reverse cars so that the car to uncouple is last
            carsToCut.Reverse();

            PerformCut(carsToCut, consist, waypoint);
        }

        private static void PerformCut(List<Car> carsToCut, List<Car> allCars, ManagedWaypoint waypoint)
        {
            carsToCut = FilterAnySplitLocoTenderPairs(carsToCut);
            List<Car> carsRemaining = [.. allCars.Where(c => !carsToCut.Contains(c))];

            List<Car> activeCut = carsRemaining;
            List<Car> inactiveCut = carsToCut;

            Loader.Log($"Seeking to uncouple {carsToCut.Count} cars from train of {allCars.Count} total cars with {carsRemaining.Count} cars left behind");

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

            Loader.Log($"Cutting {CarListToString(carsToCut)} from {CarListToString(allCars)} as {(waypoint.TakeUncoupledCarsAsActiveCut ? "active cut" : "inactive cut")}");

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
                BleedAirOnCut(inactiveCut);
            }
        }

        private static void BleedAirOnCut(List<Car> cars)
        {
            Loader.LogDebug($"Bleeding air on {cars.Count} cars");
            foreach (Car car in cars)
            {
                car.air.BleedBrakeCylinder();
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
            Loader.LogDebug($"Trying to uncouple {car.Ident} on logical end {(endToUncouple == LogicalEnd.A ? "A" : "B")}");

            if (StateManager.IsHost && car.set != null)
            {
                if (car.TryGetAdjacentCar(endToUncouple, out var adjacent))
                {
                    Loader.Log($"Uncoupling {car.Ident} and {adjacent.Ident}");
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
                else
                {
                    Loader.LogError($"No adjacent car to {car.Ident} on logical end {(endToUncouple == LogicalEnd.A ? "A" : "B")}");
                }
            }
        }

        private static void SetHandbrakes(List<Car> cars)
        {
            Traverse.Create(TrainController.Shared).Method("ApplyHandbrakesAsNeeded", [typeof(List<Car>), typeof(PlaceTrainHandbrakes)], [cars, PlaceTrainHandbrakes.Automatic]).GetValue();
        }

        internal static void ApplyTimetableSymbolIfRequested(ManagedWaypoint waypoint)
        {
            if (waypoint.TimetableSymbol == null) return;

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

            if (waypoint.TimetableSymbol == RemoveTrainSymbolString)
            {
                waypoint.TimetableSymbol = null;
            }

            StateManager.ApplyLocal(new RequestSetTrainCrewTimetableSymbol(crewId, waypoint.TimetableSymbol));
            Loader.Log($"[Timetable] {(waypoint.TimetableSymbol ?? "None")} for {waypoint.Locomotive.Ident}");
        }
    }
}
