using Game.State;
using HarmonyLib;
using Model;
using Model.Definition;
using Model.Ops;
using System;
using System.Collections.Generic;
using System.Linq;
using UI.Common;
using UnityEngine;
using WaypointQueue.UUM;
using static Model.Car;
using static Model.Ops.OpsController;
using static WaypointQueue.CarUtils;

namespace WaypointQueue
{
    internal class UncouplingHandler
    {
        public void UncoupleByCount(ManagedWaypoint waypoint)
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

        public void UncoupleByDestination(ManagedWaypoint waypoint)
        {
            Loader.Log($"Resolving uncouple by destination for {waypoint.Locomotive.Ident}");
            LogicalEnd directionToCountCars = GetEndRelativeToWapoint(waypoint.Locomotive, waypoint.Location, useFurthestEnd: !waypoint.CountUncoupledFromNearestToWaypoint);

            List<Car> allCarsFromEnd = waypoint.Locomotive.EnumerateCoupled(directionToCountCars).ToList();
            Loader.LogDebug($"Enumerating all cars of {waypoint.Locomotive.Ident} from logical end {LogicalEndToString(directionToCountCars)}:\n{CarListToString(allCarsFromEnd)}");

            List<Car> carsToCut = [];
            if (waypoint.UncoupleDestinationId == WaypointResolver.NoDestinationString)
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

        private List<Car> FindMatchingCarsByNoDestination(List<Car> allCars, bool excludeMatchingCarsFromCut)
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

        private List<Car> FindMatchingCarsByTrackDestination(List<Car> allCars, OpsCarPosition destinationMatch, bool excludeMatchingCarsFromCut)
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

        private List<Car> FindMatchingCarsByIndustryDestination(List<Car> allCars, Industry destinationMatch, bool excludeMatchingCarsFromCut)
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

        private List<Car> FindMatchingCarsByAreaDestination(List<Car> allCars, Area destinationMatch, bool excludeMatchingCarsFromCut)
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

        public void UncoupleBySpecificCar(ManagedWaypoint waypoint)
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
                            SetHandbrakesOnCut(block);
                        }

                        if (waypoint.BleedAirOnUncouple)
                        {
                            BleedAirOnCut(block);
                        }
                    }
                }
            }

            UpdateCarsForAE(waypoint.Locomotive as BaseLocomotive);
        }

        public void PostCouplingCutByCount(ManagedWaypoint waypoint)
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
                    SetHandbrakesOnCut(carsLeftBehind);
                }

                Car carToUncouple = carsLeftBehind.FirstOrDefault();

                if (carToUncouple != null)
                {
                    Loader.Log($"Uncoupling {carToUncouple.Ident} for cut of {clampedNumberOfCarsToCut} cars with {carsLeftBehind.Count} total left behind ");
                    UncoupleCar(carToUncouple, closestEnd);
                    UpdateCarsForAE(waypoint.Locomotive as BaseLocomotive);

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

        private void PerformCut(List<Car> carsToCut, List<Car> allCars, ManagedWaypoint waypoint)
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

            Loader.Log($"Cutting {CarUtils.CarListToString(carsToCut)} from {CarListToString(allCars)} as {(waypoint.TakeUncoupledCarsAsActiveCut ? "active cut" : "inactive cut")}");

            if (waypoint.ApplyHandbrakesOnUncouple)
            {
                SetHandbrakesOnCut(inactiveCut);
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
            UpdateCarsForAE(waypoint.Locomotive as BaseLocomotive);

            if (waypoint.BleedAirOnUncouple)
            {
                BleedAirOnCut(inactiveCut);
            }
        }

        private void UncoupleCar(Car car, LogicalEnd endToUncouple)
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

        private List<Car> FilterAnySplitLocoTenderPairs(List<Car> carsToCut)
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
    }
}
