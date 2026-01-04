using Game;
using Game.Messages;
using Game.State;
using HarmonyLib;
using Helpers;
using Model;
using Model.AI;
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
using WaypointQueue.Services;
using WaypointQueue.UUM;
using static Model.Car;
using static WaypointQueue.CarUtils;

namespace WaypointQueue
{
    internal class WaypointResolver(UncouplingService uncouplingHandler, RefuelService refuelService)
    {
        private static readonly float WaitBeforeCuttingTimeout = 5f;
        private static readonly float AverageCarLengthMeters = 12.2f;
        public static readonly string NoDestinationString = "No destination";
        public static readonly string RemoveTrainSymbolString = "remove-train-symbol";

        /**
         * Returns false when the waypoint is not yet resolved (i.e. needs to continue)
         */
        public bool TryHandleUnresolvedWaypoint(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper, Action<ManagedWaypoint> onWaypointDidUpdate)
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

        private void ResolveChangeMaxSpeed(ManagedWaypoint wp)
        {
            var ordersHelper = WaypointQueueController.Shared.GetOrdersHelper(wp.Locomotive);
            int maxSpeedToSet = Mathf.Clamp(wp.MaxSpeedForChange, 0, 45);
            ordersHelper.SetOrdersValue(null, null, maxSpeedMph: maxSpeedToSet, null, null);
        }

        public bool CleanupBeforeRemovingWaypoint(ManagedWaypoint wp)
        {
            if (wp.RefuelLoaderAnimated)
            {
                SetCarLoaderSequencerWantsLoading(wp, false);
            }
            return true;
        }

        private bool TryResolveFuelingOrders(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper)
        {
            // Reposition to refuel
            if (wp.WillRefuel && !wp.CurrentlyRefueling && !wp.HasAnyCouplingOrders && !wp.MoveTrainPastWaypoint)
            {
                refuelService.OrderToRefuel(wp, ordersHelper);
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
                if (refuelService.IsDoneRefueling(wp))
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

        internal bool IsTrainStopped(ManagedWaypoint wp)
        {
            List<Car> coupled = [.. wp.Locomotive.EnumerateCoupled()];
            Car firstCar = coupled.First();
            Car lastCar = coupled.Last();
            Loader.LogDebug($"First car {firstCar.Ident} is {(firstCar.IsStopped(2) ? "stopped for 2" : "NOT stopped")} and last car {lastCar.Ident} is {(lastCar.IsStopped(2) ? "stopped for 2" : "NOT stopped")}");

            return firstCar.IsStopped(2) && lastCar.IsStopped(2);
        }

        private bool TryEndWaiting(ManagedWaypoint wp)
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

        private bool TryBeginWaiting(ManagedWaypoint wp, Action<ManagedWaypoint> onWaypointsUpdated)
        {
            return wp.WillWait && (TryBeginWaitingDuration(wp, onWaypointsUpdated) || TryBeginWaitingUntilTime(wp, onWaypointsUpdated));
        }

        private bool TryBeginWaitingDuration(ManagedWaypoint wp, Action<ManagedWaypoint> onWaypointsUpdated)
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

        private bool TryBeginWaitingUntilTime(ManagedWaypoint wp, Action<ManagedWaypoint> onWaypointsUpdated)
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

        private bool FindNearbyCoupling(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper)
        {
            return wp.OnlySeekNearbyOnTrackAhead ? FindNearbyCouplingInStraightLine(wp, ordersHelper) : FindNearbyCouplingInRadius(wp, ordersHelper);
        }

        private bool FindNearbyCouplingInStraightLine(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper)
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

        private bool FindNearbyCouplingInRadius(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper)
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

        private bool FindSpecificCouplingTarget(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
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

        private Location GetCouplerLocation(Car car, LogicalEnd logicalEnd)
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

        private bool OrderClearBeyondWaypoint(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
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

        private void CleanupAfterRefuel(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper)
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

        private void SetCarLoaderSequencerWantsLoading(ManagedWaypoint waypoint, bool value)
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

        private CarLoadTargetLoader FindCarLoadTargetLoader(ManagedWaypoint waypoint)
        {
            WaypointQueueController.Shared.InitCarLoaders();
            Vector3 worldPosition = WorldTransformer.GameToWorld(waypoint.RefuelPoint);
            //Loader.LogDebug($"Starting search for target loader matching world point {worldPosition}");
            CarLoadTargetLoader loader = WaypointQueueController.Shared.CarLoadTargetLoaders.Find(l => l.transform.position == worldPosition);
            //Loader.LogDebug($"Found matching {loader.load.name} loader at game point {waypoint.RefuelPoint}");
            return loader;
        }

        private float GetTrainLength(BaseLocomotive locomotive)
        {
            MethodInfo calculateTotalLengthMI = AccessTools.Method(typeof(AutoEngineerPlanner), "CalculateTotalLength");
            float totalTrainLength = (float)calculateTotalLengthMI.Invoke(locomotive.AutoEngineerPlanner, []);
            return totalTrainLength;
        }

        private void ResolveBrakeSystemOnCouple(ManagedWaypoint waypoint)
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

        private void ResolvePostCouplingCut(ManagedWaypoint waypoint)
        {
            uncouplingHandler.PostCouplingCutByCount(waypoint);
        }

        private void ResolveUncouplingOrders(ManagedWaypoint waypoint)
        {
            if (waypoint.WillUncoupleByCount && waypoint.NumberOfCarsToCut > 0)
            {
                uncouplingHandler.UncoupleByCount(waypoint);
            }

            if (waypoint.WillUncoupleByDestination && !string.IsNullOrEmpty(waypoint.UncoupleDestinationId))
            {
                uncouplingHandler.UncoupleByDestination(waypoint);
            }

            if (waypoint.WillUncoupleBySpecificCar)
            {
                uncouplingHandler.UncoupleBySpecificCar(waypoint);
            }

            if (waypoint.WillUncoupleAllExceptLocomotives)
            {
                uncouplingHandler.UncoupleAllExceptLocomotives(waypoint);
            }
        }

        internal void ApplyTimetableSymbolIfRequested(ManagedWaypoint waypoint)
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
