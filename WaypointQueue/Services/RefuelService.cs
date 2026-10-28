using Game.Messages;
using Game.State;
using HarmonyLib;
using Helpers;
using Model;
using Model.Definition;
using Model.Definition.Data;
using Model.Ops;
using Model.Ops.Definition;
using RollingStock;
using System;
using System.Collections.Generic;
using System.Linq;
using Track;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.Model;
using WaypointQueue.Wrappers;
using static Model.Car;

namespace WaypointQueue.Services
{
    internal class RefuelService(ICarService carService, AutoEngineerService autoEngineerService, IOpsControllerWrapper opsControllerWrapper)
    {
        private List<CarLoadTargetLoader> _carLoadTargetLoaders = [];
        private List<CarLoaderSequencer> _carLoaderSequencers = [];

        private readonly Dictionary<string, float> _cachedCarLoadQuantityByFuelCarId = [];

        public void RebuildCollections()
        {
            _carLoadTargetLoaders.Clear();
            _carLoaderSequencers.Clear();

            _carLoadTargetLoaders = [.. UnityEngine.Object.FindObjectsByType<CarLoadTargetLoader>(FindObjectsSortMode.None)];
            _carLoaderSequencers = [.. UnityEngine.Object.FindObjectsByType<CarLoaderSequencer>(FindObjectsSortMode.None)];
        }

        public void OrderToRefuel(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper, out List<BaseLocomotive> locosForRefuelList)
        {
            ModLocator.Loader.Log($"Beginning order to refuel {waypoint.Locomotive.Ident} for waypoint");

            BaseLocomotive locoForRefuel = null;
            // Only populate the refuel loco id queue if it is currently empty
            if (waypoint.RefuelLocoIdsQueue.Count > 0)
            {
                locoForRefuel = carService.GetLocoById(waypoint.RefuelLocoIdsQueue.First());
                locosForRefuelList = [waypoint.Locomotive];
            }
            else
            {
                locosForRefuelList = waypoint.EnableMultipleRefueling
                    ? GetCoupledLocosForRefuel(waypoint.Locomotive, waypoint.RefuelLoadId, waypoint.Location)
                    : [waypoint.Locomotive];

                waypoint.RefuelLocoIdsQueue = locosForRefuelList.Select(l => l.id).ToList();
                locoForRefuel = locosForRefuelList.First();
            }

            waypoint.CurrentlyRefueling = true;

            waypoint.MaxSpeedAfterRefueling = ordersHelper.Orders.MaxSpeedMph;
            // Set speed limit to help prevent train from overrunning waypoint
            int speedWhileRefueling = waypoint.RefuelingSpeedLimit;
            ordersHelper.SetOrdersValue(null, null, maxSpeedMph: speedWhileRefueling, null, null);

            // Make sure AE knows how many cars in case we coupled just before this
            carService.UpdateCarsForAE(waypoint.Locomotive as BaseLocomotive);

            OrderLocomotiveToRefuel(locoForRefuel, waypoint, ordersHelper);
        }

        public void OrderLocomotiveToRefuel(BaseLocomotive locoForRefuel, ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            Location locationToMove = GetRefuelLocation(locoForRefuel, waypoint);
            _cachedCarLoadQuantityByFuelCarId.Remove(locoForRefuel.id);

            ModLocator.Loader.Log($"Sending refueling waypoint for {waypoint.Locomotive.Ident} to {locationToMove} to refuel {locoForRefuel.Ident}");
            waypoint.StatusLabel = $"Moving to refuel {waypoint.RefuelLoadId}";
            waypoint.OverwriteLocation(locationToMove);
            waypoint.StopAtWaypoint = true;
            WaypointQueueController.Shared.UpdateWaypoint(waypoint);
            autoEngineerService.SendToWaypoint(ordersHelper, locationToMove);
        }

        private List<BaseLocomotive> GetCoupledLocosForRefuel(BaseLocomotive locomotive, string refuelLoadName, Location location)
        {
            carService.GetTrainEndLocations(locomotive, location, out _, out _, out _, out LogicalEnd closestEnd, out LogicalEnd furthestEnd);

            if (refuelLoadName == "water" || refuelLoadName == "coal")
            {
                return [.. locomotive.EnumerateCoupled(closestEnd).Where(c => c.CarType.IsLocomotiveSteam).Cast<BaseLocomotive>()];
            }
            if (refuelLoadName == "diesel-fuel")
            {
                return [.. locomotive.EnumerateCoupled(closestEnd).Where(c => c.CarType.IsLocomotiveDiesel).Cast<BaseLocomotive>()];
            }
            return [];
        }

        private Location GetRefuelLocation(BaseLocomotive locomotive, ManagedWaypoint waypoint)
        {
            Car fuelCar = GetFuelCar(locomotive);

            Vector3 loadSlotPosition = GetFuelCarLoadSlotPosition(fuelCar, waypoint.RefuelLoadId, out float slotMaxCapacity);
            waypoint.RefuelMaxCapacity = slotMaxCapacity;

            if (!Graph.Shared.TryGetLocationFromGamePoint(waypoint.RefuelPoint, 10f, out Location targetLoaderLocation))
            {
                throw new RefuelException($"Failed to get track graph location from refuel game point {waypoint.RefuelPoint}.", waypoint);
            }

            (Location closestTrainEndLocation, Location furthestTrainEndLocation) = carService.GetTrainEndLocations(locomotive, targetLoaderLocation, out _, out _, out _, out _, out _);

            LogicalEnd furthestFuelCarEnd = carService.ClosestLogicalEndTo(fuelCar, furthestTrainEndLocation);
            LogicalEnd closestFuelCarEnd = carService.GetOppositeEnd(furthestFuelCarEnd);

            List<Car> coupledCarsToEnd = carService.EnumerateAdjacentCarsTowardEnd(fuelCar, furthestFuelCarEnd, inclusive: true);
            float distanceFromFurthestEndOfTrainToFuelCarInclusive = CarService.CalculateTotalLength(coupledCarsToEnd);

            float distanceFromClosestFuelCarEndToSlot = Vector3.Distance(fuelCar.LocationFor(closestFuelCarEnd).GetPosition().ZeroY(), loadSlotPosition.ZeroY());

            float totalTrainLength = CarService.CalculateTotalLength([.. locomotive.EnumerateCoupled()]);

            //Loader.LogDebug($"Total train length is {totalTrainLength}");
            //Loader.LogDebug($"distanceFromFurthestEndOfTrainToFuelCarInclusive is {distanceFromFurthestEndOfTrainToFuelCarInclusive}");
            //Loader.LogDebug($"distanceFromClosestFuelCarEndToSlot is {distanceFromClosestFuelCarEndToSlot}");

            Location locationToMoveToward = new();
            float distanceToMove = 0;

            Vector3 targetLoaderPosition = targetLoaderLocation.GetPosition();
            Vector3 closestTrainEndPosition = closestTrainEndLocation.GetPosition();
            Vector3 furthestTrainEndPosition = furthestTrainEndLocation.GetPosition();

            // Some small locomotives may have a load slot outside the calculated train end positions
            bool slotBeyondFurthestEnd = IsTargetBetween(furthestTrainEndPosition, loadSlotPosition, closestTrainEndPosition, inclusive: true);
            bool slotBeyondClosestEnd = IsTargetBetween(closestTrainEndPosition, loadSlotPosition, furthestTrainEndPosition, inclusive: true);

            var clampedSlotPosition = loadSlotPosition;
            if (slotBeyondClosestEnd)
            {
                clampedSlotPosition = closestTrainEndPosition;
            } else if (slotBeyondFurthestEnd)
            {
                clampedSlotPosition = furthestTrainEndPosition;
            }

            //Loader.LogDebug($"Checking if slot is in between loader and closest end");
            if (IsTargetBetween(clampedSlotPosition, targetLoaderPosition, closestTrainEndPosition, inclusive: true))
            {
                // need to move toward the far end
                //Loader.LogDebug($"{waypoint.RefuelLoadName} slot is between loader and closest end");
                distanceToMove = distanceFromFurthestEndOfTrainToFuelCarInclusive - distanceFromClosestFuelCarEndToSlot;
                locationToMoveToward = furthestTrainEndLocation;
            }

            //Loader.LogDebug($"Checking if slot is in between loader and furthest end");
            if (IsTargetBetween(clampedSlotPosition, targetLoaderPosition, furthestTrainEndPosition, inclusive: true))
            {
                // need to move toward the near end
                //Loader.LogDebug($"{waypoint.RefuelLoadName} slot is between loader and furthest end");
                distanceToMove = totalTrainLength - distanceFromFurthestEndOfTrainToFuelCarInclusive + distanceFromClosestFuelCarEndToSlot;
                locationToMoveToward = closestTrainEndLocation;
            }

            //Loader.LogDebug($"Checking if loader is in between closest end and furthest end");
            if (IsTargetBetween(targetLoaderPosition, closestTrainEndPosition, furthestTrainEndPosition))
            {
                //Loader.LogDebug($"{waypoint.RefuelLoadName} loader is between closest end and furthest end");
                distanceToMove = -distanceToMove;
            }

            //Loader.LogDebug($"distanceToMove is {distanceToMove}");

            Location orientedTargetLocation = Graph.Shared.LocationOrientedToward(targetLoaderLocation, locationToMoveToward);

            Location locationToMove = Graph.Shared.LocationByMoving(orientedTargetLocation, distanceToMove, false, true);

            return locationToMove;
        }

        private Vector3 GetFuelCarLoadSlotPosition(Car fuelCar, string refuelLoadName, out float maxCapacity)
        {
            LoadSlot loadSlot = fuelCar.Definition.LoadSlots.Find(slot => slot.RequiredLoadIdentifier == refuelLoadName);

            maxCapacity = loadSlot.MaximumCapacity;

            int loadSlotIndex = fuelCar.Definition.LoadSlots.IndexOf(loadSlot);

            List<CarLoadTarget> carLoadTargets = fuelCar.GetComponentsInChildren<CarLoadTarget>().ToList();
            CarLoadTarget loadTarget = carLoadTargets.Find(clt => clt.slotIndex == loadSlotIndex);

            Vector3 loadSlotPosition = CalculatePositionFromLoadTarget(fuelCar, loadTarget);

            return loadSlotPosition;
        }

        private Vector3 CalculatePositionFromLoadTarget(Car fuelCar, CarLoadTarget loadTarget)
        {
            // This logic is based on CarLoadTargetLoader.LoadSlotFromCar
            Matrix4x4 transformMatrix = fuelCar.GetTransformMatrix(Graph.Shared);
            Vector3 point2 = fuelCar.transform.InverseTransformPoint(loadTarget.transform.position);
            Vector3 vector = transformMatrix.MultiplyPoint3x4(point2);
            return vector;
        }

        private bool IsTargetBetween(Vector3 target, Vector3 positionA, Vector3 positionB, bool inclusive = false)
        {
            // If target is in the middle, the distance between either end to the target will always be less than the length from one end to the other
            float distanceAToTarget = Vector3.Distance(positionA.ZeroY(), target.ZeroY());

            if (inclusive && distanceAToTarget == 0)
            {
                return true;
            }

            float distanceBToTarget = Vector3.Distance(positionB.ZeroY(), target.ZeroY());

            if (inclusive && distanceBToTarget == 0)
            {
                return true;
            }

            float distanceAtoB = Vector3.Distance(positionA.ZeroY(), positionB.ZeroY());

            if (distanceAToTarget < distanceAtoB && distanceBToTarget < distanceAtoB)
            {
                return true;
            }
            return false;
        }

        private List<string> GetValidLoadsForWaypoint(ManagedWaypoint waypoint)
        {
            BaseLocomotive locomotive = waypoint.Locomotive;

            if (locomotive == null)
            {
                return ["water", "coal", "diesel-fuel"];
            }
            if (locomotive.CarType.IsLocomotiveSteam)
            {
                return ["water", "coal"];
            }
            if (locomotive.CarType.IsLocomotiveDiesel)
            {
                return ["diesel-fuel"];
            }
            return null;
        }

        public void CheckNearbyFuelLoaders(ManagedWaypoint waypoint)
        {
            List<string> validLoads = GetValidLoadsForWaypoint(waypoint);

            CarLoadTargetLoader closestLoader = null;
            float shortestDistance = 0;

            foreach (CarLoadTargetLoader targetLoader in _carLoadTargetLoaders)
            {
                if (!validLoads.Contains(targetLoader.load?.id?.ToLower()))
                {
                    continue;
                }

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

                if (hasLocation)
                {
                    float distanceFromWaypointToLoader = Vector3.Distance(waypoint.Location.GetPosition(), loaderLocation.GetPosition());

                    float radiusToSearch = 5f;

                    if (distanceFromWaypointToLoader < radiusToSearch)
                    {
                        ModLocator.Loader.LogDebug($"Found {targetLoader.load.id} loader within {distanceFromWaypointToLoader}");
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
                ModLocator.Loader.Log($"Using loader named \"{closestLoader.load.id}\" with key value registered id {closestLoader.keyValueObject.RegisteredId} loader at {closestLoader.transform?.position}");
                Vector3 loaderPosition = WorldTransformer.WorldToGame(closestLoader.transform.position);
                // CarLoadTargetLoader uses game position for loading logic, not graph Location
                waypoint.SerializableRefuelPoint = new SerializableVector3(loaderPosition.x, loaderPosition.y, loaderPosition.z);
                // Water towers will have a null source industry
                waypoint.RefuelIndustryId = GetSourceIndustryId(closestLoader);
                waypoint.RefuelLoaderRegisteredId = closestLoader.keyValueObject.RegisteredId;
                waypoint.RefuelLoadId = closestLoader.load.id;
                waypoint.WillRefuel = true;
            }
        }

        private string GetSourceIndustryId(CarLoadTargetLoader targetLoader)
        {
            return Traverse.Create(targetLoader).Field("sourceIndustryId").GetValue<string>();
        }

        private Car GetFuelCar(BaseLocomotive locomotive)
        {
            Car fuelCar = locomotive;
            if (locomotive.CarType.IsLocomotiveSteam)
            {
                fuelCar = PatchSteamLocomotive.FuelCar(locomotive);
            }
            return fuelCar;
        }

        public bool IsLocoFull(BaseLocomotive locomotive, string refuelLoadName, float refuelMaxCapacity, out bool isIncreasing)
        {
            Car fuelCar = GetFuelCar(locomotive);
            CarLoadInfo? carLoadInfo = fuelCar.GetLoadInfo(refuelLoadName, out int slotIndex);
            isIncreasing = false;

            if (!carLoadInfo.HasValue)
            {
                ModLocator.Loader.LogError($"Locomotive {locomotive.Ident} had a null CarLoadInfo");
                return true;
            }

            if (_cachedCarLoadQuantityByFuelCarId.TryGetValue(locomotive.id, out var oldQuantity) && oldQuantity < carLoadInfo.Value.Quantity)
            {
                isIncreasing = true;
            }
            _cachedCarLoadQuantityByFuelCarId[locomotive.id] = carLoadInfo.Value.Quantity;

            LoadSlot loadSlot = fuelCar.Definition.LoadSlots[slotIndex];

            bool isFullVanilla = carLoadInfo.Value.Quantity / loadSlot.MaximumCapacity > 0.999f;

            if (isFullVanilla)
            {
                ModLocator.Loader.Log($"Fuel car {fuelCar.Ident} is full");
            }
            return isFullVanilla;
        }

        public bool IsLoaderEmpty(string industryId, string refuelLoadId)
        {
            if (!opsControllerWrapper.TryGetIndustryById(industryId, out Industry industry))
            {
                // Water towers are unlimited and have a null industry
                return false;
            }

            if (industry.Storage == null)
            {
                ModLocator.Loader.Log($"Storage is null for {industry.name}");
                return true;
            }

            Load matchingLoad = industry.Storage.Loads().ToList().Find(l => l.id == refuelLoadId);

            if (matchingLoad == null)
            {
                ModLocator.Loader.Log($"Industry {industry.name} is empty, did not find matching load for {refuelLoadId}");
                return true;
            }

            if (industry.Storage.QuantityInStorage(matchingLoad) <= 0f)
            {
                ModLocator.Loader.Log($"Industry {industry.name} is empty, quantity in storage was zero");
                return true;
            }
            return false;
        }

        public void CleanupAfterRefuel(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper)
        {
            ModLocator.Loader.Log($"Done refueling {wp.Locomotive.Ident}");
            wp.WillRefuel = false;
            wp.CurrentlyRefueling = false;
            SetCarLoaderSequencerWantsLoading(wp, false);
            WaypointQueueController.Shared.UpdateWaypoint(wp);

            int maxSpeed = wp.MaxSpeedAfterRefueling;
            if (maxSpeed == 0) maxSpeed = 35;
            ordersHelper.SetOrdersValue(null, null, maxSpeedMph: maxSpeed, null, null);
        }

        public void SetCarLoaderSequencerWantsLoading(ManagedWaypoint waypoint, bool value)
        {
            CarLoadTargetLoader loaderTarget = FindCarLoadTargetLoader(waypoint);
            if (loaderTarget == null)
            {
                ModLocator.Loader.LogError($"_carLoadTargetLoaders: {String.Join("-", _carLoadTargetLoaders.Select(c => $"[{c.keyValueObject.RegisteredId}, {waypoint.RefuelLoadId} loader]"))}");
                var message = $"Cannot find valid CarLoadTargetLoader for \"{waypoint.RefuelLoadId}\" at point {waypoint.RefuelPoint} with registered id {waypoint.RefuelLoaderRegisteredId}.";
                ModLocator.Loader.LogError(message);
                if (value)
                {
                    // Only throw if we are trying to activate the loader. If we're just cleaning up then it can fail silently.
                    throw new RefuelException(message, waypoint);
                }
            }
            CarLoaderSequencer sequencer = _carLoaderSequencers.Find(x => x.keyValueObject.RegisteredId == loaderTarget.keyValueObject.RegisteredId);
            if (sequencer != null)
            {
                StateManager.ApplyLocal(new PropertyChange(sequencer.keyValueObject.RegisteredId, sequencer.readWantsLoadingKey, new BoolPropertyValue(value)));
            }
            else
            {
                ModLocator.Loader.LogError($"_carLoaderSequencers: {String.Join("-", _carLoaderSequencers.Select(c => $"[{c.keyValueObject.RegisteredId}, {waypoint.RefuelLoadId} sequencer]"))}");
                var message = $"Cannot find valid CarLoaderSequencer for loader target {loaderTarget.name} with load \"{waypoint.RefuelLoadId}\" at point {waypoint.RefuelPoint} with registered id {loaderTarget.keyValueObject.RegisteredId}.";
                ModLocator.Loader.LogError(message);
                if (value)
                {
                    // Only throw if we are trying to activate the loader. If we're just cleaning up then it can fail silently.
                    throw new RefuelException(message, waypoint);
                }
            }
        }

        private CarLoadTargetLoader FindCarLoadTargetLoader(ManagedWaypoint waypoint)
        {
            CarLoadTargetLoader loader = null;
            // First try to find by registered id
            if (!String.IsNullOrEmpty(waypoint.RefuelLoaderRegisteredId))
            {
                loader = _carLoadTargetLoaders.Find(l => l.keyValueObject.RegisteredId == waypoint.RefuelLoaderRegisteredId);
            }

            // If that fails, then try to find by position
            if (loader == null)
            {
                Vector3 worldPosition = WorldTransformer.GameToWorld(waypoint.RefuelPoint);
                //Loader.LogDebug($"Starting search for target loader matching world point {worldPosition}");
                loader = _carLoadTargetLoaders.Find(l => l.transform.position == worldPosition);
                //Loader.LogDebug($"Found matching {loader.load.name} loader at game point {waypoint.RefuelPoint}");
            }

            return loader;
        }

        public void RepositionToRetryRefueling(ManagedWaypoint waypoint, BaseLocomotive currentRefuelLoco, AutoEngineerOrdersHelper ordersHelper)
        {
            _cachedCarLoadQuantityByFuelCarId.Remove(currentRefuelLoco.id);

            BaseLocomotive locomotive = waypoint.Locomotive;

            if (!Graph.Shared.TryGetLocationFromGamePoint(waypoint.RefuelPoint, 10f, out Location targetLoaderLocation))
            {
                throw new RefuelException($"Failed to get track graph location from refuel game point {waypoint.RefuelPoint}.", waypoint);
            }

            (Location closestTrainEndLocation, Location furthestTrainEndLocation) = carService.GetTrainEndLocations(locomotive, targetLoaderLocation, out _, out _, out _, out _, out _);

            Location orientedFurthestEnd = Graph.Shared.LocationOrientedToward(furthestTrainEndLocation, targetLoaderLocation);

            float distanceToMoveAwayFromLoader = 10;
            Location locationToMove = Graph.Shared.LocationByMoving(orientedFurthestEnd, distanceToMoveAwayFromLoader);

            ModLocator.Loader.Log($"Sending retry refueling waypoint for {waypoint.Locomotive.Ident} to {locationToMove} to refuel {currentRefuelLoco.Ident}");
            waypoint.StatusLabel = $"Moving to refuel {waypoint.RefuelLoadId}";
            waypoint.OverwriteLocation(locationToMove);
            waypoint.StopAtWaypoint = true;
            autoEngineerService.SendToWaypoint(ordersHelper, locationToMove);
        }
    }
}
