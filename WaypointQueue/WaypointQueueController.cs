using Game.State;
using MessagePack;
using Model;
using Model.AI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Track;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.UUM;
using static Model.Car;

namespace WaypointQueue
{
    public class WaypointQueueController : MonoBehaviour
    {
        public static event Action OnWaypointsUpdated;
        private Coroutine _coroutine;

        public List<LocoWaypointState> WaypointStateList { get; private set; } = new List<LocoWaypointState>();

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
                List<AdvancedWaypoint> waypointList = entry.Waypoints;
                AutoEngineerOrdersHelper ordersHelper = GetOrdersHelper(entry.Locomotive);

                // Let loco continue if it has active waypoint orders
                if (HasActiveWaypoint(ordersHelper))
                {
                    continue;
                }

                Loader.LogDebug($"Loco {entry.Locomotive.Ident} has no active waypoint during tick update");

                // Resolve waypoint order
                /**
                 * Unresolved waypoint should be the latest waypoint that this coroutine sent to the loco.
                 * We can't simply always resolve the first waypoint because we wouldn't know whether the loco has 
                 * actually performed the AE move order yet.
                 */
                if (entry.UnresolvedWaypoint != null)
                {
                    ResolveWaypointOrders(entry.UnresolvedWaypoint);
                    // RemoveCurrentWaypoint will be called as a side effect of ClearWaypoint postfix
                    ordersHelper.ClearWaypoint();
                    waypointsUpdated = true;
                }

                // Send next waypoint
                if (waypointList.Count > 0)
                {
                    AdvancedWaypoint nextWaypoint = waypointList.First();
                    entry.UnresolvedWaypoint = nextWaypoint;
                    SendToWaypointFromQueue(nextWaypoint, ordersHelper);
                }

                // Mark if empty
                if (waypointList.Count == 0)
                {
                    Loader.LogDebug($"Marking {entry.Locomotive.Ident} waypoint queue for removal");
                    listForRemoval.Add(entry);
                }
            }

            if (waypointsUpdated)
            {
                Loader.LogDebug($"Invoking OnWaypointsUpdated at end of coroutine");
                OnWaypointsUpdated?.Invoke();
            }

            // Update list of states
            WaypointStateList = WaypointStateList.FindAll(x => !listForRemoval.Contains(x));

            if (WaypointStateList.Count == 0)
            {
                Loader.LogDebug("Stopping coroutine because queue list is empty");
                Stop();
            }
        }

        public void AddWaypoint(Car loco, Location location, string coupleToCarId)
        {
            bool isCoupling = coupleToCarId != null && coupleToCarId.Length > 0;
            string couplingLogSegment = isCoupling ? $"coupling to ${coupleToCarId}" : "no coupling";
            Loader.LogDebug($"Trying to add waypoint for loco {loco.Ident} to {location} with {couplingLogSegment}");

            LocoWaypointState entry = WaypointStateList.Find(x => x.Locomotive.id == loco.id);

            if (entry == null)
            {
                entry = new LocoWaypointState(loco);
                WaypointStateList.Add(entry);
            }

            AdvancedWaypoint waypoint = new AdvancedWaypoint((BaseLocomotive)loco, location, coupleToCarId);
            entry.Waypoints.Add(waypoint);
            Loader.LogDebug($"Added waypoint for {waypoint.Locomotive.Ident} to {waypoint.Location}");

            OnWaypointsUpdated?.Invoke();

            if (_coroutine == null)
            {
                Loader.LogDebug($"Starting waypoint coroutine");
                _coroutine = StartCoroutine(Ticker());
            }
        }

        public void ClearWaypointState(Car loco)
        {
            Loader.LogDebug($"Trying to clear waypoint state for {loco.Ident}");
            LocoWaypointState entry = WaypointStateList.Find(x => x.Locomotive.id == loco.id);
            if (entry != null)
            {
                WaypointStateList = WaypointStateList.FindAll(x => x.Locomotive.id != loco.id);
                Loader.LogDebug($"Removed waypoint state entry for {loco.Ident}");
                CancelActiveOrders(loco);
                Loader.LogDebug($"Invoking OnWaypointsUpdated in ClearWaypointState");
                OnWaypointsUpdated?.Invoke();
            }
        }

        private void CancelActiveOrders(Car loco)
        {
            Loader.LogDebug($"Canceling active orders for {loco.Ident}");
            GetOrdersHelper(loco).ClearWaypoint();
        }

        public void RemoveWaypoint(AdvancedWaypoint waypoint)
        {
            LocoWaypointState entry = WaypointStateList.Find(x => x.Locomotive.id == waypoint.Locomotive.id);
            Loader.LogDebug($"Removing waypoint {waypoint.Location} for {waypoint.Locomotive.Ident}");
            entry.Waypoints.Remove(waypoint);
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
            RemoveCurrentWaypoint(state);
        }

        private void RemoveCurrentWaypoint(LocoWaypointState state)
        {
            if (state.Waypoints.Count > 0)
            {
                state.Waypoints.RemoveAt(0);
                state.UnresolvedWaypoint = null;
            }
        }

        public void UpdateWaypoint(AdvancedWaypoint updatedWaypoint)
        {
            List<AdvancedWaypoint> waypointList = GetWaypointList(updatedWaypoint.Locomotive);
            if (waypointList != null)
            {
                int index = waypointList.FindIndex(w => w.Id == updatedWaypoint.Id);
                if (index >= 0)
                {
                    waypointList[index] = updatedWaypoint;
                    OnWaypointsUpdated.Invoke();
                }
            }
        }

        public List<AdvancedWaypoint> GetWaypointList(Car loco)
        {
            return WaypointStateList.Find(x => x.Locomotive.id == loco.id)?.Waypoints;
        }

        public bool HasAnyWaypoints(Car loco)
        {
            List<AdvancedWaypoint> waypoints = GetWaypointList(loco);
            return waypoints != null && waypoints.Count > 0;
        }

        private bool HasActiveWaypoint(AutoEngineerOrdersHelper ordersHelper)
        {
            //Loader.LogDebug($"Locomotive {locomotive} ready for next waypoint");
            return ordersHelper.Orders.Waypoint.HasValue;
        }

        private void ResolveWaypointOrders(AdvancedWaypoint waypoint)
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

        private void ResolveCouplingOrders(AdvancedWaypoint waypoint)
        {
            Loader.LogDebug($"Resolving coupling orders");
            foreach (Car car in waypoint.Locomotive.EnumerateCoupled())
            {
                if (waypoint.ConnectAirOnCouple)
                {
                    ConnectAir(car);
                }

                if (waypoint.ReleaseHandbrakesOnCouple)
                {
                    Loader.LogDebug($"Releasing handbrake on {car.Ident}");
                    car.SetHandbrake(false);
                }
            }
        }

        private void ConnectAir(Car car)
        {
            if (StateManager.IsHost && car.set != null)
            {
                if (car.TryGetAdjacentCar(LogicalEnd.A, out var adjacent))
                {
                    Loader.LogDebug($"Connecting air from {car.Ident} to {adjacent.Ident}");
                    adjacent.ApplyEndGearChange(LogicalEnd.B, EndGearStateKey.IsAirConnected, boolValue: true);
                    adjacent.ApplyEndGearChange(LogicalEnd.B, EndGearStateKey.Anglecock, f: 1.0f);
                }
                else
                {
                    // Close anglecock if not adjacent to a car
                    Loader.LogDebug($"Closing air on {car.Ident} end without adjacent car");
                    car.ApplyEndGearChange(LogicalEnd.A, EndGearStateKey.Anglecock, f: 0.0f);
                }

                if (car.TryGetAdjacentCar(LogicalEnd.B, out var adjacent2))
                {
                    Loader.LogDebug($"Connecting air from {car.Ident} to {adjacent.Ident}");
                    adjacent2.ApplyEndGearChange(LogicalEnd.A, EndGearStateKey.IsAirConnected, boolValue: true);
                    adjacent2.ApplyEndGearChange(LogicalEnd.A, EndGearStateKey.Anglecock, f: 1.0f);
                }
                else
                {
                    // Close anglecock if not adjacent to a car
                    Loader.LogDebug($"Closing air on {car.Ident} end without adjacent car");
                    car.ApplyEndGearChange(LogicalEnd.B, EndGearStateKey.Anglecock, f: 0.0f);
                }
            }
        }

        private void ResolveUncouplingOrders(AdvancedWaypoint waypoint)
        {
            Loader.LogDebug($"Resolving uncoupling orders");
            // Cut is enumerated from the logical end closest to the waypoint,
            // so the last car in this list is the one that should be uncoupled
            List<Car> uncoupledCars = GetCarsToUncouple(waypoint);

            if (waypoint.ApplyHandbrakesOnUncouple)
            {
                SetHandbrakes(uncoupledCars);
            }

            Car carToUncouple = uncoupledCars.Last();
            Loader.LogDebug($"Uncoupling {carToUncouple.Ident} for cut of {uncoupledCars.Count} cars from train of {waypoint.Locomotive}");
            UncoupleCar(carToUncouple, waypoint.Location);

            if (waypoint.BleedAirOnUncouple)
            {
                Loader.LogDebug($"Bleeding air on {uncoupledCars.Count} cars");
                foreach (Car car in uncoupledCars)
                {
                    car.air.BleedBrakeCylinder();
                }
            }
        }

        private List<Car> GetCarsToUncouple(AdvancedWaypoint waypoint)
        {
            BaseLocomotive locomotive = waypoint.Locomotive;
            LogicalEnd logicalEnd = locomotive.ClosestLogicalEndTo(waypoint.Location, Graph.Shared);
            return locomotive.EnumerateCoupled(logicalEnd).Take(waypoint.NumberOfCarsToUncouple).ToList();
        }

        private void UncoupleCar(Car car, Location waypointLocation)
        {
            LogicalEnd logicalEndNearLocation = car.ClosestLogicalEndTo(waypointLocation, Graph.Shared);
            // Uncouple at the logical end further from the waypoint
            LogicalEnd endToUncouple = logicalEndNearLocation == LogicalEnd.A ? LogicalEnd.B : LogicalEnd.A;

            if (StateManager.IsHost && car.set != null)
            {
                if (car.TryGetAdjacentCar(endToUncouple, out var adjacent))
                {
                    // Leave anglecock open on the car being uncoupled
                    car.ApplyEndGearChange(endToUncouple, EndGearStateKey.Anglecock, f: 1.0f);
                    car.ApplyEndGearChange(endToUncouple, EndGearStateKey.IsCoupled, boolValue: false);
                    car.ApplyEndGearChange(endToUncouple, EndGearStateKey.IsAirConnected, boolValue: false);

                    // Close anglecock on the car still connected to the train
                    adjacent.ApplyEndGearChange(logicalEndNearLocation, EndGearStateKey.Anglecock, f: 0f);
                    adjacent.ApplyEndGearChange(logicalEndNearLocation, EndGearStateKey.IsCoupled, boolValue: false);
                    adjacent.ApplyEndGearChange(logicalEndNearLocation, EndGearStateKey.IsAirConnected, boolValue: false);
                }
            }
        }

        private void SetHandbrakes(List<Car> cars)
        {
            float handbrakePercentage = Loader.Settings.HandbrakePercentOnUncouple;
            float minimum = Loader.Settings.MinimumHandbrakesOnUncouple;

            int carsToTieDown = (int)Math.Max(minimum, Math.Ceiling(cars.Count * handbrakePercentage));
            if (carsToTieDown > cars.Count) carsToTieDown = cars.Count;

            Loader.Log($"Setting handbrakes on {carsToTieDown} uncoupled cars");
            for (int i = 0; i < carsToTieDown; i++)
            {
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

        private void SendToWaypointFromQueue(AdvancedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.LogDebug($"Sending next waypoint for {waypoint.Locomotive.Ident} to {waypoint.Location}");
            (Location, string)? maybeWaypoint = (waypoint.Location, waypoint.CoupleToCarId);
            ordersHelper.SetOrdersValue(null, null, null, null, maybeWaypoint);
        }

        [MessagePackObject]
        public class LocoWaypointState
        {
            public Car Locomotive;
            public List<AdvancedWaypoint> Waypoints;
            public AdvancedWaypoint UnresolvedWaypoint;

            public LocoWaypointState(Car loco)
            {
                Locomotive = loco;
                Waypoints = new List<AdvancedWaypoint>();
            }
        }
    }
}
