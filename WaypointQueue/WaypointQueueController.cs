using Game.Messages;
using Game.State;
using Model;
using Model.AI;
using RollingStock;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Track;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.UUM;
using static WaypointQueue.ModSaveManager;

namespace WaypointQueue
{
    public class WaypointQueueController : MonoBehaviour
    {
        public static event Action OnWaypointsUpdated;
        private Coroutine _coroutine;

        public List<LocoWaypointState> WaypointStateList { get; private set; } = new List<LocoWaypointState>();

        public List<CarLoadTargetLoader> CarLoadTargetLoaders { get; private set; } = new List<CarLoadTargetLoader>();

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

        public void InitCarLoaders(bool reload = false)
        {
            if (reload || CarLoadTargetLoaders == null || CarLoadTargetLoaders.Count <= 0)
            {
                Loader.LogDebug($"Initializing list of car load target loaders");
                CarLoadTargetLoaders = FindObjectsOfType<CarLoadTargetLoader>().ToList();
            }
        }

        private void DoQueueTickUpdate()
        {
            if (WaypointStateList == null)
            {
                WaypointStateList = new List<LocoWaypointState>();
            }
            HandleLoopingRoutes();

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
                    //Loader.LogDebug($"Loco {entry.Locomotive.Ident} has ACTIVE waypoint during tick update");
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
                    if (!WaypointResolver.TryHandleUnresolvedWaypoint(entry.UnresolvedWaypoint, ordersHelper, OnWaypointsUpdated))
                    {
                        continue;
                    }
                    else
                    {
                        RemoveWaypoint(entry.UnresolvedWaypoint);
                    }
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
        }

        public void AddWaypoint(Car loco, Location location, string coupleToCarId, bool isReplacing)
        {
            bool isCoupling = coupleToCarId != null && coupleToCarId.Length > 0;
            string couplingLogSegment = isCoupling ? $"coupling to ${coupleToCarId}" : "no coupling";
            Loader.Log($"Trying to add waypoint for loco {loco.Ident} to {location} with {couplingLogSegment}");

            LocoWaypointState entry = GetOrAddLocoWaypointState(loco);

            ManagedWaypoint waypoint = new ManagedWaypoint(loco, location, coupleToCarId);
            WaypointResolver.CheckNearbyFuelLoaders(waypoint);

            if (isReplacing && entry.Waypoints.Count > 0)
            {
                if (entry.Waypoints[0].Id == entry.UnresolvedWaypoint.Id)
                {
                    entry.UnresolvedWaypoint = waypoint;
                }
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

        public LocoWaypointState GetLocoWaypointState(Car loco)
        {
            LocoWaypointState entry = WaypointStateList.Find(x => x.Locomotive.id == loco.id);
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

        private void HandleLoopingRoutes()
        {
            List<RouteAssignment> assignmentList = RouteAssignmentRegistry
                .All()
                .Where(ra => ra.Loop && !WaypointStateList.Exists(entry => entry.LocomotiveId == ra.LocoId))
                .ToList();

            foreach (var ra in assignmentList)
            {
                if (TrainController.Shared.TryGetCarForId(ra.LocoId, out Car loco))
                {
                    RouteDefinition route = RouteRegistry.GetById(ra.RouteId);
                    if (route == null)
                    {
                        Loader.Log($"Failed to find route matching id {ra.RouteId}");
                        continue;
                    }
                    AddWaypointsFromRoute(loco, route, true);
                }
                else
                {
                    Loader.Log($"Failed to find loco matching id {ra.LocoId}");
                    continue;
                }
            }
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
            AutoEngineerOrdersHelper ordersHelper = GetOrdersHelper(loco);

            if (ordersHelper.Mode == AutoEngineerMode.Waypoint)
            {
                ordersHelper.ClearWaypoint();
            }
        }

        public void RemoveWaypoint(ManagedWaypoint waypoint)
        {
            Loader.Log($"Removing waypoint {waypoint.Id} {waypoint.Location} for {waypoint.Locomotive.Ident}");

            LocoWaypointState entry = GetLocoWaypointState(waypoint.Locomotive);

            string waypointId = waypoint.Id;

            if (entry.Waypoints.Remove(waypoint))
            {
                Loader.Log($"Removed waypoint {waypointId}");
            }
            else
            {
                Loader.Log($"Failed to remove waypoint {waypointId}");
            }

            if (entry.UnresolvedWaypoint.Id == waypointId)
            {
                Loader.LogDebug($"Removed waypoint was unresolved. Resetting unresolved to null");
                entry.UnresolvedWaypoint = null;
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
                RemoveWaypoint(state.Waypoints[0]);
            }
        }

        public void UpdateWaypoint(ManagedWaypoint updatedWaypoint)
        {
            LocoWaypointState state = GetLocoWaypointState(updatedWaypoint.Locomotive);

            if (state != null && state.Waypoints != null)
            {
                int index = state.Waypoints.FindIndex(w => w.Id == updatedWaypoint.Id);
                if (index >= 0)
                {
                    state.Waypoints[index] = updatedWaypoint;

                    if (updatedWaypoint.Id == state.UnresolvedWaypoint.Id)
                    {
                        Loader.LogDebug($"Updated unresolved waypoint");
                        state.UnresolvedWaypoint = updatedWaypoint;
                    }

                    Loader.LogDebug($"Invoking OnWaypointsUpdated in UpdateWaypoint");
                    OnWaypointsUpdated.Invoke();
                }
            }
        }

        public void ReorderWaypoint(ManagedWaypoint waypoint, int newIndex)
        {
            LocoWaypointState state = GetLocoWaypointState(waypoint.Locomotive);

            if (state != null && state.Waypoints != null)
            {
                int oldIndex = state.Waypoints.IndexOf(waypoint);
                if (oldIndex < 0) return;

                state.Waypoints.RemoveAt(oldIndex);

                if (newIndex > oldIndex)
                {
                    newIndex--; // the actual index could have shifted due to the removal
                }

                state.Waypoints.Insert(newIndex, waypoint);

                if (state.Waypoints[0].Id != state.UnresolvedWaypoint.Id)
                {
                    Loader.LogDebug($"Resetting unresolved waypoint after reordering waypoint list");
                    state.UnresolvedWaypoint = waypoint;
                    SendToWaypointFromQueue(waypoint, GetOrdersHelper(waypoint.Locomotive));
                }

                Loader.LogDebug($"Invoking OnWaypointsUpdated in ReorderWaypoint");
                OnWaypointsUpdated.Invoke();
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

        public bool TryGetActiveWaypointFor(Car loco, out ManagedWaypoint waypoint)
        {
            waypoint = null;

            if (loco == null)
                return false;

            // Find the LocoWaypointState for this locomotive
            var state = WaypointStateList.FirstOrDefault(x => x.Locomotive != null && x.Locomotive.id == loco.id);

            if (state == null)
                return false;

            // The "active" waypoint is the unresolved one if present, otherwise the first in the list
            var active = state.UnresolvedWaypoint ?? state.Waypoints.FirstOrDefault();
            if (active == null)
                return false;

            waypoint = active;
            return true;
        }

        private bool HasActiveWaypoint(AutoEngineerOrdersHelper ordersHelper)
        {
            //Loader.LogDebug($"Locomotive {locomotive} ready for next waypoint");
            return ordersHelper.Orders.Waypoint.HasValue;
        }

        internal AutoEngineerOrdersHelper GetOrdersHelper(Car locomotive)
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
            WaypointResolver.ApplyTimetableSymbolIfRequested(waypoint);
            SendToWaypoint(ordersHelper, waypoint.Location, waypoint.CoupleToCarId);
        }

        internal void SendToWaypoint(AutoEngineerOrdersHelper ordersHelper, Location location, string coupleToCarId = null)
        {
            (Location, string)? maybeWaypoint = (location, coupleToCarId);
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

            CarLoadTargetLoaders = null;
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


    }

}
