using Game.State;
using HarmonyLib;
using Model;
using Model.AI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Track;
using WaypointQueue.UUM;
using WaypointQueue.Wrappers;
using static Model.Car;

namespace WaypointQueue.Services
{
    internal class CarService(TrainControllerWrapper trainControllerWrapper) : ICarService
    {
        public void SetHandbrakesOnCut(List<Car> cars)
        {
            Loader.LogDebug($"Setting handbrakes on {cars.Count} cars: {CarUtils.CarListToString(cars)}");
            trainControllerWrapper.ApplyHandbrakesAsNeeded(cars);
        }

        public void BleedAirOnCut(List<Car> cars)
        {
            Loader.LogDebug($"Bleeding air on {cars.Count} cars: {CarUtils.CarListToString(cars)}");
            foreach (Car car in cars)
            {
                car.air.BleedBrakeCylinder();
            }
        }

        public void ConnectAir(Car car)
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

        public void UpdateCarsForAE(BaseLocomotive locomotive)
        {
            MethodInfo updateCarsMI = AccessTools.Method(typeof(AutoEngineerPlanner), "UpdateCars");
            object[] parameters = [null];
            updateCarsMI.Invoke(locomotive.AutoEngineerPlanner, parameters);
        }

        public List<Car> EnumerateCoupled(Car car, LogicalEnd fromEnd)
        {
            return [.. car.EnumerateCoupled(fromEnd)];
        }

        public List<Car> EnumerateAdjacentCarsTowardEnd(Car car, LogicalEnd directionToCount, bool inclusive = false)
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


        public (Location closest, Location furthest) GetTrainEndLocations(ManagedWaypoint waypoint, out float closestDistance, out Car closestCar, out Car furthestCar)
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

        public LogicalEnd ClosestLogicalEndTo(Car car, Location location)
        {
            return car.ClosestLogicalEndTo(location, Graph.Shared);
        }

        public LogicalEnd GetOppositeEnd(LogicalEnd logicalEnd)
        {
            return logicalEnd == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;
        }
        private bool TryGetOpenEndForCar(Car car, out LogicalEnd logicalEnd)
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

        public LogicalEnd GetEndRelativeToWaypoint(Car car, Location waypointLocation, bool useFurthestEnd)
        {
            LogicalEnd closestEnd = car.ClosestLogicalEndTo(waypointLocation, Graph.Shared);
            LogicalEnd furthestEnd = closestEnd == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;
            return useFurthestEnd ? furthestEnd : closestEnd;
        }

        public (LogicalEnd closest, LogicalEnd furthest) GetEndsRelativeToLocation(Car car, Location location)
        {
            LogicalEnd closestEnd = car.ClosestLogicalEndTo(location, Graph.Shared);
            LogicalEnd furthestEnd = closestEnd == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;
            return (closestEnd, furthestEnd);
        }

        public List<Car> FilterAnySplitLocoTenderPairs(List<Car> carsToCut)
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

        public bool IsCarLocomotiveType(Car car)
        {
            return car.Archetype == Model.Definition.CarArchetype.LocomotiveDiesel || car.Archetype == Model.Definition.CarArchetype.LocomotiveSteam || car.Archetype == Model.Definition.CarArchetype.Tender;
        }
    }
}
