using Helpers;
using Model;
using Model.Definition.Data;
using Model.Ops;
using Model.Ops.Definition;
using RollingStock;
using System;
using System.Collections.Generic;
using System.Linq;
using Track;
using UI.Common;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.UUM;
using WaypointQueue.Wrappers;
using static Model.Car;

namespace WaypointQueue.Services
{
    internal class RefuelService(ICarService carService, AutoEngineerService autoEngineerService, IOpsControllerWrapper opsControllerWrapper)
    {
        private List<CarLoadTargetLoader> _carLoadTargetLoaders = [];
        private List<CarLoaderSequencer> _carLoaderSequencers = [];

        public void RebuildCollections()
        {
            Loader.LogDebug($"RefuelService RebuildCollections");
            _carLoadTargetLoaders.Clear();
            _carLoaderSequencers.Clear();

            _carLoadTargetLoaders = [.. UnityEngine.Object.FindObjectsOfType<CarLoadTargetLoader>()];
            _carLoaderSequencers = [.. UnityEngine.Object.FindObjectsOfType<CarLoaderSequencer>()];
        }

        public void OrderToRefuel(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.Log($"Beginning order to refuel {waypoint.Locomotive.Ident}");
            waypoint.CurrentlyRefueling = true;
            // maybe in the future, support refueling multiple locomotives if they are MU'd

            waypoint.MaxSpeedAfterRefueling = ordersHelper.Orders.MaxSpeedMph;
            // Set speed limit to help prevent train from overrunning waypoint
            int speedWhileRefueling = waypoint.RefuelingSpeedLimit;
            ordersHelper.SetOrdersValue(null, null, maxSpeedMph: speedWhileRefueling, null, null);
            // Make sure AE knows how many cars in case we coupled just before this
            carService.UpdateCarsForAE(waypoint.Locomotive as BaseLocomotive);

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
            autoEngineerService.SendToWaypoint(ordersHelper, locationToMove);
        }

        private Location GetRefuelLocation(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            Car fuelCar = GetFuelCar((BaseLocomotive)waypoint.Locomotive);

            Loader.LogDebug($"Finding {waypoint.RefuelLoadName} refuel location for {fuelCar.Ident}");

            Vector3 loadSlotPosition = GetFuelCarLoadSlotPosition(fuelCar, waypoint.RefuelLoadName, out float slotMaxCapacity);
            waypoint.RefuelMaxCapacity = slotMaxCapacity;

            if (!Graph.Shared.TryGetLocationFromGamePoint(waypoint.RefuelPoint, 10f, out Location targetLoaderLocation))
            {
                throw new InvalidOperationException($"Cannot refuel at waypoint, failed to get graph location from refuel game point {waypoint.RefuelPoint}");
            }

            (Location closestTrainEndLocation, Location furthestTrainEndLocation) = carService.GetTrainEndLocations(waypoint, out _, out _, out _);

            LogicalEnd furthestFuelCarEnd = carService.ClosestLogicalEndTo(fuelCar, furthestTrainEndLocation);
            LogicalEnd closestFuelCarEnd = carService.GetOppositeEnd(furthestFuelCarEnd);

            List<Car> coupledCarsToEnd = carService.EnumerateAdjacentCarsTowardEnd(fuelCar, furthestFuelCarEnd, inclusive: true);
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

        private bool IsTargetBetween(Vector3 target, Vector3 positionA, Vector3 positionB)
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

        private List<string> GetValidLoadsForLoco(BaseLocomotive locomotive)
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

        public void CheckNearbyFuelLoaders(ManagedWaypoint waypoint)
        {
            RebuildCollections();
            List<string> validLoads = GetValidLoadsForLoco((BaseLocomotive)waypoint.Locomotive);
            CarLoadTargetLoader closestLoader = null;
            float shortestDistance = 0;
            Loader.LogDebug($"Checking for nearby fuel loaders");

            foreach (CarLoadTargetLoader targetLoader in _carLoadTargetLoaders)
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

        // Copied from AutoEngineerPlanner.CalculateTotalLength
        private float CalculateTotalLength(List<Car> cars)
        {
            float num = 0f;
            foreach (Car item in cars)
            {
                num += item.carLength;
            }

            return num + 1.04f * (float)(cars.Count - 1);
        }

        private Car GetFuelCar(BaseLocomotive locomotive)
        {
            Car fuelCar = locomotive;
            if (locomotive.Archetype == Model.Definition.CarArchetype.LocomotiveSteam)
            {
                fuelCar = PatchSteamLocomotive.FuelCar(locomotive);
            }
            return fuelCar;
        }

        public bool IsDoneRefueling(ManagedWaypoint waypoint)
        {
            return IsLocoFull(waypoint) || IsLoaderEmpty(waypoint);
        }

        private bool IsLocoFull(ManagedWaypoint waypoint)
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

        private bool IsLoaderEmpty(ManagedWaypoint waypoint)
        {
            string industryId = waypoint.RefuelIndustryId;
            string loadId = waypoint.RefuelLoadName;

            if (!opsControllerWrapper.TryGetIndustryById(industryId, out Industry industry))
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

        public void CleanupAfterRefuel(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper)
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

        public void SetCarLoaderSequencerWantsLoading(ManagedWaypoint waypoint, bool value)
        {
            CarLoadTargetLoader loaderTarget = FindCarLoadTargetLoader(waypoint);
            if (loaderTarget == null)
            {
                Loader.LogError($"Cannot find CarLoadTargetLoader at point {waypoint.RefuelPoint} for waypoint {waypoint.Id}");
                return;
            }
            CarLoaderSequencer sequencer = _carLoaderSequencers.Find(x => x.keyValueObject.RegisteredId == loaderTarget.keyValueObject.RegisteredId);
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
            RebuildCollections();
            Vector3 worldPosition = WorldTransformer.GameToWorld(waypoint.RefuelPoint);
            //Loader.LogDebug($"Starting search for target loader matching world point {worldPosition}");
            CarLoadTargetLoader loader = _carLoadTargetLoaders.Find(l => l.transform.position == worldPosition);
            //Loader.LogDebug($"Found matching {loader.load.name} loader at game point {waypoint.RefuelPoint}");
            return loader;
        }
    }
}
