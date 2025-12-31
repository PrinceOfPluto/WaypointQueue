using Game.Messages;
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
using static Model.Car;

namespace WaypointQueue
{
    internal class CarUtils
    {
        public static (LogicalEnd closest, LogicalEnd furthest) GetEndsRelativeToLocation(Car car, Location location)
        {
            LogicalEnd closestEnd = car.ClosestLogicalEndTo(location, Graph.Shared);
            LogicalEnd furthestEnd = closestEnd == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;
            return (closestEnd, furthestEnd);
        }

        public static LogicalEnd ClosestLogicalEndTo(Car car, Location location)
        {
            return car.ClosestLogicalEndTo(location, Graph.Shared);
        }

        public static LogicalEnd FurthestLogicalEndFrom(Car car, Location location)
        {
            LogicalEnd closestEnd = ClosestLogicalEndTo(car, location);
            LogicalEnd furthestEnd = closestEnd == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;
            return furthestEnd;
        }

        public static LogicalEnd GetOppositeEnd(LogicalEnd logicalEnd)
        {
            return logicalEnd == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;
        }

        public static LogicalEnd GetEndRelativeToWapoint(Car car, Location waypointLocation, bool useFurthestEnd)
        {
            LogicalEnd closestEnd = car.ClosestLogicalEndTo(waypointLocation, Graph.Shared);
            LogicalEnd furthestEnd = closestEnd == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;
            return useFurthestEnd ? furthestEnd : closestEnd;
        }

        public static string CarListToString(List<Car> cars)
        {
            return String.Join("-", cars.Select(c => $"[{c.Ident}]"));
        }

        public static string LogicalEndToString(LogicalEnd logicalEnd)
        {
            return logicalEnd == LogicalEnd.A ? "A" : "B";
        }

        public static void SetHandbrakesOnCut(List<Car> cars)
        {
            Loader.LogDebug($"Setting handbrakes on {cars.Count} cars: {CarListToString(cars)}");
            Traverse.Create(TrainController.Shared).Method("ApplyHandbrakesAsNeeded", [typeof(List<Car>), typeof(PlaceTrainHandbrakes)], [cars, PlaceTrainHandbrakes.Automatic]).GetValue();
        }

        public static void BleedAirOnCut(List<Car> cars)
        {
            Loader.LogDebug($"Bleeding air on {cars.Count} cars: {CarListToString(cars)}");
            foreach (Car car in cars)
            {
                car.air.BleedBrakeCylinder();
            }
        }

        public static void ConnectAir(Car car)
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

        public static void UpdateCarsForAE(BaseLocomotive locomotive)
        {
            MethodInfo updateCarsMI = AccessTools.Method(typeof(AutoEngineerPlanner), "UpdateCars");
            object[] parameters = [null];
            updateCarsMI.Invoke(locomotive.AutoEngineerPlanner, parameters);
        }

        public static List<Car> EnumerateCoupledToEnd(Car car, LogicalEnd directionToCount, bool inclusive = false)
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
    }
}
