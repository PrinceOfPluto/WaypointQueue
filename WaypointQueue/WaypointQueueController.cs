using Game.Messages;
using Game.State;
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
using static WaypointQueue.WaypointSaveManager;

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
                List<ManagedWaypoint> waypointList = entry.Waypoints;
                AutoEngineerOrdersHelper ordersHelper = GetOrdersHelper(entry.Locomotive);

                // Let loco continue if it has active waypoint orders
                // or skip if not in waypoint mode
                if (HasActiveWaypoint(ordersHelper) || ordersHelper.Orders.Mode != Game.Messages.AutoEngineerMode.Waypoint)
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

        public void AddWaypoint(Car loco, Location location, string coupleToCarId, bool isReplacing)
        {
            bool isCoupling = coupleToCarId != null && coupleToCarId.Length > 0;
            string couplingLogSegment = isCoupling ? $"coupling to ${coupleToCarId}" : "no coupling";
            Loader.LogDebug($"Trying to add waypoint for loco {loco.Ident} to {location} with {couplingLogSegment}");

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

            ManagedWaypoint waypoint = new ManagedWaypoint(loco, location, coupleToCarId);
            if (isReplacing && entry.Waypoints.Count > 0)
            {
                entry.Waypoints[0] = waypoint;
                RefreshCurrentWaypoint(loco, GetOrdersHelper(loco));
            }
            else
            {
                entry.Waypoints.Add(waypoint);
            }
            Loader.LogDebug($"Added waypoint for {waypoint.Locomotive.Ident} to {waypoint.Location}");

            OnWaypointsUpdated?.Invoke();

            if (_coroutine == null)
            {
                Loader.LogDebug($"Starting waypoint coroutine after adding waypoint");
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
            }
            CancelActiveOrders(loco);
            Loader.LogDebug($"Invoking OnWaypointsUpdated in ClearWaypointState");
            OnWaypointsUpdated?.Invoke();
        }

        private void CancelActiveOrders(Car loco)
        {
            Loader.LogDebug($"Canceling active orders for {loco.Ident}");
            GetOrdersHelper(loco).ClearWaypoint();
        }

        public void RemoveWaypoint(ManagedWaypoint waypoint)
        {
            LocoWaypointState entry = WaypointStateList.Find(x => x.Locomotive.id == waypoint.Locomotive.id);
            Loader.LogDebug($"Removing waypoint {waypoint.Location} for {waypoint.Locomotive.Ident}");
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
                Loader.LogDebug($"Resetting current waypoint as active");
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

        private void ResolveCouplingOrders(ManagedWaypoint waypoint)
        {
            Loader.LogDebug($"Resolving coupling orders for loco {waypoint.Locomotive.Ident}");
            foreach (Car car in waypoint.Locomotive.EnumerateCoupled())
            {
                Loader.LogDebug($"Resolving coupling orders on {car.Ident}");
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

            if (waypoint.NumberOfCarsToCut > 0 && TrainController.Shared.TryGetCarForId(waypoint.CoupleToCarId, out Car carCoupledTo))
            {
                bool isTake = waypoint.TakeOrLeaveCut == ManagedWaypoint.PostCoupleCutType.Take;
                Loader.LogDebug($"Resolving post-coupling cut of type: " + (isTake ? "Take" : "Leave"));
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

                Loader.LogDebug($"Cars coupled to original coupled car: " + String.Join("-", coupledToOriginal.Select(c => $"[{c.Ident}]")));
                Car targetCar = coupledToOriginal.ElementAt(waypoint.NumberOfCarsToCut - 1);
                Loader.LogDebug($"Target car is {targetCar.Ident}");

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

                Loader.LogDebug("Post-couple cutting " + String.Join("-", carsLeftBehind.Select(c => $"[{c.Ident}]")) + " from " + String.Join("-", waypoint.Locomotive.EnumerateCoupled(directionToCountCars).Select(c => $"[{c.Ident}]")));

                // Only apply handbrakes and bleed air on cars we leave behind
                if (waypoint.ApplyHandbrakesOnUncouple)
                {
                    SetHandbrakes(carsLeftBehind);
                }

                Car carToUncouple = carsLeftBehind.First();

                Loader.LogDebug($"Uncoupling {carToUncouple.Ident} for cut of {waypoint.NumberOfCarsToCut} cars with {carsLeftBehind.Count} total left behind ");
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
                    Loader.LogDebug($"Connecting air from {car.Ident} to {adjacent.Ident}");
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
                    Loader.LogDebug($"Connecting air from {car.Ident} to {adjacent2.Ident}");
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
            Loader.LogDebug($"Resolving uncoupling orders");
            List<Car> uncoupledCars = GetCarsToUncouple(waypoint);

            if (waypoint.ApplyHandbrakesOnUncouple)
            {
                SetHandbrakes(uncoupledCars);
            }

            // The car from the cut that is adjacent to the rest of the uncut train
            Car carToUncouple = waypoint.CountUncoupledFromNearestToWaypoint ? uncoupledCars.Last() : uncoupledCars.First();

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

            Loader.LogDebug($"Uncoupling {carToUncouple.Ident} for cut of {uncoupledCars.Count} cars");
            UncoupleCar(carToUncouple, endToUncouple);

            if (waypoint.BleedAirOnUncouple)
            {
                Loader.LogDebug($"Bleeding air on {uncoupledCars.Count} cars");
                foreach (Car car in uncoupledCars)
                {
                    car.air.BleedBrakeCylinder();
                }
            }
        }

        private List<Car> GetCarsToUncouple(ManagedWaypoint waypoint)
        {
            LogicalEnd directionToCountCars = GetEndRelativeToWapoint(waypoint.Locomotive, waypoint.Location, useFurthestEnd: !waypoint.CountUncoupledFromNearestToWaypoint);

            List<Car> allCarsFromEnd = waypoint.Locomotive.EnumerateCoupled(directionToCountCars).ToList();

            // Handling the direction ensures cars to cut are at the front of the list
            List<Car> carsToCut = allCarsFromEnd.Take(waypoint.NumberOfCarsToCut).ToList();

            carsToCut = FilterAnySplitLocoTenderPairs(carsToCut);

            Loader.LogDebug("Cutting " + String.Join("-", carsToCut.Select(c => $"[{c.Ident}]") + " from " + String.Join("-", allCarsFromEnd.Select(c => $"[{c.Ident}]"))));

            return carsToCut;
        }

        private List<Car> FilterAnySplitLocoTenderPairs(List<Car> carsToCut)
        {
            // Check first and last to make sure we aren't splitting a loco and tender
            List<Car> firstAndLastCars = [carsToCut.First(), carsToCut.Last()];
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
                    // Leave anglecock open on the car being uncoupled
                    car.ApplyEndGearChange(endToUncouple, EndGearStateKey.Anglecock, f: 1.0f);
                    car.ApplyEndGearChange(endToUncouple, EndGearStateKey.IsCoupled, boolValue: false);
                    car.ApplyEndGearChange(endToUncouple, EndGearStateKey.IsAirConnected, boolValue: false);

                    // Close anglecock on the car still connected to the train
                    adjacent.ApplyEndGearChange(oppositeEnd, EndGearStateKey.Anglecock, f: 0f);
                    adjacent.ApplyEndGearChange(oppositeEnd, EndGearStateKey.IsCoupled, boolValue: false);
                    adjacent.ApplyEndGearChange(oppositeEnd, EndGearStateKey.IsAirConnected, boolValue: false);
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

        private void SendToWaypointFromQueue(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.LogDebug($"Sending next waypoint for {waypoint.Locomotive.Ident} to {waypoint.Location}");
            (Location, string)? maybeWaypoint = (waypoint.Location, waypoint.CoupleToCarId);
            ordersHelper.SetOrdersValue(null, null, null, null, maybeWaypoint);
        }

        public void LoadWaypointSaveState(WaypointSaveState saveState)
        {
            Loader.LogDebug($"Starting LoadWaypointSaveState");
            foreach (var entry in saveState.WaypointStates)
            {
                Loader.LogDebug($"Loading waypoint state for {entry.LocomotiveId}");
                entry.Load();
                foreach (var waypoint in entry.Waypoints)
                {
                    Loader.LogDebug($"Loading waypoint {waypoint.Id}");
                    waypoint.Load();
                }
                if (entry.UnresolvedWaypoint != null)
                {
                    Loader.LogDebug($"Loading unresolved waypoint {entry.UnresolvedWaypoint.Id}");
                    entry.UnresolvedWaypoint.Load();
                }
            }
            WaypointStateList = saveState.WaypointStates;
            Loader.LogDebug($"Invoking OnWaypointsUpdated in LoadWaypointSaveState");
            OnWaypointsUpdated?.Invoke();

            if (_coroutine == null)
            {
                Loader.LogDebug($"Starting waypoint coroutine in LoadWaypointSaveState");
                _coroutine = StartCoroutine(Ticker());
            }
        }
    }
}
