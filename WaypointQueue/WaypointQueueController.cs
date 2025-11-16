using Game;
using Game.Messages;
using Game.State;
using Helpers;
using Model;
using Model.AI;
using Model.Definition.Data;
using Model.Ops;
using Model.Ops.Definition;
using Model.Ops.Timetable;
using RollingStock;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Track;
using UI.Common;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.UUM;
using static Model.Car;
using static WaypointQueue.ModSaveManager;

namespace WaypointQueue
{
    public class WaypointQueueController : MonoBehaviour
    {
        public static event Action OnWaypointsUpdated;
        private Coroutine _coroutine;

        public List<LocoWaypointState> WaypointStateList { get; private set; } = new List<LocoWaypointState>();

        private List<CarLoadTargetLoader> _carLoadTargetLoaders = new List<CarLoadTargetLoader>();

        private static WaypointQueueController _shared;

        public static WaypointQueueController Shared
        {
            get
            {
                if (_shared == null)
                {
                    _shared = FindObjectOfType<WaypointQueueController>();
                }
                return _shared;
            }
        }

        private IEnumerator Ticker()
        {
            WaitForSeconds t = new WaitForSeconds(0.5f);
            while (true)
            {
                yield return t;
                Tick();
            }
        }

        private void Tick()
        {
            DoQueueTickUpdate();
        }

        private void Stop()
        {
            if (_coroutine != null)
            {
                StopCoroutine(_coroutine);
            }
            _coroutine = null;
        }

        public void InitCarLoaders(bool reload = false)
        {
            if (reload || _carLoadTargetLoaders == null || _carLoadTargetLoaders.Count <= 0)
            {
                Loader.LogDebug($"Initializing list of car load target loaders");
                _carLoadTargetLoaders = FindObjectsOfType<CarLoadTargetLoader>().ToList();
            }
        }

        private void DoQueueTickUpdate()
        {
            if (WaypointStateList == null)
            {
                Loader.LogDebug("Stopping coroutine because waypoint state list is null");
                Stop();
            }

            List<LocoWaypointState> listForRemoval = new List<LocoWaypointState>();

            bool waypointsUpdated = false;
            foreach (LocoWaypointState entry in WaypointStateList)
            {
                List<ManagedWaypoint> waypointList = entry.Waypoints;
                AutoEngineerOrdersHelper ordersHelper = GetOrdersHelper(entry.Locomotive);

                // Let loco continue if it has active waypoint orders
                // or skip if not in waypoint mode
                if (HasActiveWaypoint(ordersHelper) || ordersHelper.Orders.Mode != Game.Messages.AutoEngineerMode.Waypoint)
                {
                    continue;
                }

                //Loader.LogDebug($"Loco {entry.Locomotive.Ident} has no active waypoint during tick update");

                // Resolve waypoint order
                /**
                 * Unresolved waypoint should be the latest waypoint that this coroutine sent to the loco.
                 * We can't simply always resolve the first waypoint because we wouldn't know whether the loco has 
                 * actually performed the AE move order yet.
                 */
                if (entry.UnresolvedWaypoint != null)
                {
                    if (entry.UnresolvedWaypoint.CurrentlyWaiting)
                    {
                        if (TimeWeather.Now.TotalSeconds >= entry.UnresolvedWaypoint.WaitUntilGameTotalSeconds)
                        {
                            Loader.Log($"Loco {entry.UnresolvedWaypoint.Locomotive.Ident} done waiting");
                            entry.UnresolvedWaypoint.WillWait = false;
                            entry.UnresolvedWaypoint.CurrentlyWaiting = false;
                            // We don't want to start waiting until after we resolve the current waypoint orders, but we also don't want that resolving logic to run again after we are finished waiting
                            goto AfterWaiting;
                        }
                        else
                        {
                            continue;
                        }
                    }

                    if (entry.UnresolvedWaypoint.WillRefuel && !entry.UnresolvedWaypoint.CurrentlyRefueling)
                    {
                        Loader.Log($"Start currently refueling {entry.Locomotive.Ident}");
                        entry.UnresolvedWaypoint.CurrentlyRefueling = true;
                        ResolveRefuel(entry.UnresolvedWaypoint, ordersHelper);
                        continue;
                    }

                    if (entry.UnresolvedWaypoint.CurrentlyRefueling)
                    {
                        if (IsDoneRefueling(entry.UnresolvedWaypoint))
                        {
                            Loader.Log($"Done refueling {entry.Locomotive.Ident}");
                            entry.UnresolvedWaypoint.WillRefuel = false;
                            entry.UnresolvedWaypoint.CurrentlyRefueling = false;
                            SetCarLoadTargetLoaderCanLoad(entry.UnresolvedWaypoint, false);

                            int maxSpeed = entry.UnresolvedWaypoint.MaxSpeedAfterRefueling;
                            if (maxSpeed == 0) maxSpeed = 35;
                            ordersHelper.SetOrdersValue(null, null, maxSpeedMph: maxSpeed, null, null);
                        }
                        else
                        {
                            //Loader.LogDebug($"Still refueling");
                            continue;
                        }
                    }

                    /*
                     * Locomotive must come to a complete stop before resolving coupling or uncoupling orders.
                     * Otherwise, some cars may be uncoupled and then recoupled if the train still has momentum.
                     */
                    if (Math.Abs(entry.Locomotive.velocity) > 0)
                    {
                        Loader.LogDebug($"Locomotive not stopped, continuing");
                        continue;
                    }

                    ResolveWaypointOrders(entry.UnresolvedWaypoint);

                    if (entry.UnresolvedWaypoint.WillWait)
                    {
                        if (entry.UnresolvedWaypoint.DurationOrSpecificTime == ManagedWaypoint.WaitType.Duration && entry.UnresolvedWaypoint.WaitForDurationMinutes > 0)
                        {
                            GameDateTime waitUntilTime = TimeWeather.Now.AddingMinutes(entry.UnresolvedWaypoint.WaitForDurationMinutes);
                            entry.UnresolvedWaypoint.WaitUntilGameTotalSeconds = waitUntilTime.TotalSeconds;
                            entry.UnresolvedWaypoint.CurrentlyWaiting = true;
                            Loader.Log($"Loco {entry.UnresolvedWaypoint.Locomotive.Ident} waiting {entry.UnresolvedWaypoint.WaitForDurationMinutes}m until {waitUntilTime}");
                            OnWaypointsUpdated?.Invoke();
                            continue;
                        }

                        if (entry.UnresolvedWaypoint.DurationOrSpecificTime == ManagedWaypoint.WaitType.SpecificTime)
                        {
                            if (TimetableReader.TryParseTime(entry.UnresolvedWaypoint.WaitUntilTimeString, out TimetableTime time))
                            {
                                entry.UnresolvedWaypoint.SetWaitUntilByMinutes(time.Minutes, out GameDateTime waitUntilTime);
                                entry.UnresolvedWaypoint.CurrentlyWaiting = true;
                                Loader.Log($"Loco {entry.UnresolvedWaypoint.Locomotive.Ident} waiting until {waitUntilTime}");
                                OnWaypointsUpdated?.Invoke();
                                continue;
                            }
                            else
                            {
                                Loader.Log($"Error parsing time: \"{entry.UnresolvedWaypoint.WaitUntilTimeString}\"");
                                Toast.Present("Waypoint wait time must be in HH:MM 24-hour format.");
                            }
                        }
                    }
                AfterWaiting:

                    entry.UnresolvedWaypoint = null;
                    // RemoveCurrentWaypoint gets called as a side effect of the ClearWaypoint postfix
                    ordersHelper.ClearWaypoint();
                    waypointsUpdated = true;
                }

                // Send next waypoint
                if (waypointList.Count > 0)
                {
                    ManagedWaypoint nextWaypoint = waypointList.First();
                    entry.UnresolvedWaypoint = nextWaypoint;
                    SendToWaypointFromQueue(nextWaypoint, ordersHelper);
                }

                // Mark if empty
                if (waypointList.Count == 0)
                {
                    var (assignedRouteId, loop) = RouteAssignmentRegistry.Get(entry.Locomotive.id);
                    if (loop && !string.IsNullOrEmpty(assignedRouteId))
                    {
                        var assignedRoute = RouteRegistry.GetById(assignedRouteId);
                        if (assignedRoute != null)
                        {
                            Loader.Log($"Loco {entry.Locomotive.Ident}: queue empty & looping enabled → reassigning route '{assignedRoute.Name}' (apply mode).");
                            // Re-apply the saved route, but we already know the waypoint list is currently empty so just append
                            AddWaypointsFromRoute(entry.Locomotive, assignedRoute, append: true);

                            // After reassigning, continue to next loco without marking for removal
                            continue;
                        }
                    }
                    Loader.Log($"Marking {entry.Locomotive.Ident} waypoint queue for removal");
                    listForRemoval.Add(entry);
                }
            }

            if (waypointsUpdated)
            {
                Loader.LogDebug($"Invoking OnWaypointsUpdated at end of tick loop");
                OnWaypointsUpdated?.Invoke();
            }

            // Update list of states
            WaypointStateList = WaypointStateList.FindAll(x => !listForRemoval.Contains(x));

            if (WaypointStateList.Count == 0)
            {

                Loader.Log("Stopping coroutine because queue list is empty");
                Stop();
            }
        }

        public void AddWaypoint(Car loco, Location location, string coupleToCarId, bool isReplacing)
        {
            bool isCoupling = coupleToCarId != null && coupleToCarId.Length > 0;
            string couplingLogSegment = isCoupling ? $"coupling to ${coupleToCarId}" : "no coupling";
            Loader.Log($"Trying to add waypoint for loco {loco.Ident} to {location} with {couplingLogSegment}");

            LocoWaypointState entry = GetOrAddLocoWaypointState(loco);

            ManagedWaypoint waypoint = new ManagedWaypoint(loco, location, coupleToCarId);
            CheckNearbyFuelLoaders(waypoint);

            if (isReplacing && entry.Waypoints.Count > 0)
            {
                entry.Waypoints[0] = waypoint;
                RefreshCurrentWaypoint(loco, GetOrdersHelper(loco));
            }
            else
            {
                entry.Waypoints.Add(waypoint);
            }
            Loader.Log($"Added waypoint for {waypoint.Locomotive.Ident} to {waypoint.Location}");

            OnWaypointWasAdded();
        }

        public LocoWaypointState GetOrAddLocoWaypointState(Car loco)
        {
            LocoWaypointState entry = WaypointStateList.Find(x => x.Locomotive.id == loco.id);

            if (entry == null)
            {
                Loader.LogDebug($"No existing waypoint list found for {loco.Ident}");
                entry = new LocoWaypointState(loco);
                WaypointStateList.Add(entry);
            }
            else
            {
                Loader.LogDebug($"Found existing waypoint list for {loco.Ident}");
            }
            return entry;
        }

        public void AddWaypointsFromRoute(Car loco, RouteDefinition route, bool append)
        {
            if (loco == null || route == null) return;

            if (route.Waypoints == null || route.Waypoints.Count == 0) return;

            if (!append)
            {
                ClearWaypointState(loco);
            }

            Loader.LogDebug($"Adding waypoints from {route.Name} to {loco.Ident} queue");

            var entry = GetOrAddLocoWaypointState(loco);

            int validWaypointsAdded = 0;
            foreach (var rw in route.Waypoints)
            {
                if (rw.TryCopyForRoute(out ManagedWaypoint copy, loco: loco))
                {
                    entry.Waypoints.Add(copy);
                    validWaypointsAdded++;
                }
                else
                {
                    Loader.LogDebug($"Failed to add waypoint {rw.Id} from route {route.Name} to {loco.Ident} queue");
                }
            }
            Loader.Log($"Added {validWaypointsAdded} waypoints for {loco.Ident} from route {route.Name}");
            OnWaypointWasAdded();
        }

        private void OnWaypointWasAdded()
        {
            OnWaypointsUpdated?.Invoke();

            if (_coroutine == null)
            {
                Loader.Log($"Starting waypoint coroutine after adding waypoint");
                _coroutine = StartCoroutine(Ticker());
            }
        }

        public void ClearWaypointState(Car loco)
        {
            Loader.Log($"Trying to clear waypoint state for {loco.Ident}");
            LocoWaypointState entry = WaypointStateList.Find(x => x.Locomotive.id == loco.id);
            if (entry != null)
            {
                WaypointStateList = WaypointStateList.FindAll(x => x.Locomotive.id != loco.id);
                Loader.Log($"Removed waypoint state entry for {loco.Ident}");
            }
            CancelActiveOrders(loco);
            Loader.LogDebug($"Invoking OnWaypointsUpdated in ClearWaypointState");
            OnWaypointsUpdated?.Invoke();
        }

        private void CancelActiveOrders(Car loco)
        {
            Loader.Log($"Canceling active orders for {loco.Ident}");
            GetOrdersHelper(loco).ClearWaypoint();
        }

        public void RemoveWaypoint(ManagedWaypoint waypoint)
        {
            LocoWaypointState entry = WaypointStateList.Find(x => x.Locomotive.id == waypoint.Locomotive.id);
            Loader.Log($"Removing waypoint {waypoint.Location} for {waypoint.Locomotive.Ident}");
            entry?.Waypoints.Remove(waypoint);
            if (entry.UnresolvedWaypoint.Id == waypoint.Id)
            {
                Loader.LogDebug($"Removed waypoint was unresolved. Resetting unresolved to null");
                entry.UnresolvedWaypoint = null;
            }
            if (entry.Waypoints.Count == 0)
            {
                // Removed current waypoint
                CancelActiveOrders(entry.Locomotive);
            }

            Loader.LogDebug($"Invoking OnWaypointsUpdated in RemoveWaypoint");
            OnWaypointsUpdated?.Invoke();
        }

        public void RemoveCurrentWaypoint(Car locomotive)
        {
            LocoWaypointState state = WaypointStateList.Find(x => x.Locomotive.id == locomotive.id);
            if (state != null && state.Waypoints.Count > 0)
            {
                state.Waypoints.RemoveAt(0);
                state.UnresolvedWaypoint = null;
                Loader.LogDebug($"Invoking OnWaypointsUpdated in RemoveCurrentWaypoint");
                OnWaypointsUpdated.Invoke();
            }
        }

        public void UpdateWaypoint(ManagedWaypoint updatedWaypoint)
        {
            List<ManagedWaypoint> waypointList = GetWaypointList(updatedWaypoint.Locomotive);
            if (waypointList != null)
            {
                int index = waypointList.FindIndex(w => w.Id == updatedWaypoint.Id);
                if (index >= 0)
                {
                    waypointList[index] = updatedWaypoint;
                    Loader.LogDebug($"Invoking OnWaypointsUpdated in UpdateWaypoint");
                    OnWaypointsUpdated.Invoke();
                }
            }
        }

        public void RerouteCurrentWaypoint(Car locomotive)
        {
            AutoEngineerOrdersHelper ordersHelper = GetOrdersHelper(locomotive);
            if (HasActiveWaypoint(ordersHelper))
            {
                StateManager.ApplyLocal(new AutoEngineerWaypointRerouteRequest(locomotive.id));
            }
            else
            {
                RefreshCurrentWaypoint(locomotive, ordersHelper);
            }
        }

        public void RefreshCurrentWaypoint(Car locomotive, AutoEngineerOrdersHelper ordersHelper)
        {
            LocoWaypointState state = WaypointStateList.Find(x => x.Locomotive.id == locomotive.id);
            if (state != null && state.Waypoints.Count > 0)
            {
                Loader.Log($"Resetting current waypoint as active");
                ManagedWaypoint nextWaypoint = state.Waypoints.First();
                state.UnresolvedWaypoint = nextWaypoint;
                SendToWaypointFromQueue(nextWaypoint, ordersHelper);
                Loader.LogDebug($"Invoking OnWaypointsUpdated in RemoveCurrentWaypoint");
                OnWaypointsUpdated.Invoke();
            }
        }

        public List<ManagedWaypoint> GetWaypointList(Car loco)
        {
            return WaypointStateList.Find(x => x.Locomotive.id == loco.id)?.Waypoints;
        }

        public bool HasAnyWaypoints(Car loco)
        {
            List<ManagedWaypoint> waypoints = GetWaypointList(loco);
            return waypoints != null && waypoints.Count > 0;
        }

        private bool HasActiveWaypoint(AutoEngineerOrdersHelper ordersHelper)
        {
            //Loader.LogDebug($"Locomotive {locomotive} ready for next waypoint");
            return ordersHelper.Orders.Waypoint.HasValue;
        }

        private void ResolveWaypointOrders(ManagedWaypoint waypoint)
        {
            Loader.LogDebug($"Resolving loco {waypoint.Locomotive.Ident} waypoint to {waypoint.Location}");
            if (waypoint.IsCoupling)
            {
                ResolveCouplingOrders(waypoint);
            }
            if (waypoint.IsUncoupling)
            {
                ResolveUncouplingOrders(waypoint);
            }
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

        private bool IsDoneRefueling(ManagedWaypoint waypoint)
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

            Industry industry = OpsController.Shared.AllIndustries.ToList().Find(x => x.identifier == industryId);
            if (industry == null)
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

        private void SetCarLoadTargetLoaderCanLoad(ManagedWaypoint waypoint, bool value)
        {
            CarLoadTargetLoader loaderTarget = FindCarLoadTargetLoader(waypoint);
            if (loaderTarget != null)
            {
                loaderTarget.keyValueObject[loaderTarget.canLoadBoolKey] = value;
            }
        }

        public void ResolveRefuel(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.LogDebug($"Resolving refuel");
            // maybe in the future, support refueling multiple locomotives if they are MU'd

            waypoint.MaxSpeedAfterRefueling = ordersHelper.Orders.MaxSpeedMph;
            // Set max speed of 5 to help prevent train from overrunning waypoint
            int speedWhileRefueling = 5;
            ordersHelper.SetOrdersValue(null, null, maxSpeedMph: speedWhileRefueling, null, null);

            Location locationToMove = GetRefuelLocation(waypoint, ordersHelper);
            SetCarLoadTargetLoaderCanLoad(waypoint, true);
            SendToWaypointFromRefuel(waypoint, locationToMove, ordersHelper);
        }

        private CarLoadTargetLoader FindCarLoadTargetLoader(ManagedWaypoint waypoint)
        {
            InitCarLoaders();
            Vector3 worldPosition = WorldTransformer.GameToWorld(waypoint.RefuelPoint);
            //Loader.LogDebug($"Starting search for target loader matching world point {worldPosition}");
            CarLoadTargetLoader loader = _carLoadTargetLoaders.Find(l => l.transform.position == worldPosition);
            //Loader.LogDebug($"Found matching {loader.load.name} loader at game point {waypoint.RefuelPoint}");
            return loader;
        }

        private Location GetRefuelLocation(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.LogDebug($"Finding refuel location");

            Car fuelCar = GetFuelCar((BaseLocomotive)waypoint.Locomotive);

            LoadSlot loadSlot = fuelCar.Definition.LoadSlots.Find(slot => slot.RequiredLoadIdentifier == waypoint.RefuelLoadName);

            waypoint.RefuelMaxCapacity = loadSlot.MaximumCapacity;

            int loadSlotIndex = fuelCar.Definition.LoadSlots.IndexOf(loadSlot);

            List<CarLoadTarget> carLoadTargets = fuelCar.GetComponentsInChildren<CarLoadTarget>().ToList();
            CarLoadTarget loadTarget = carLoadTargets.Find(clt => clt.slotIndex == loadSlotIndex);

            Vector3 loadTargetPosition = GetPositionFromLoadTarget(fuelCar, loadTarget);

            if (!Graph.Shared.TryGetLocationFromGamePoint(waypoint.RefuelPoint, 10f, out Location targetLoaderLocation))
            {
                throw new InvalidOperationException($"Cannot refuel at waypoint, failed to get graph location from refuel game point {waypoint.RefuelPoint}");
            }
            Loader.LogDebug($"Target {waypoint.RefuelLoadName} loader location is {targetLoaderLocation}");

            LogicalEnd closestEnd = ClosestLogicalEndTo(fuelCar, targetLoaderLocation);
            Car nearestCar = fuelCar.EnumerateCoupled(closestEnd).First();
            Location closestTrainEndLocation = nearestCar.LocationFor(closestEnd);
            Loader.LogDebug($"Nearest car to {waypoint.RefuelLoadName} loader is {nearestCar.Ident} at {closestTrainEndLocation} with logical end {(closestEnd == LogicalEnd.A ? "A" : "B")}");

            LogicalEnd furthestEnd = closestEnd == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;
            Car furthestCar = fuelCar.EnumerateCoupled(furthestEnd).First();
            Location furthestTrainEndLocation = nearestCar.LocationFor(furthestEnd);

            // Unclear how accurate this is when the train is on segment of curved track since the distance
            // would be a straight line between the points and wouldn't account for the curve.
            // May have to calculate distances based on the track segments rather than Vector3 points
            float distanceFromEndToSlot = Vector3.Distance(closestTrainEndLocation.GetPosition().ZeroY(), loadTargetPosition.ZeroY());
            Loader.LogDebug($"Distance from end of train to locomotive's {waypoint.RefuelLoadName} slot is {distanceFromEndToSlot}");

            Location orientedTargetLocation = Graph.Shared.LocationOrientedToward(targetLoaderLocation, closestTrainEndLocation);

            // If the end of the train is already past the waypoint, then it would incorrectly orient the distance
            if (IsTargetInMiddle(targetLoaderLocation, closestTrainEndLocation, furthestTrainEndLocation))
            {
                distanceFromEndToSlot = -distanceFromEndToSlot;
            }

            Location locationToMove = Graph.Shared.LocationByMoving(orientedTargetLocation, distanceFromEndToSlot, true, true);

            Loader.LogDebug($"Location to refuel {waypoint.RefuelLoadName} is {locationToMove}");
            return locationToMove;
        }

        private bool IsTargetInMiddle(Location targetLoaderLocation, Location closestTrainEndLocation, Location furthestTrainEndLocation)
        {
            // If target is in the middle, the distance between either end to the target will always be less than the length from one end to the other
            float distanceCloseToTarget = Vector3.Distance(closestTrainEndLocation.GetPosition().ZeroY(), targetLoaderLocation.GetPosition().ZeroY());

            float distanceFarToTarget = Vector3.Distance(furthestTrainEndLocation.GetPosition().ZeroY(), targetLoaderLocation.GetPosition().ZeroY());

            float distanceEndToEnd = Vector3.Distance(closestTrainEndLocation.GetPosition().ZeroY(), furthestTrainEndLocation.GetPosition().ZeroY());

            if (distanceCloseToTarget < distanceEndToEnd && distanceFarToTarget < distanceEndToEnd)
            {
                return true;
            }
            return false;
        }

        private Vector3 GetPositionFromLoadTarget(Car fuelCar, CarLoadTarget loadTarget)
        {
            // This logic is based on CarLoadTargetLoader.LoadSlotFromCar
            Matrix4x4 transformMatrix = fuelCar.GetTransformMatrix(Graph.Shared);
            Vector3 point2 = fuelCar.transform.InverseTransformPoint(loadTarget.transform.position);
            Vector3 vector = transformMatrix.MultiplyPoint3x4(point2);
            return vector;
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

        private void CheckNearbyFuelLoaders(ManagedWaypoint waypoint)
        {
            InitCarLoaders();
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

                    float radiusToSearch = 10f;

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

        private void ResolveCouplingOrders(ManagedWaypoint waypoint)
        {
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
                    SetHandbrakes(carsLeftBehind);
                }

                Car carToUncouple = carsLeftBehind.FirstOrDefault();

                if (carToUncouple != null)
                {
                    Loader.Log($"Uncoupling {carToUncouple.Ident} for cut of {clampedNumberOfCarsToCut} cars with {carsLeftBehind.Count} total left behind ");
                    UncoupleCar(carToUncouple, closestEnd);

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

        private List<Car> EnumerateCoupledToEnd(Car car, LogicalEnd directionToCount)
        {
            List<Car> result = [];
            Car currentCar = car;
            while (currentCar.TryGetAdjacentCar(directionToCount, out Car nextCar))
            {
                result.Add(nextCar);
                currentCar = nextCar;
            }
            return result;
        }

        private void ConnectAir(Car car)
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

        private void ResolveUncouplingOrders(ManagedWaypoint waypoint)
        {
            Loader.Log($"Resolving uncoupling orders");

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

            List<Car> carsRemaining = allCarsFromEnd.Where(c => !carsToCut.Contains(c)).ToList();

            List<Car> activeCut = carsRemaining;
            List<Car> inactiveCut = carsToCut;

            Loader.Log($"Seeking to uncouple {waypoint.NumberOfCarsToCut} cars from train of {allCarsFromEnd.Count} total cars with {carsRemaining.Count} cars left behind");

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

            string carsToCutFormatted = String.Join("-", carsToCut.Select(c => $"[{c.Ident}]"));
            string allCarsFromEndFormatted = String.Join("-", allCarsFromEnd.Select(c => $"[{c.Ident}]"));
            Loader.Log($"Cutting {carsToCutFormatted} from {allCarsFromEndFormatted} as {(waypoint.TakeUncoupledCarsAsActiveCut ? "active cut" : "inactive cut")}");

            if (waypoint.ApplyHandbrakesOnUncouple)
            {
                SetHandbrakes(inactiveCut);
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

            if (waypoint.BleedAirOnUncouple)
            {
                Loader.LogDebug($"Bleeding air on {inactiveCut.Count} cars");
                foreach (Car car in inactiveCut)
                {
                    car.air.BleedBrakeCylinder();
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

        private LogicalEnd ClosestLogicalEndTo(Car car, Location location)
        {
            return car.ClosestLogicalEndTo(location, Graph.Shared);
        }

        private LogicalEnd FurthestLogicalEndFrom(Car car, Location location)
        {
            LogicalEnd closestEnd = ClosestLogicalEndTo(car, location);
            LogicalEnd furthestEnd = closestEnd == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;
            return furthestEnd;
        }

        private LogicalEnd GetEndRelativeToWapoint(Car car, Location waypointLocation, bool useFurthestEnd)
        {
            LogicalEnd closestEnd = car.ClosestLogicalEndTo(waypointLocation, Graph.Shared);
            LogicalEnd furthestEnd = closestEnd == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;
            return useFurthestEnd ? furthestEnd : closestEnd;
        }

        private void UncoupleCar(Car car, LogicalEnd endToUncouple)
        {
            LogicalEnd oppositeEnd = endToUncouple == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;

            if (StateManager.IsHost && car.set != null)
            {
                if (car.TryGetAdjacentCar(endToUncouple, out var adjacent))
                {
                    // Close anglecocks on both sides to simplify uncoupling. Bleeding air is already a separate option
                    car.ApplyEndGearChange(endToUncouple, EndGearStateKey.Anglecock, f: 0f);
                    car.ApplyEndGearChange(endToUncouple, EndGearStateKey.IsCoupled, boolValue: false);
                    car.ApplyEndGearChange(endToUncouple, EndGearStateKey.IsAirConnected, boolValue: false);

                    adjacent.ApplyEndGearChange(oppositeEnd, EndGearStateKey.Anglecock, f: 0f);
                    adjacent.ApplyEndGearChange(oppositeEnd, EndGearStateKey.IsCoupled, boolValue: false);
                    adjacent.ApplyEndGearChange(oppositeEnd, EndGearStateKey.IsAirConnected, boolValue: false);
                }
            }
        }

        private void SetHandbrakes(List<Car> cars)
        {
            if (cars.Count == 0)
            {
                return;
            }
            float handbrakePercentage = Loader.Settings.HandbrakePercentOnUncouple;
            float minimum = Loader.Settings.MinimumHandbrakesOnUncouple;

            int carsToTieDown = (int)Math.Max(minimum, Math.Ceiling(cars.Count * handbrakePercentage));
            if (carsToTieDown > cars.Count) carsToTieDown = cars.Count;

            Loader.Log($"Setting handbrakes on {carsToTieDown} uncoupled cars");
            for (int i = 0; i < carsToTieDown; i++)
            {
                //if (cars[i].Archetype.IsLocomotive())
                //{
                //    carsToTieDown++;
                //    continue;
                //}

                cars[i].SetHandbrake(true);
            }
        }

        private AutoEngineerOrdersHelper GetOrdersHelper(Car locomotive)
        {
            Type plannerType = typeof(AutoEngineerPlanner);
            FieldInfo fieldInfo = plannerType.GetField("_persistence", BindingFlags.NonPublic | BindingFlags.Instance);
            AutoEngineerPersistence persistence = (AutoEngineerPersistence)fieldInfo.GetValue((locomotive as BaseLocomotive).AutoEngineerPlanner);
            AutoEngineerOrdersHelper ordersHelper = new AutoEngineerOrdersHelper(locomotive, persistence);
            return ordersHelper;
        }

        private void SendToWaypointFromQueue(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.Log($"Sending next waypoint for {waypoint.Locomotive.Ident} to {waypoint.Location}");
            ApplyTimetableSymbolIfRequested(waypoint);
            (Location, string)? maybeWaypoint = (waypoint.Location, waypoint.CoupleToCarId);
            ordersHelper.SetOrdersValue(null, null, null, null, maybeWaypoint);
        }

        private void SendToWaypointFromRefuel(ManagedWaypoint waypoint, Location refuelLocation, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.Log($"Sending refueling waypoint for {waypoint.Locomotive.Ident} to {refuelLocation}");
            ApplyTimetableSymbolIfRequested(waypoint);
            (Location, string)? maybeWaypoint = (refuelLocation, null);
            ordersHelper.SetOrdersValue(null, null, null, null, maybeWaypoint);
        }

        internal void LoadWaypointSaveState(WaypointSaveState saveState)
        {
            Loader.LogDebug($"Starting LoadWaypointSaveState");
            foreach (var entry in saveState.WaypointStates)
            {
                Loader.LogDebug($"Loading waypoint state for {entry.LocomotiveId}");
                entry.Load();
                List<ManagedWaypoint> validWaypoints = [];
                foreach (var waypoint in entry.Waypoints)
                {
                    Loader.LogDebug($"Loading waypoint {waypoint.Id}");
                    if (waypoint.IsValidWithLoco())
                    {
                        validWaypoints.Add(waypoint);
                    }
                    else
                    {
                        Loader.Log($"Failed to hydrate waypoint {waypoint?.Id}");
                    }
                }
                entry.Waypoints = validWaypoints;
                if (entry.UnresolvedWaypoint != null)
                {
                    Loader.LogDebug($"Loading unresolved waypoint {entry.UnresolvedWaypoint.Id}");
                    if (!entry.UnresolvedWaypoint.IsValidWithLoco())
                    {
                        Loader.Log($"Failed to hydrate unresolved waypoint {entry.UnresolvedWaypoint?.Id}");
                    }
                }
            }
            WaypointStateList = saveState.WaypointStates;
            Loader.LogDebug($"Invoking OnWaypointsUpdated in LoadWaypointSaveState");
            OnWaypointsUpdated?.Invoke();

            _carLoadTargetLoaders = null;
            InitCarLoaders();

            if (_coroutine == null)
            {
                Loader.LogDebug($"Starting waypoint coroutine in LoadWaypointSaveState");
                _coroutine = StartCoroutine(Ticker());
            }
            else
            {
                Loader.LogDebug($"Restarting waypoint coroutine in LoadWaypointSaveState");
                StopCoroutine(_coroutine);
                _coroutine = StartCoroutine(Ticker());
            }
        }

        private void ApplyTimetableSymbolIfRequested(ManagedWaypoint waypoint)
        {
            if (waypoint.TimetableSymbol == null) return;

            string valueToSet = string.IsNullOrEmpty(waypoint.TimetableSymbol) ? null : waypoint.TimetableSymbol;

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

            StateManager.ApplyLocal(new RequestSetTrainCrewTimetableSymbol(crewId, valueToSet));
            Loader.Log($"[Timetable] {(valueToSet ?? "None")} for {waypoint.Locomotive.Ident}");
        }
    }

}
