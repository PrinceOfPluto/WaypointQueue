using Game;
using Game.Messages;
using Game.State;
using HarmonyLib;
using Model;
using Model.AI;
using Model.Ops.Timetable;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Track;
using UI.Common;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.Model;
using WaypointQueue.Services;
using WaypointQueue.UI;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    internal class WaypointResolver(UncouplingService uncouplingService, RefuelService refuelService, CouplingService couplingService, ICarService carService, AutoEngineerService autoEngineerService)
    {
        public static event Action<string> WaypointForLocoIdDidError;

        private static readonly float WaitBeforeCuttingTimeout = 15f;
        public static readonly string NoDestinationString = "No destination";
        public static readonly string RemoveTrainSymbolString = "remove-train-symbol";

        public bool HandleUnresolvedWaypoint(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper, float tickIntervalSeconds)
        {
            if (wp.Errors.Any())
            {
                return false;
            }

            try
            {
                return TryHandleUnresolvedWaypoint(wp, ordersHelper, tickIntervalSeconds);
            }
            catch (UncouplingException e)
            {
                HandleProcessingError("Uncoupling", e, wp);
                return false;
            }
            catch (CouplingException e)
            {
                HandleProcessingError("Coupling", e, wp);
                return false;
            }
            catch (RefuelException e)
            {
                HandleProcessingError("Refueling", e, wp);
                return false;
            }
            catch (Exception e)
            {
                HandleProcessingError("Waypoint", e, wp);
                return false;
            }
        }

        private void HandleProcessingError(string orderType, Exception exception, ManagedWaypoint wp)
        {
            Loader.LogError($"Exception while processing waypoint: {exception}");
            wp.Errors.Add(new WaypointError(errorType: orderType, message: exception.Message));
            wp.StatusLabel = "Paused due to error";
            WaypointQueueController.Shared.UpdateWaypoint(wp);
            ErrorModalController.Shared.ShowProcessingErrorModal(exception.Message, orderType, wp);
            WaypointForLocoIdDidError.Invoke(wp.Locomotive.id);
        }

        /**
         * Returns false when the waypoint is not yet resolved (i.e. needs to continue)
         */
        private bool TryHandleUnresolvedWaypoint(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper, float tickIntervalSeconds)
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
                    if (couplingService.FindNearbyCoupling(wp, ordersHelper))
                    {
                        return false;
                    }
                    else
                    {
                        wp.CouplingSearchMode = ManagedWaypoint.CoupleSearchMode.None;
                        Loader.LogError($"{wp.Locomotive.Ident} cannot find a nearby car to couple.");
                    }
                }

                // Try coupling to target
                if (wp.WillSeekSpecificCarCoupling && !wp.CurrentlyCouplingSpecificCar)
                {
                    if (couplingService.FindSpecificCouplingTarget(wp, ordersHelper))
                    {
                        return false;
                    }
                    else
                    {
                        wp.CouplingSearchMode = ManagedWaypoint.CoupleSearchMode.None;
                    }
                }

                if (wp.HasAnyUncouplingOrders)
                {
                ResolveUncouplingOrders(wp);
                }
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
                if (couplingService.FindNearbyCoupling(wp, ordersHelper))
                {
                    return false;
                }
                else
                {
                    wp.CouplingSearchMode = ManagedWaypoint.CoupleSearchMode.None;
                    Loader.LogError($"{wp.Locomotive.Ident} cannot find a nearby car to couple.");
                }
            }

            // Try coupling to target
            if (wp.WillSeekSpecificCarCoupling && !wp.CurrentlyCouplingSpecificCar)
            {
                if (couplingService.FindSpecificCouplingTarget(wp, ordersHelper))
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

            if (!TryResolveFuelingOrders(wp, ordersHelper))
            {
                return false;
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
                    wp.SecondsSpentWaitingBeforeCut += tickIntervalSeconds;
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

            if (wp.IsCoupling && wp.HasAnyPostCouplingCutOrders)
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

            if (TryBeginWaiting(wp))
            {
                wp.StatusLabel = "Waiting before continuing";
                WaypointQueueController.Shared.UpdateWaypoint(wp);
                return false;
            }

            return true;
        }

        private void ResolveChangeMaxSpeed(ManagedWaypoint wp)
        {
            var ordersHelper = autoEngineerService.GetOrdersHelper(wp.Locomotive);
            int maxSpeedToSet = Mathf.Clamp(wp.MaxSpeedForChange, 0, 45);
            ordersHelper.SetOrdersValue(null, null, maxSpeedMph: maxSpeedToSet, null, null);
        }

        public bool CleanupBeforeRemovingWaypoint(ManagedWaypoint wp)
        {
            if (wp.RefuelLoaderAnimated)
            {
                refuelService.SetCarLoaderSequencerWantsLoading(wp, false);
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
                refuelService.SetCarLoaderSequencerWantsLoading(wp, true);
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
                    refuelService.CleanupAfterRefuel(wp, ordersHelper);
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
            //Loader.LogDebug($"First car {firstCar.Ident} is {(firstCar.IsStopped(2) ? "stopped for 2" : "NOT stopped")} and last car {lastCar.Ident} is {(lastCar.IsStopped(2) ? "stopped for 2" : "NOT stopped")}");

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

        private bool TryBeginWaiting(ManagedWaypoint wp)
        {
            return wp.WillWait && (TryBeginWaitingDuration(wp) || TryBeginWaitingUntilTime(wp));
        }

        private bool TryBeginWaitingDuration(ManagedWaypoint wp)
        {
            if (wp.DurationOrSpecificTime == ManagedWaypoint.WaitType.Duration && wp.WaitForDurationMinutes > 0)
            {
                GameDateTime waitUntilTime = TimeWeather.Now.AddingMinutes(wp.WaitForDurationMinutes);
                wp.WaitUntilGameTotalSeconds = waitUntilTime.TotalSeconds;
                wp.CurrentlyWaiting = true;
                Loader.Log($"Loco {wp.Locomotive.Ident} waiting {wp.WaitForDurationMinutes}m until {waitUntilTime}");
                WaypointQueueController.Shared.UpdateWaypoint(wp);
                return true;
            }
            return false;
        }

        private bool TryBeginWaitingUntilTime(ManagedWaypoint wp)
        {
            if (wp.DurationOrSpecificTime == ManagedWaypoint.WaitType.SpecificTime)
            {
                if (TimetableReader.TryParseTime(wp.WaitUntilTimeString, out TimetableTime time))
                {
                    wp.SetWaitUntilByMinutes(time.Minutes, out GameDateTime waitUntilTime);
                    wp.CurrentlyWaiting = true;
                    Loader.Log($"Loco {wp.Locomotive.Ident} waiting until {waitUntilTime}");
                    WaypointQueueController.Shared.UpdateWaypoint(wp);
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

        private bool OrderClearBeyondWaypoint(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            waypoint.StopAtWaypoint = true;
            waypoint.MoveTrainPastWaypoint = false;

            Loader.Log($"Beginning order to clear {waypoint.Locomotive.Ident} train past the waypoint");
            (_, Location furthestCarLocation) = carService.GetTrainEndLocations(waypoint, out _, out _, out _);

            float totalTrainLength = GetTrainLength(waypoint.Locomotive as BaseLocomotive);

            Location orientedLocation = Graph.Shared.LocationOrientedToward(waypoint.Location, furthestCarLocation);
            Location locationToMove;

            try
            {
                locationToMove = Graph.Shared.LocationByMoving(orientedLocation, totalTrainLength, checkSwitchAgainstMovement: false, stopAtEndOfTrack: false);
                locationToMove.AssertValid();
            }
            catch (Exception e)
            {
                throw new WaypointProcessingException($"{waypoint.Locomotive.Ident} cannot fit train past the waypoint", waypoint, e);
            }

            waypoint.StatusLabel = "Sending train past waypoint";
            waypoint.OverwriteLocation(locationToMove);
            WaypointQueueController.Shared.UpdateWaypoint(waypoint);

            Loader.Log($"Sending train of {waypoint.Locomotive.Ident} to {locationToMove} past the waypoint");
            autoEngineerService.SendToWaypoint(ordersHelper, locationToMove);
            return true;
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
                    carService.ConnectAir(car);
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
            if (!waypoint.TryResolveCoupleToCar(out Car carCoupledTo))
            {
                throw new UncouplingException($"Cannot perform post coupling cut due to unresolvable CoupledToCarId {waypoint.CoupleToCarId}", waypoint);
            }

            // Take active cut not allowed for pickups or dropoffs
            waypoint.TakeUncoupledCarsAsActiveCut = false;

            bool isPickup = waypoint.PostCouplingCutMode == ManagedWaypoint.PostCoupleCutType.Pickup;

            List<Car> carsToCut = [];
            if (waypoint.WillUncoupleByCount && waypoint.NumberOfCarsToCut > 0)
            {
                carsToCut = uncouplingService.FindPickupOrDropoffByCount(waypoint, carCoupledTo);
            }
            if (waypoint.WillUncoupleByDestination && !string.IsNullOrEmpty(waypoint.UncoupleDestinationId))
            {
                carsToCut = isPickup
                    ? uncouplingService.FindPickupByDestination(waypoint, carCoupledTo) : uncouplingService.FindDropoffByDestination(waypoint, carCoupledTo);
            }

            if (waypoint.WillUncoupleBySpecificCar)
            {
                carsToCut = isPickup
                    ? uncouplingService.FindPickupBySpecificCar(waypoint, carCoupledTo) : uncouplingService.FindDropoffBySpecificCar(waypoint, carCoupledTo);
            }

            if (waypoint.WillUncoupleAllExceptLocomotives)
            {
                carsToCut = isPickup
                    ? uncouplingService.FindPickupAllExceptLocomotives(waypoint, carCoupledTo) : uncouplingService.FindDropoffAllExceptLocomotives(waypoint, carCoupledTo);
            }

            uncouplingService.PerformCut(carsToCut, waypoint);
        }

        private void ResolveUncouplingOrders(ManagedWaypoint waypoint)
        {
            Loader.Log($"Resolving uncoupling orders for {waypoint.Locomotive.Ident}");
            List<Car> carsToCut = [];
            if (waypoint.WillUncoupleByCount && waypoint.NumberOfCarsToCut > 0)
            {
                carsToCut = uncouplingService.FindCutByCount(waypoint);
            }

            if (waypoint.WillUncoupleByDestination && !string.IsNullOrEmpty(waypoint.UncoupleDestinationId))
            {
                carsToCut = uncouplingService.FindCutByDestination(waypoint);
            }

            if (waypoint.WillUncoupleBySpecificCar)
            {
                carsToCut = uncouplingService.FindCutBySpecificCar(waypoint);
            }

            if (waypoint.WillUncoupleAllExceptLocomotives)
            {
                uncouplingService.UncoupleAllExceptLocomotives(waypoint);
                return;
            }

            uncouplingService.PerformCut(carsToCut, waypoint);
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
