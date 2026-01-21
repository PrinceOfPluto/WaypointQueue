using Game.State;
using Model;
using Model.Definition;
using Model.Ops;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using WaypointQueue.Model;
using WaypointQueue.Services;
using WaypointQueue.UUM;
using WaypointQueue.Wrappers;
using static Model.Car;
using static WaypointQueue.CarUtils;

namespace WaypointQueue
{
    internal class UncouplingService(ICarService carService, IOpsControllerWrapper opsControllerWrapper)
    {
        public List<Car> FindCutByCount(ManagedWaypoint wp)
        {
            List<Car> consistFromEnd = GetConsistForCutFromEnd(wp);

            List<Car> carsToCut = [.. consistFromEnd.Take(wp.NumberOfCarsToCut)];
            return carsToCut;
        }

        public List<Car> FindPickupOrDropoffByCount(ManagedWaypoint wp, Car carCoupledTo)
        {
            List<Car> carsToCut = [];

            if (wp.NumberOfCarsToCut <= 0)
            {
                return carsToCut;
            }

            // Direction of coupling means we always use the furthest logical end of the coupled car
            (LogicalEnd _, LogicalEnd farEnd) = carService.GetEndsRelativeToLocation(carCoupledTo, wp.Location);

            List<Car> fullConsist = carService.EnumerateCoupled(carCoupledTo, farEnd);

            int indexOfCoupledCar = fullConsist.FindIndex(c => c.id == carCoupledTo.id);

            if (wp.PostCouplingCutMode == ManagedWaypoint.PostCoupleCutType.Pickup)
            {
                carsToCut = CalculateCutForPickupByCount(fullConsist, indexOfCoupledCar, wp.NumberOfCarsToCut);
                Loader.LogDebug($"Pickup seeking to cut {CarListToString(carsToCut)} from {CarListToString(fullConsist)}");
            }
            if (wp.PostCouplingCutMode == ManagedWaypoint.PostCoupleCutType.Dropoff)
            {
                carsToCut = CalculateCutForDropoffByCount(fullConsist, indexOfCoupledCar, wp.NumberOfCarsToCut);
                Loader.LogDebug($"Dropoff seeking to cut {CarListToString(carsToCut)} from {CarListToString(fullConsist)}");
            }

            return carsToCut;
        }

        private List<Car> CalculateCutForPickupByCount(List<Car> consist, int indexOfCoupledCar, int carsToPickup)
        {
            int maxCarsAvailableToPickup = indexOfCoupledCar + 1;

            int clampedNumberOfCarsToCut = Mathf.Clamp(carsToPickup, 0, maxCarsAvailableToPickup);
            Loader.LogDebug($"Clamped number of cars to cut is {clampedNumberOfCarsToCut}");

            int lowestIndexToPickup = indexOfCoupledCar - clampedNumberOfCarsToCut + 1;

            List<Car> carsToCut = consist.GetRange(0, lowestIndexToPickup);
            return carsToCut;
        }

        private List<Car> CalculateCutForDropoffByCount(List<Car> consist, int indexOfCoupledCar, int carsToDropoff)
        {
            int maxCarsAvailableToDropoff = consist.Count - indexOfCoupledCar - 1;

            int clampedNumberOfCarsToCut = Mathf.Clamp(carsToDropoff, 0, maxCarsAvailableToDropoff);
            Loader.LogDebug($"Clamped number of cars to cut is {clampedNumberOfCarsToCut}");

            int highestIndexToDropoff = indexOfCoupledCar + clampedNumberOfCarsToCut;
            List<Car> carsToCut = consist.GetRange(0, highestIndexToDropoff + 1);
            return carsToCut;
        }



        public List<Car> FindCutByDestination(ManagedWaypoint wp)
        {
            List<Car> consistFromEnd = GetConsistForCutFromEnd(wp);
            var matchFunction = GetDestinationMatchFunction(wp, isPostCoupleCut: false);
            List<Car> carsToCut = matchFunction(consistFromEnd);
            return carsToCut;
        }

        public List<Car> FindPickupByDestination(ManagedWaypoint wp, Car carCoupledTo)
        {
            List<Car> carsAvailableForPickup = GetCarsAvailableForPickup(wp, carCoupledTo);

            var pickupCalcFunction = GetDestinationMatchFunction(wp, isPostCoupleCut: true);

            if (wp.CountUncoupledFromNearestToWaypoint)
            {
                carsAvailableForPickup.Reverse();
                List<Car> carsToPickup = pickupCalcFunction(carsAvailableForPickup);
                List<Car> carsNotPickedUp = [.. carsAvailableForPickup.Where(c => !carsToPickup.Any(p => p.id == c.id))];
                return carsNotPickedUp;
            }
            else
            {
                List<Car> carsToCut = pickupCalcFunction(carsAvailableForPickup);
                return carsToCut;
            }
        }

        public List<Car> FindDropoffByDestination(ManagedWaypoint wp, Car carCoupledTo)
        {
            List<Car> carsAvailableForDropoff = GetCarsAvailableForDropoff(wp, carCoupledTo, out List<Car> consistFromFarEnd);
            var dropoffCalcFunction = GetDestinationMatchFunction(wp, isPostCoupleCut: true);

            if (wp.CountUncoupledFromNearestToWaypoint)
            {
                List<Car> carsToDropoff = dropoffCalcFunction(carsAvailableForDropoff);
                List<Car> carsToCut = [];
                if (carsToDropoff.Count > 0)
                {
                    // need to add cars before the last car
                    Car lastCar = carsToDropoff.Last();
                    int indexOfLastCarToDropoff = consistFromFarEnd.FindIndex(c => c.id == lastCar.id);
                    carsToCut = consistFromFarEnd.GetRange(0, indexOfLastCarToDropoff + 1);
                }
                return carsToCut;
            }
            else
            {
                carsAvailableForDropoff.Reverse();
                // if we're dropping off furthest block, we invert the exclude because we're matching from the other end
                List<Car> carsToKeep = dropoffCalcFunction(carsAvailableForDropoff);
                List<Car> carsToCut = [.. consistFromFarEnd.Where(c => carsToKeep.All(x => x.id != c.id))];
                return carsToCut;
            }
        }

        private Func<List<Car>, List<Car>> GetDestinationMatchFunction(ManagedWaypoint wp, bool isPostCoupleCut)
        {
            bool excludeMatchFromCut = wp.ExcludeMatchingCarsFromCut;

            if (isPostCoupleCut && !wp.CountUncoupledFromNearestToWaypoint)
            {
                // If using the furthest block on pickup or dropoff, invert the exclude
                excludeMatchFromCut = !wp.ExcludeMatchingCarsFromCut;
            }

            if (wp.WillUncoupleByNoDestination)
            {
                return (cars) => GetMatchingCarsByNoDestination(cars, excludeMatchFromCut);
            }
            if (wp.WillUncoupleByDestinationTrack)
            {
                OpsCarPosition destinationMatch = TryGetDestinationTrackMatch(wp);
                return (cars) => GetMatchingCarsByDestinationTrack(cars, destinationMatch, excludeMatchFromCut);
            }
            if (wp.WillUncoupleByDestinationIndustry)
            {
                Industry industryMatch = TryGetDestinationIndustryMatch(wp);
                return (cars) => GetMatchingCarsByDestinationIndustry(cars, industryMatch, excludeMatchFromCut);
            }
            if (wp.WillUncoupleByDestinationArea)
            {
                Area areaMatch = TryGetDestinationAreaMatch(wp);
                return (cars) => GetMatchingCarsByDestinationArea(cars, areaMatch, excludeMatchFromCut);
            }
            throw new UncouplingException("Failed to find a valid destination match function.", wp);
        }

        private List<Car> GetMatchingCarsByNoDestination(List<Car> consist, bool excludeMatchFromCut)
        {
            bool matchFunction(Car car)
            {
                bool hasDestination = opsControllerWrapper.TryGetCarDesination(car, out _);
                return !hasDestination;
            }
            return FindMatchingCarBlock(consist, matchFunction, excludeMatchFromCut);
        }

        private List<Car> GetMatchingCarsByDestinationTrack(List<Car> consist, OpsCarPosition destinationMatch, bool excludeMatchFromCut)
        {
            bool matchFunction(Car car)
            {
                bool hasDestination = opsControllerWrapper.TryGetCarDesination(car, out OpsCarPosition carDestination);
                bool carMatchesFilter = hasDestination && carDestination.DisplayName == destinationMatch.DisplayName;
                return carMatchesFilter;
            }
            return FindMatchingCarBlock(consist, matchFunction, excludeMatchFromCut);
        }

        private List<Car> GetMatchingCarsByDestinationIndustry(List<Car> consist, Industry industryMatch, bool excludeMatchFromCut)
        {
            bool matchFunction(Car car)
            {
                bool carMatchesFilter = false;

                if (opsControllerWrapper.TryGetCarDesination(car, out OpsCarPosition carDestination))
                {
                    IndustryComponent industryComponent = opsControllerWrapper.IndustryComponentForPosition(carDestination);
                    carMatchesFilter = industryComponent?.Industry?.identifier == industryMatch.identifier;
                }
                return carMatchesFilter;
            }
            return FindMatchingCarBlock(consist, matchFunction, excludeMatchFromCut);
        }

        private List<Car> GetMatchingCarsByDestinationArea(List<Car> consist, Area areaMatch, bool excludeMatchFromCut)
        {
            bool matchFunction(Car car)
            {
                bool carMatchesFilter = false;

                if (opsControllerWrapper.TryGetCarDesination(car, out OpsCarPosition carDestination))
                {
                    Area carArea = opsControllerWrapper.AreaForCarPosition(carDestination);
                    carMatchesFilter = carArea?.identifier == areaMatch.identifier;
                }
                return carMatchesFilter;
            }
            return FindMatchingCarBlock(consist, matchFunction, excludeMatchFromCut);
        }

        private OpsCarPosition TryGetDestinationTrackMatch(ManagedWaypoint wp)
        {
            if (!opsControllerWrapper.TryResolveOpsCarPosition(wp.UncoupleDestinationId, out OpsCarPosition destinationMatch))
            {
                throw new UncouplingException($"Failed to resolve unknown track destination by id {wp.UncoupleDestinationId}", wp);
            }
            return destinationMatch;
        }

        private Industry TryGetDestinationIndustryMatch(ManagedWaypoint wp)
        {
            if (!opsControllerWrapper.TryGetIndustryById(wp.UncoupleDestinationId, out Industry industryMatch))
            {
                throw new UncouplingException($"Failed to resolve unknown industry destination by id {wp.UncoupleDestinationId}", wp);
            }
            return industryMatch;
        }

        private Area TryGetDestinationAreaMatch(ManagedWaypoint wp)
        {
            if (!opsControllerWrapper.TryGetAreaById(wp.UncoupleDestinationId, out Area areaMatch))
            {
                throw new UncouplingException($"Failed to resolve unknown area destination by id {wp.UncoupleDestinationId}", wp);
            }
            return areaMatch;
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
                        result.Add(car);
                    }
                }
                else
                {
                    result.Add(car);
                }
            }

            return result;
        }

        private List<Car> GetConsistForCutFromEnd(ManagedWaypoint wp)
        {
            (LogicalEnd nearEnd, LogicalEnd farEnd) = carService.GetEndsRelativeToLocation(wp.Locomotive, wp.Location);
            LogicalEnd directionToCountCars = wp.CountUncoupledFromNearestToWaypoint ? nearEnd : farEnd;

            List<Car> consistFromEnd = carService.EnumerateCoupled(wp.Locomotive, directionToCountCars);
            return consistFromEnd;
        }

        private List<Car> GetCarsAvailableForPickup(ManagedWaypoint wp, Car carCoupledTo)
        {
            (LogicalEnd _, LogicalEnd farEnd) = carService.GetEndsRelativeToLocation(carCoupledTo, wp.Location);

            List<Car> consistFromEnd = carService.EnumerateCoupled(carCoupledTo, farEnd);
            int indexOfCoupledCar = consistFromEnd.FindIndex(c => c.id == carCoupledTo.id);

            List<Car> carsAvailable = consistFromEnd.GetRange(0, indexOfCoupledCar + 1);
            return carsAvailable;
        }

        private List<Car> GetCarsAvailableForDropoff(ManagedWaypoint wp, Car carCoupledTo, out List<Car> consistFromFarEnd)
        {
            (LogicalEnd _, LogicalEnd farEnd) = carService.GetEndsRelativeToLocation(carCoupledTo, wp.Location);

            consistFromFarEnd = carService.EnumerateCoupled(carCoupledTo, farEnd);
            int indexOfCoupledCar = consistFromFarEnd.FindIndex(c => c.id == carCoupledTo.id);

            // does NOT include coupled car
            List<Car> carsAvailableForDropoff = consistFromFarEnd.GetRange(indexOfCoupledCar + 1, consistFromFarEnd.Count - indexOfCoupledCar - 1);
            return carsAvailableForDropoff;
        }

        public List<Car> FindCutBySpecificCar(ManagedWaypoint waypoint)
        {
            if (!waypoint.TryResolveUncouplingSearchText(out Car carToUncouple))
            {
                throw new UncouplingException($"Cannot find valid car matching \"{waypoint.UncouplingSearchText}\" for {waypoint.Locomotive.Ident} to uncouple.", waypoint);
            }

            LogicalEnd closestEnd = carService.ClosestLogicalEndTo(carToUncouple, waypoint.Location);
            List<Car> consist = [.. waypoint.Locomotive.EnumerateCoupled(closestEnd)];
            if (!consist.Any(c => c.id == carToUncouple.id))
            {
                throw new UncouplingException($"{carToUncouple.Ident} cannot be uncoupled because it is not part of {waypoint.Locomotive.Ident}'s consist.", waypoint);
            }

            LogicalEnd endToUncouple = waypoint.CountUncoupledFromNearestToWaypoint ? closestEnd : carService.GetOppositeEnd(closestEnd);

            List<Car> carsToCut = carService.EnumerateAdjacentCarsTowardEnd(carToUncouple, endToUncouple, !waypoint.ExcludeMatchingCarsFromCut);
            // Reverse cars so that the car to uncouple is last
            carsToCut.Reverse();

            return carsToCut;
        }

        public List<Car> FindPickupBySpecificCar(ManagedWaypoint waypoint, Car carCoupledTo)
        {
            if (!waypoint.TryResolveUncouplingSearchText(out Car carToPickup))
            {
                throw new UncouplingException($"Cannot find valid car matching \"{waypoint.UncouplingSearchText}\" for {waypoint.Locomotive.Ident} to pickup as a post-coupling cut.", waypoint);
            }

            List<Car> carsAvailableForPickup = GetCarsAvailableForPickup(waypoint, carCoupledTo);

            int indexOfCarToPickup = carsAvailableForPickup.FindIndex(c => c.id == carToPickup.id);

            if (indexOfCarToPickup < 0)
            {
                throw new UncouplingException($"{carToPickup.Ident} is not a valid car to pickup for {waypoint.Locomotive.Ident}.", waypoint);
            }

            // Do not include picked up car
            List<Car> carsToCut = carsAvailableForPickup.GetRange(0, indexOfCarToPickup);
            return carsToCut;
        }

        public List<Car> FindDropoffBySpecificCar(ManagedWaypoint wp, Car carCoupledTo)
        {
            if (!wp.TryResolveUncouplingSearchText(out Car carToDropoff))
            {
                throw new UncouplingException($"Cannot find valid car matching \"{wp.UncouplingSearchText}\" for {wp.Locomotive.Ident} to dropoff as post-coupling cut.", wp);
            }

            (LogicalEnd _, LogicalEnd farEnd) = carService.GetEndsRelativeToLocation(carCoupledTo, wp.Location);

            List<Car> consistFromEnd = carService.EnumerateCoupled(carCoupledTo, farEnd);

            int indexOfCarToDropoff = consistFromEnd.FindIndex(c => c.id == carToDropoff.id);

            if (indexOfCarToDropoff < 0)
            {
                throw new UncouplingException($"{carToDropoff.Ident} is not a valid car to dropoff for {wp.Locomotive.Ident}.", wp);
            }

            // Include dropped off car
            List<Car> carsToCut = consistFromEnd.GetRange(0, indexOfCarToDropoff + 1);
            return carsToCut;
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

            for (int i = 0; i < listOfCarBlocks.Count; i++)
            {
                List<Car> block = listOfCarBlocks[i];

                if (block.Count > 0)
                {
                    Car lastCar = block.Last();

                    if (i != listOfCarBlocks.Count - 1)
                    {
                        Loader.Log($"Uncoupling {lastCar.Ident} on logical end B for block {block.Count} cars");
                        UncoupleCar(lastCar, LogicalEnd.B);
                    }

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

        public List<Car> FindPickupAllExceptLocomotives(ManagedWaypoint wp, Car carCoupledTo)
        {
            List<Car> carsAvailableForPickup = GetCarsAvailableForPickup(wp, carCoupledTo);
            carsAvailableForPickup.Reverse();

            List<Car> carsToPickup = GetFirstCarBlockWithNoLocomotives(carsAvailableForPickup, excludeMatchFromCut: true);

            List<Car> carsNotPickedUp = [.. carsAvailableForPickup.Where(c => carsToPickup.All(p => p.id != c.id))];

            return carsNotPickedUp;
        }

        public List<Car> FindDropoffAllExceptLocomotives(ManagedWaypoint wp, Car carCoupledTo)
        {
            List<Car> carsAvailableForDropoff = GetCarsAvailableForDropoff(wp, carCoupledTo, out List<Car> consistFromFarEnd);

            List<Car> carsToDropoff = GetFirstCarBlockWithNoLocomotives(carsAvailableForDropoff, excludeMatchFromCut: true);

            List<Car> carsToCut = [];
            if (carsToDropoff.Count > 0)
            {
                Car lastCar = carsToDropoff.Last();
                int indexOfLastCar = consistFromFarEnd.FindIndex(c => c.id == lastCar.id);

                carsToCut = [.. consistFromFarEnd.GetRange(0, indexOfLastCar + 1)];
            }

            return carsToCut;
        }

        private List<Car> GetFirstCarBlockWithNoLocomotives(List<Car> consist, bool excludeMatchFromCut)
        {
            bool matchFunction(Car car)
            {
                return carService.IsCarLocomotiveType(car);
            }
            return FindMatchingCarBlock(consist, matchFunction, excludeMatchFromCut);
        }

        internal (Car carToUncouple, LogicalEnd endToUncouple) FindCarToUncouple(List<Car> carsToCut, List<Car> consistFromEndA)
        {
            if (carsToCut == null || carsToCut.Count == 0)
            {
                throw new InvalidOperationException("Cannot calculate uncoupling point for empty cut list");
            }

            if (carsToCut.Count >= consistFromEndA.Count)
            {
                throw new InvalidOperationException("Cannot uncouple more cars than exist in consist.");
            }

            int startIndex = consistFromEndA.FindIndex(c => c.id == carsToCut.First().id);
            int endIndex = consistFromEndA.FindIndex(c => c.id == carsToCut.Last().id);

            if (startIndex == -1 || endIndex == -1)
            {
                throw new InvalidOperationException("The calculated list of cars to cut is not a valid contiguous sublist of consist.");
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
            else
            {
                throw new InvalidOperationException("Cannot determine a valid car to uncouple.");
            }
        }

        public List<Car> PerformCut(List<Car> carsToCut, ManagedWaypoint waypoint)
        {
            Loader.LogDebug($"Entering PerformCut before filtering split tenders");
            int carsBeforeFilter = carsToCut.Count;
            carsToCut = carService.FilterAnySplitLocoTenderPairs(carsToCut);

            if (carsBeforeFilter > 0 && carsToCut.Count == 0)
            {
                string errorMessage = $"No valid cars can be cut for {waypoint.Locomotive.Ident} due to a restriction on separating a locomotive from its tender.";
                Loader.LogError($"{errorMessage} Car count before filter was {carsBeforeFilter}. Car count after filter was 0.");
                throw new UncouplingException(errorMessage, waypoint);
            }

            if (carsToCut == null || carsToCut.Count == 0)
            {
                string errorMessage = $"No valid cars can be cut for {waypoint.Locomotive.Ident}.";
                throw new UncouplingException(errorMessage, waypoint);
            }

            List<Car> fullConsistFromEndA = [.. carsToCut.First().EnumerateCoupled(fromEnd: LogicalEnd.A)];

            if (carsToCut.Count >= fullConsistFromEndA.Count)
            {
                string errorMessage = $"Cannot uncouple more cars than exist in {waypoint.Locomotive.Ident}'s consist.";
                Loader.LogError($"{errorMessage}\nCars to cut: {CarListToString(carsToCut)}\nConsist: {CarListToString(fullConsistFromEndA)}");
                throw new UncouplingException(errorMessage, waypoint);
            }

            Loader.Log($"Uncoupling {carsToCut.Count} cars from consist of {fullConsistFromEndA.Count} cars:\n" +
                $"cutting: {CarListToString(carsToCut)}\n" +
                $"from: {CarListToString(fullConsistFromEndA)}\n" +
                $"as: {(waypoint.TakeUncoupledCarsAsActiveCut ? "active cut" : "inactive cut")}");


            Car carToUncouple;
            LogicalEnd endToUncouple = LogicalEnd.A;

            try
            {
                (carToUncouple, endToUncouple) = FindCarToUncouple(carsToCut, fullConsistFromEndA);
            }
            catch (Exception e)
            {
                Loader.LogError($"Error while attempting to uncouple car: {e}");
                Loader.LogError($"Cars to cut: {CarListToString(carsToCut)}\nConsist: {CarListToString(fullConsistFromEndA)}");
                throw new UncouplingException(e.Message, waypoint, e);
            }

            Loader.Log($"Uncoupling {carToUncouple.Ident} on end {LogicalEndToString(endToUncouple)} for cut of {carsToCut.Count} cars");

            UncoupleCar(carToUncouple, endToUncouple);

            carService.UpdateCarsForAE(waypoint.Locomotive as BaseLocomotive);

            List<Car> carsStillCoupled = [.. fullConsistFromEndA.Where(c => !carsToCut.Contains(c))];
            List<Car> inactiveCut = waypoint.TakeUncoupledCarsAsActiveCut ? carsStillCoupled : carsToCut;
            if (waypoint.ApplyHandbrakesOnUncouple)
            {
                carService.SetHandbrakesOnCut(inactiveCut);
            }

            if (waypoint.BleedAirOnUncouple)
            {
                carService.BleedAirOnCut(inactiveCut);
            }
            return inactiveCut;
        }

        private void UncoupleCar(Car car, LogicalEnd endToUncouple)
        {
            LogicalEnd oppositeEnd = endToUncouple == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;
            Loader.LogDebug($"Trying to uncouple {car.Ident} on logical end {LogicalEndToString(endToUncouple)}");

            if (StateManager.IsHost && car.set != null)
            {
                Car adjacent = car.CoupledTo(endToUncouple);
                if (adjacent != null)
                {
                    Loader.Log($"Uncoupling {car.Ident} and {adjacent.Ident}");
                    car.ApplyEndGearChange(endToUncouple, EndGearStateKey.Anglecock, f: 0f);
                    car.ApplyEndGearChange(endToUncouple, EndGearStateKey.IsAirConnected, boolValue: false);

                    adjacent.ApplyEndGearChange(oppositeEnd, EndGearStateKey.CutLever, 1f);
                    adjacent.ApplyEndGearChange(oppositeEnd, EndGearStateKey.Anglecock, f: 0f);
                    adjacent.ApplyEndGearChange(oppositeEnd, EndGearStateKey.IsAirConnected, boolValue: false);
                }
                else
                {
                    throw new UncouplingException($"Cannot uncouple {car.Ident} on logical end {LogicalEndToString(endToUncouple)} because there is no adjacent car");
                }
            }
        }
    }
}
