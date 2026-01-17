using Model;
using System;
using System.Collections.Generic;
using System.Linq;
using Track;
using Track.Search;
using UI.EngineControls;
using WaypointQueue.Model;
using WaypointQueue.UUM;
using WaypointQueue.Wrappers;
using static Model.Car;

namespace WaypointQueue.Services
{
    internal class CouplingService(ICarService carService, AutoEngineerService autoEngineerService, TrainControllerWrapper trainControllerWrapper)
    {
        private static readonly float AverageCarLengthMeters = 12.2f;

        public bool FindNearbyCoupling(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper)
        {
            return wp.OnlySeekNearbyOnTrackAhead ? FindNearbyCouplingInStraightLine(wp, ordersHelper) : FindNearbyCouplingInRadius(wp, ordersHelper);
        }

        public bool FindNearbyCouplingInStraightLine(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.LogDebug($"Starting search for nearby coupling in straight line");
            (Location closestTrainEnd, Location furthestTrainEnd) = carService.GetTrainEndLocations(wp, out float closestDistance, out Car closestCar, out Car furthestCar);
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
                    targetCar = trainControllerWrapper.CheckForCarAtLocation(checkLocation);
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
                LogicalEnd nearestEnd = carService.ClosestLogicalEndTo(targetCar, closestTrainEnd);
                Location bestLocation;
                if (!targetCar[nearestEnd].IsCoupled)
                {
                    Loader.Log($"Closest end of {targetCar.Ident} is available to couple");
                    bestLocation = GetCouplerLocation(targetCar, nearestEnd);
                }
                else
                {
                    throw new CouplingException($"Closest end of {targetCar.Ident} is not available for {wp.Locomotive.Ident} to couple.", wp);
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
                autoEngineerService.SendToWaypoint(ordersHelper, bestLocation, targetCar.id);
                return true;
            }

            throw new CouplingException($"Found no nearby cars to couple for {wp.Locomotive.Ident} within straight line track search distance of {Loader.Settings.NearbyCouplingSearchDistanceInCarLengths} car lengths.", wp);
        }

        public bool FindNearbyCouplingInRadius(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.LogDebug($"Starting search for nearby coupling in radius");
            float searchRadius = Loader.Settings.NearbyCouplingSearchDistanceInCarLengths * AverageCarLengthMeters;
            List<string> alreadyCoupledIds = [.. wp.Locomotive.EnumerateCoupled().Select(c => c.id)];
            List<string> nearbyCarIds = [.. trainControllerWrapper
                .GetNearbyCarIds(wp.Location, searchRadius)
                .Where(cid => !alreadyCoupledIds.Contains(cid))];

            Car bestMatchCar = null;
            Location bestMatchLocation = default;
            float bestMatchDistance = 100000;

            foreach (var carId in nearbyCarIds)
            {
                if (trainControllerWrapper.TryGetCarForId(carId, out Car car))
                {
                    if (car.EndGearA.IsCoupled && car.EndGearB.IsCoupled)
                    {
                        continue;
                    }

                    LogicalEnd nearestEnd = carService.ClosestLogicalEndTo(car, wp.Location);
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
                autoEngineerService.SendToWaypoint(ordersHelper, adjustedLocation, bestMatchCar.id);
                return true;
            }

            throw new CouplingException($"Found no nearby cars to couple for {wp.Locomotive.Ident} within search radius of {Loader.Settings.NearbyCouplingSearchDistanceInCarLengths} car lengths.", wp);
        }

        public bool FindSpecificCouplingTarget(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            Car targetCar = waypoint.CouplingSearchResultCar;
            if (targetCar == null && !waypoint.TryResolveCouplingSearchText(out targetCar))
            {
                throw new CouplingException($"Cannot find valid car matching \"{waypoint.CouplingSearchText}\" for {waypoint.Locomotive.Ident} to couple", waypoint);
            }

            LogicalEnd nearestEnd = carService.ClosestLogicalEndTo(targetCar, waypoint.Locomotive.OpsLocation);

            Location bestLocation;

            if (!targetCar[nearestEnd].IsCoupled)
            {
                Loader.Log($"Closest end of {targetCar.Ident} is available to couple");
                bestLocation = GetCouplerLocation(targetCar, nearestEnd);
            }
            else if (!targetCar[carService.GetOppositeEnd(nearestEnd)].IsCoupled)
            {
                Loader.Log($"Furthest end of {targetCar.Ident} is available to couple");
                bestLocation = GetCouplerLocation(targetCar, carService.GetOppositeEnd(nearestEnd));
            }
            else
            {
                Loader.Log($"Both ends of {targetCar.Ident} are unavailable to couple");
                throw new CouplingException($"Both ends of {targetCar.Ident} are unavailable for {waypoint.Locomotive.Ident} to couple", waypoint);
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
                autoEngineerService.SendToWaypoint(ordersHelper, bestLocation, targetCar.id);
                return true;
            }

            throw new CouplingException($"Location {bestLocation} is not valid for {waypoint.Locomotive.Ident} to couple to {targetCar.Ident}.", waypoint);
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
    }
}
