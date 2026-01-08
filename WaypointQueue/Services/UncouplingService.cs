using Game.State;
using Model;
using Model.Definition;
using Model.Ops;
using System;
using System.Collections.Generic;
using System.Linq;
using UI.Common;
using UnityEngine;
using WaypointQueue.Services;
using WaypointQueue.UUM;
using WaypointQueue.Wrappers;
using static Model.Car;
using static WaypointQueue.CarUtils;

namespace WaypointQueue
{
    internal class UncouplingService(ICarService carService, IOpsControllerWrapper opsControllerWrapper)
    {
        public void UncoupleByCount(ManagedWaypoint waypoint)
        {
            if (!waypoint.WillUncoupleByCount && waypoint.NumberOfCarsToCut <= 0) return;
            Loader.Log($"Resolving uncoupling orders for {waypoint.Locomotive.Ident}");

            LogicalEnd directionToCountCars = carService.GetEndRelativeToWaypoint(waypoint.Locomotive, waypoint.Location, useFurthestEnd: !waypoint.CountUncoupledFromNearestToWaypoint);

            List<Car> allCarsFromEnd = waypoint.Locomotive.EnumerateCoupled(directionToCountCars).ToList();

            // Handling the direction ensures cars to cut are at the front of the list
            List<Car> carsToCut = allCarsFromEnd.Take(waypoint.NumberOfCarsToCut).ToList();

            carsToCut = carService.FilterAnySplitLocoTenderPairs(carsToCut);

            PerformCut(carsToCut, allCarsFromEnd, waypoint);
        }

        public void UncoupleByDestination(ManagedWaypoint waypoint)
        {
            Loader.Log($"Resolving uncouple by destination for {waypoint.Locomotive.Ident}");
            LogicalEnd directionToCountCars = carService.GetEndRelativeToWaypoint(waypoint.Locomotive, waypoint.Location, useFurthestEnd: !waypoint.CountUncoupledFromNearestToWaypoint);

            List<Car> allCarsFromEnd = waypoint.Locomotive.EnumerateCoupled(directionToCountCars).ToList();
            Loader.LogDebug($"Enumerating all cars of {waypoint.Locomotive.Ident} from logical end {LogicalEndToString(directionToCountCars)}:\n{CarListToString(allCarsFromEnd)}");

            List<Car> carsToCut = [];
            if (waypoint.UncoupleDestinationId == WaypointResolver.NoDestinationString)
            {
                carsToCut = FindMatchingCarsByNoDestination(allCarsFromEnd, waypoint.ExcludeMatchingCarsFromCut);
            }
            if (waypoint.WillUncoupleByDestinationTrack)
            {
                if (opsControllerWrapper.TryResolveOpsCarPosition(waypoint.UncoupleDestinationId, out OpsCarPosition destinationMatch))
                {
                    carsToCut = FindMatchingCarsByTrackDestination(allCarsFromEnd, destinationMatch, waypoint.ExcludeMatchingCarsFromCut);
                }
                else
                {
                    Toast.Present($"{waypoint.Locomotive.Ident} failed to resolve unknown track destination.");
                    Loader.LogError($"Failed to resolve track destination by id {waypoint.UncoupleDestinationId}");
                    return;
                }
            }
            if (waypoint.WillUncoupleByDestinationIndustry)
            {
                Industry industryMatch = opsControllerWrapper.GetIndustryById(waypoint.UncoupleDestinationId);
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
                Area areaMatch = opsControllerWrapper.GetAreaById(waypoint.UncoupleDestinationId);
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

        private List<Car> FindMatchingCarBlock(List<Car> allCars, Func<Car, bool> matchFunction, bool excludeMatchingCars)
        {
            List<Car> result = [];

            bool foundBlock = false;
            for (int i = 0; i < allCars.Count; i++)
            {
                Car car = allCars[i];

                bool carMatchesFilter = matchFunction(car);

                if (foundBlock && !carMatchesFilter)
                {
                    break;
                }

                if (carMatchesFilter)
                {
                    foundBlock = true;
                    if (!excludeMatchingCars)
                    {
                        Loader.LogDebug($"Adding matching {car.Ident} to list");
                        result.Add(car);
                    }
                }
                else
                {
                    Loader.LogDebug($"Adding non-matching {car.Ident} to list");
                    result.Add(car);
                }
            }

            return result;
        }

        private List<Car> FindMatchingCarsByNoDestination(List<Car> allCars, bool excludeMatchingCarsFromCut)
        {
            bool matchFunction(Car car)
            {
                bool hasDestination = opsControllerWrapper.TryGetCarDesination(car, out _);
                return !hasDestination;
            }
            return FindMatchingCarBlock(allCars, matchFunction, excludeMatchingCarsFromCut);
        }

        private List<Car> FindMatchingCarsByTrackDestination(List<Car> allCars, OpsCarPosition destinationMatch, bool excludeMatchingCarsFromCut)
        {
            bool matchFunction(Car car)
            {
                bool hasDestination = opsControllerWrapper.TryGetCarDesination(car, out OpsCarPosition carDestination);
                bool carMatchesFilter = hasDestination && carDestination.DisplayName == destinationMatch.DisplayName;
                Loader.LogDebug(carMatchesFilter ? $"Car {car.Ident} matches filter of {destinationMatch.DisplayName}" : $"Car {car.Ident} does NOT match filter of {destinationMatch.DisplayName}");
                return carMatchesFilter;
            }
            return FindMatchingCarBlock(allCars, matchFunction, excludeMatchingCarsFromCut);
        }

        private List<Car> FindMatchingCarsByIndustryDestination(List<Car> allCars, Industry destinationMatch, bool excludeMatchingCarsFromCut)
        {
            bool matchFunction(Car car)
            {
                bool carMatchesFilter = false;

                if (opsControllerWrapper.TryGetCarDesination(car, out OpsCarPosition carDestination))
                {
                    IndustryComponent industryComponent = opsControllerWrapper.IndustryComponentForPosition(carDestination);
                    carMatchesFilter = industryComponent?.Industry?.identifier == destinationMatch.identifier;
                }
                Loader.LogDebug(carMatchesFilter ? $"Car {car.Ident} matches filter of {destinationMatch.name}" : $"Car {car.Ident} does NOT match filter of {destinationMatch.name}");
                return carMatchesFilter;
            }
            return FindMatchingCarBlock(allCars, matchFunction, excludeMatchingCarsFromCut);
        }

        private List<Car> FindMatchingCarsByAreaDestination(List<Car> allCars, Area destinationMatch, bool excludeMatchingCarsFromCut)
        {
            bool matchFunction(Car car)
            {
                bool carMatchesFilter = false;

                if (opsControllerWrapper.TryGetCarDesination(car, out OpsCarPosition carDestination))
                {
                    Area carArea = opsControllerWrapper.AreaForCarPosition(carDestination);
                    carMatchesFilter = carArea?.identifier == destinationMatch.identifier;
                }

                Loader.LogDebug(carMatchesFilter ? $"Car {car.Ident} matches filter of {destinationMatch.name}" : $"Car {car.Ident} does NOT match filter of {destinationMatch.name}");
                return carMatchesFilter;
            }
            return FindMatchingCarBlock(allCars, matchFunction, excludeMatchingCarsFromCut);
        }

        public void UncoupleBySpecificCar(ManagedWaypoint waypoint)
        {
            if (!waypoint.TryResolveUncouplingSearchText(out Car carToUncouple))
            {
                Toast.Present($"Cannot find valid car matching \"{waypoint.UncouplingSearchText}\" for {waypoint.Locomotive.Ident} to uncouple");
                Loader.LogError($"Cannot find valid car matching \"{waypoint.UncouplingSearchText}\" for {waypoint.Locomotive.Ident} to uncouple");
                return;
            }

            LogicalEnd closestEnd = carService.ClosestLogicalEndTo(carToUncouple, waypoint.Location);
            List<Car> consist = [.. waypoint.Locomotive.EnumerateCoupled(closestEnd)];
            if (!consist.Any(c => c.id == carToUncouple.id))
            {
                Toast.Present($"{carToUncouple.Ident} cannot be uncoupled because it is not part of {waypoint.Locomotive.Ident}'s consist");
                return;
            }

            LogicalEnd endToUncouple = waypoint.CountUncoupledFromNearestToWaypoint ? closestEnd : carService.GetOppositeEnd(closestEnd);

            List<Car> carsToCut = carService.EnumerateCoupledToEnd(carToUncouple, endToUncouple, !waypoint.ExcludeMatchingCarsFromCut);
            // Reverse cars so that the car to uncouple is last
            carsToCut.Reverse();

            PerformCut(carsToCut, consist, waypoint);
        }

        public void UncoupleAllExceptLocomotives(ManagedWaypoint waypoint)
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
                            carService.SetHandbrakesOnCut(block);
                        }

                        if (waypoint.BleedAirOnUncouple)
                        {
                            carService.BleedAirOnCut(block);
                        }
                    }
                }
            }

            carService.UpdateCarsForAE(waypoint.Locomotive as BaseLocomotive);
        }

        public void PostCouplingCutByCount(ManagedWaypoint wp)
        {
            if (wp.NumberOfCarsToCut <= 0)
            {
                Loader.Log($"Number of cars to cut is zero for a post coupling cut by count");
                return;
            }

            Loader.Log($"Handling pickup by count for locomotive {wp.Locomotive.Ident}");
            if (!wp.TryResolveCoupleToCar(out Car carCoupledTo))
            {
                throw new InvalidOperationException("Cannot pickup by count due to unresolved CoupledToCarId");
            }

            (LogicalEnd _, LogicalEnd farEnd) = carService.GetEndsRelativeToLocation(carCoupledTo, wp.Location);

            List<Car> fullConsist = [.. carCoupledTo.EnumerateCoupled(fromEnd: farEnd)];

            int indexOfCoupledCar = fullConsist.IndexOf(carCoupledTo);

            List<Car> carsToCut = [];

            if (wp.TakeOrLeaveCut == ManagedWaypoint.PostCoupleCutType.Take)
            {
                carsToCut = CalculateCutForPickupByCount(fullConsist, indexOfCoupledCar, wp.NumberOfCarsToCut);
                Loader.LogDebug($"Pickup seeking to cut {CarListToString(carsToCut)} from {CarListToString(fullConsist)}");
            }
            if (wp.TakeOrLeaveCut == ManagedWaypoint.PostCoupleCutType.Leave)
            {
                carsToCut = CalculateCutForDropoffByCount(fullConsist, indexOfCoupledCar, wp.NumberOfCarsToCut);
                Loader.LogDebug($"Dropoff seeking to cut {CarListToString(carsToCut)} from {CarListToString(fullConsist)}");
            }

            PerformCut(carsToCut, fullConsist, wp);
        }

        internal List<Car> CalculateCutForPickupByCount(List<Car> consist, int indexOfCoupledCar, int carsToPickup)
        {
            int maxCarsAvailableToPickup = indexOfCoupledCar + 1;

            int clampedNumberOfCarsToCut = Mathf.Clamp(carsToPickup, 0, maxCarsAvailableToPickup);
            Loader.LogDebug($"Clamped number of cars to cut is {clampedNumberOfCarsToCut}");

            int lowestIndexToPickup = indexOfCoupledCar - clampedNumberOfCarsToCut + 1;

            List<Car> carsToCut = consist.GetRange(0, lowestIndexToPickup);
            return carsToCut;
        }

        internal List<Car> CalculateCutForDropoffByCount(List<Car> consist, int indexOfCoupledCar, int carsToDropoff)
        {
            int maxCarsAvailableToDropoff = consist.Count - indexOfCoupledCar - 1;

            int clampedNumberOfCarsToCut = Mathf.Clamp(carsToDropoff, 0, maxCarsAvailableToDropoff);
            Loader.LogDebug($"Clamped number of cars to cut is {clampedNumberOfCarsToCut}");

            int highestIndexToDropoff = indexOfCoupledCar + clampedNumberOfCarsToCut;
            List<Car> carsToCut = consist.GetRange(0, highestIndexToDropoff + 1);
            return carsToCut;
        }

        internal (Car carToUncouple, LogicalEnd endToUncouple) FindCarToUncouple(List<Car> carsToCut, List<Car> consistFromEndA)
        {
            if (carsToCut == null || carsToCut.Count == 0)
            {
                throw new InvalidOperationException("Cannot calculate uncoupling point for empty cut list");
            }

            if (carsToCut.Count >= consistFromEndA.Count)
            {
                throw new InvalidOperationException("Cannot uncouple full consist");
            }

            int startIndex = consistFromEndA.FindIndex(c => c.id == carsToCut.First().id);
            int endIndex = consistFromEndA.FindIndex(c => c.id == carsToCut.Last().id);

            if (startIndex == -1 || endIndex == -1)
            {
                throw new InvalidOperationException("Cars to cut are not a subset of consist");
            }

            // Ensure indices are sorted the same way as the full consist
            if (startIndex > endIndex)
            {
                (endIndex, startIndex) = (startIndex, endIndex);
            }

            Car carToUncouple;
            if (startIndex == 0)
            {
                // Uncouple car at end index on side B
                carToUncouple = consistFromEndA[endIndex];
                return (carToUncouple, LogicalEnd.B);
            }
            else if (endIndex == consistFromEndA.Count - 1)
            {
                // Uncouple car at start index on side A
                carToUncouple = consistFromEndA[startIndex];
                return (carToUncouple, LogicalEnd.A);
            }

            Loader.LogError($"Cannot determine car to uncouple from cut list {CarListToString(carsToCut)} within the full consist {CarListToString(consistFromEndA)}");
            throw new InvalidOperationException("Failed to find valid car to uncouple");
        }

        private void PerformCut(List<Car> carsToCut, List<Car> allCars, ManagedWaypoint waypoint)
        {
            Loader.LogDebug($"Entering PerformCut before filtering split tenders");
            carsToCut = carService.FilterAnySplitLocoTenderPairs(carsToCut);

            if (carsToCut.Count == 0)
            {
                Toast.Present($"{waypoint.Locomotive.Ident} found no valid cars to cut");
                return;
            }

            List<Car> fullConsistFromEndA = [.. carsToCut.First().EnumerateCoupled(fromEnd: LogicalEnd.A)];

            Loader.Log($"Uncoupling {carsToCut.Count} cars from consist of {fullConsistFromEndA.Count} cars:\n" +
                $"cutting: {CarListToString(carsToCut)}\n" +
                $"from: {CarListToString(fullConsistFromEndA)}\n" +
                $"as: {(waypoint.TakeUncoupledCarsAsActiveCut ? "active cut" : "inactive cut")}");

            (Car carToUncouple, LogicalEnd endToUncouple) = FindCarToUncouple(carsToCut, fullConsistFromEndA);

            Loader.Log($"Uncoupling {carToUncouple.Ident} on end {LogicalEndToString(endToUncouple)} for cut of {carsToCut.Count} cars");
            UncoupleCar(carToUncouple, endToUncouple);

            carService.UpdateCarsForAE(waypoint.Locomotive as BaseLocomotive);

            List<Car> carsRemaining = [.. fullConsistFromEndA.Where(c => !carsToCut.Contains(c))];
            List<Car> inactiveCut = waypoint.TakeUncoupledCarsAsActiveCut ? carsToCut : carsRemaining;
            if (waypoint.ApplyHandbrakesOnUncouple)
            {
                carService.SetHandbrakesOnCut(inactiveCut);
            }

            if (waypoint.BleedAirOnUncouple)
            {
                carService.BleedAirOnCut(inactiveCut);
            }
        }

        private void UncoupleCar(Car car, LogicalEnd endToUncouple)
        {
            LogicalEnd oppositeEnd = endToUncouple == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;
            Loader.LogDebug($"Trying to uncouple {car.Ident} on logical end {LogicalEndToString(endToUncouple)}");

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
                    Loader.LogError($"No adjacent car to {car.Ident} on logical end {LogicalEndToString(endToUncouple)}");
                }
            }
        }
    }
}
