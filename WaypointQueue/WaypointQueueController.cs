using GalaSoft.MvvmLight.Messaging;
using Game.Events;
using Game.Messages;
using Game.State;
using Model;
using Network;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Track;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.Model;
using WaypointQueue.Services;
using WaypointQueue.State;
using WaypointQueue.UI;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    public class WaypointQueueController : MonoBehaviour
    {
        private Coroutine _coroutine;

        private WaypointResolver _waypointResolver;
        private RefuelService _refuelService;
        private ICarService _carService;
        private AutoEngineerService _autoEngineerService;

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

        public static float WaypointTickInterval = 0.5f;

        private void Awake()
        {
            Messenger.Default.Register<MapWillUnloadEvent>(this, OnMapWillUnload);
            _waypointResolver = Loader.ServiceProvider.GetService<WaypointResolver>();
            _refuelService = Loader.ServiceProvider.GetService<RefuelService>();
            _carService = Loader.ServiceProvider.GetService<ICarService>();
            _autoEngineerService = Loader.ServiceProvider.GetService<AutoEngineerService>();
        }

        private void OnMapWillUnload(MapWillUnloadEvent @event)
        {
            if (_coroutine != null)
            {
                Loader.LogDebug($"OnMapWillUnload stopping coroutine in WaypointQueueController OnMapWillUnload");
                StopCoroutine(_coroutine);
                _coroutine = null;
            }
        }

        private IEnumerator Ticker()
        {
            WaitForSeconds t = new(WaypointTickInterval);
            while (true)
            {
                yield return t;
                Tick();
            }
        }

        private void Tick()
        {
            try
            {
                using (StateManager.TransactionScope())
                {
                    DoQueueTickUpdate();
                }
            }
            catch (Exception e)
            {
                Loader.LogError(e.ToString());
                ErrorModalController.Shared.ShowTickErrorModal(e.Message);
                StopCoroutine(_coroutine);
                _coroutine = null;
            }
        }

        private void DoQueueTickUpdate()
        {
            HandleLoopingRoutes();

            List<LocoWaypointState> listForRemoval = [];
            List<LocoWaypointState> statesToProcess = [.. ModStateManager.Shared.LocoWaypointStates.Values];

            foreach (LocoWaypointState entry in statesToProcess)
            {
                List<ManagedWaypoint> waypointList = entry.Waypoints;
                AutoEngineerOrdersHelper ordersHelper = _autoEngineerService.GetOrdersHelper(entry.Locomotive);

                if (!_autoEngineerService.IsInWaypointMode(ordersHelper))
                {
                    entry.UnresolvedWaypoint = null;
                    continue;
                }

                if (!IsReadyToResolve(entry, ordersHelper))
                {
                    continue;
                }

                // Resolve waypoint order
                /**
                 * Unresolved waypoint should be the latest waypoint that this coroutine sent to the loco.
                 * We can't simply always resolve the first waypoint because we wouldn't know whether the loco has 
                 * actually performed the AE move order yet.
                 */
                if (entry.UnresolvedWaypoint != null)
                {
                    if (!_waypointResolver.HandleUnresolvedWaypoint(entry.UnresolvedWaypoint, ordersHelper, WaypointTickInterval))
                    {
                        continue;
                    }
                    else
                    {
                        //Loader.Log($"Finish resolving waypoint {state.UnresolvedWaypoint.Id} {state.UnresolvedWaypoint.Location} for {state.UnresolvedWaypoint.Locomotive.Ident}");
                        RemoveWaypoint(entry.UnresolvedWaypoint);
                    }
                }

                // Send next waypoint
                if (waypointList.Count > 0)
                {
                    ManagedWaypoint nextWaypoint = waypointList.First();
                    entry.UnresolvedWaypoint = nextWaypoint;
                    Loader.LogDebug($"Sending loco {entry.LocomotiveId} to next waypoint in queue");
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
                    listForRemoval.Add(entry);
                }
            }

            // Update list of states
            foreach (var entry in listForRemoval)
            {
                ModStateManager.Shared.RemoveLocoWaypointState(entry.LocomotiveId);
            }
        }

        private bool IsReadyToResolve(LocoWaypointState entry, AutoEngineerOrdersHelper ordersHelper)
        {
            try
            {
                bool readyToResolve = !_autoEngineerService.HasActiveWaypoint(ordersHelper) && _autoEngineerService.IsInWaypointMode(ordersHelper);
                return readyToResolve || NeedsForceResolve(entry);
            }
            catch (Exception e)
            {
                throw new QueueTickException($"Exception while checking if waypoint for {entry.Locomotive.Ident} is ready to resolve: {e.Message}", e);
            }
        }

        private bool NeedsForceResolve(LocoWaypointState entry)
        {
            if (entry.UnresolvedWaypoint == null)
            {
                return false;
            }

            bool atEndOfTrack = _autoEngineerService.AtEndOfTrack(entry.Locomotive as BaseLocomotive);
            bool isNearWaypoint = _autoEngineerService.IsNearWaypoint(entry.UnresolvedWaypoint);
            bool isTrainStopped = _waypointResolver.IsTrainStopped(entry.UnresolvedWaypoint);
            bool needsEndOfTrackResolve = atEndOfTrack && isNearWaypoint && isTrainStopped;

            bool needsAlreadyCoupledResolve = IsUnresolvedWaypointAlreadyCoupled(entry.UnresolvedWaypoint);

            return needsEndOfTrackResolve || needsAlreadyCoupledResolve;
        }

        private bool IsUnresolvedWaypointAlreadyCoupled(ManagedWaypoint wp)
        {
            if (wp.IsCoupling && wp.TryResolveCoupleToCar(out Car car))
            {
                List<Car> consist = [.. wp.Locomotive.EnumerateCoupled()];
                if (consist.Contains(car))
                {
                    return true;
                }
            }
            return false;
        }

        public void AddWaypoint(BaseLocomotive loco, Location location, string coupleToCarId, bool isReplacing, bool isInsertingNext)
        {
            Location clampedLocation = location.Clamped();
            bool isCoupling = coupleToCarId != null && coupleToCarId.Length > 0;
            string couplingLogSegment = isCoupling ? $"coupling to ${coupleToCarId}" : "no coupling";
            string actionName = "add";
            if (isReplacing) actionName = "replace";
            if (isInsertingNext) actionName = "insert next";
            Loader.Log($"Trying to {actionName} waypoint for loco {loco.Ident} to {clampedLocation} with {couplingLogSegment}");

            LocoWaypointState entry = ModStateManager.Shared.GetLocoWaypointState(loco.id);
            ManagedWaypoint waypoint = new ManagedWaypoint(loco, clampedLocation, coupleToCarId);
            _refuelService.CheckNearbyFuelLoaders(waypoint);

            if (isReplacing && entry.Waypoints.Count > 0)
            {
                if (entry.Waypoints[0].Id == entry.UnresolvedWaypoint.Id)
                {
                    _waypointResolver.CleanupBeforeRemovingWaypoint(entry.UnresolvedWaypoint);
                    entry.UnresolvedWaypoint = waypoint;
                }
                entry.Waypoints[0] = waypoint;
                SendToWaypointFromQueue(waypoint, _autoEngineerService.GetOrdersHelper(loco));
                Loader.LogDebug($"Replaced waypoint for {waypoint.Locomotive.Ident} to {waypoint.Location}");
                RestartCoroutine();
                return;
            }
            else if (isInsertingNext && entry.Waypoints.Count > 0)
            {
                entry.Waypoints.Insert(1, waypoint);
            }
            else
            {
                entry.Waypoints.Add(waypoint);
            }

            Loader.LogDebug($"Saving loco queue state after adding waypoint for loco id: {loco.id}");
            ModStateManager.Shared.SaveLocoWaypointState(loco.id, entry);
            Loader.Log($"Added waypoint for {waypoint.Locomotive.Ident} to {waypoint.Location}");
            RestartCoroutine();
        }

        public void InsertWaypoint(BaseLocomotive loco, Location location, string coupledToCarId, string beforeWaypointId)
        {
            Location clampedLocation = location.Clamped();
            LocoWaypointState locoState = ModStateManager.Shared.GetLocoWaypointState(loco.id); ;
            ManagedWaypoint waypoint = new ManagedWaypoint(loco, location, coupledToCarId);
            _refuelService.CheckNearbyFuelLoaders(waypoint);

            int beforeWaypointIndex = locoState.Waypoints?.FindIndex(w => w.Id == beforeWaypointId) ?? 0;
            locoState.Waypoints.Insert(beforeWaypointIndex, waypoint);

            locoState.UnresolvedWaypoint = locoState.Waypoints.FirstOrDefault();

            Loader.LogDebug($"Saving loco queue state after inserting waypoint for loco id: {loco.id}");
            ModStateManager.Shared.SaveLocoWaypointState(loco.id, locoState);
            Loader.Log($"Inserted waypoint for {waypoint.Locomotive.Ident} to {waypoint.Location} at index {beforeWaypointIndex}");
            RestartCoroutine();
        }

        public void AddWaypointsFromRoute(BaseLocomotive loco, RouteDefinition route, bool append)
        {
            if (loco == null || route == null) return;

            if (route.Waypoints == null || route.Waypoints.Count == 0) return;

            Loader.LogDebug($"Adding waypoints from {route.Name} to {loco.Ident} queue");

            var state = ModStateManager.Shared.GetLocoWaypointState(loco.id);

            int validWaypointsAdded = 0;
            foreach (var rw in route.Waypoints)
            {
                if (rw.TryCopyForRoute(out ManagedWaypoint copy, loco: loco))
                {
                    state.Waypoints.Add(copy);
                    validWaypointsAdded++;
                }
                else
                {
                    Loader.LogDebug($"Failed to add waypoint {rw.Id} from route {route.Name} to {loco.Ident} queue");
                }
            }
            Loader.LogDebug($"Saving loco queue state after adding waypoints from route id {route.Id} for loco id: {loco.id}");
            ModStateManager.Shared.SaveLocoWaypointState(loco.id, state);
            Loader.Log($"Added {validWaypointsAdded} waypoints for {loco.Ident} from route {route.Name}");
            RestartCoroutine();
        }

        private void HandleLoopingRoutes()
        {
            try
            {
                TryHandleLoopingRoutes();
            }
            catch (Exception e)
            {
                throw new QueueTickException("Failed to handle looping routes", e);
            }
        }

        private void TryHandleLoopingRoutes()
        {
            IEnumerable<RouteAssignment> assignmentsToCheck = ModStateManager.Shared.RouteAssignments.Values.Where(ra => ra.Loop);

            foreach (var assignment in assignmentsToCheck)
            {
                LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(assignment.LocoId);
                if (state.Waypoints.Count == 0)
                {
                    if (TrainController.Shared.TryGetCarForId(assignment.LocoId, out Car loco) && loco is BaseLocomotive)
                    {
                        RouteDefinition route = RouteRegistry.GetById(assignment.RouteId);
                        if (route == null)
                        {
                            Loader.LogError($"Failed to find route matching id {assignment.RouteId}");
                            continue;
                        }
                        AddWaypointsFromRoute((BaseLocomotive)loco, route, true);
                    }
                    else
                    {
                        Loader.LogError($"Failed to find loco matching id {assignment.LocoId}");
                        continue;
                    }
                }
            }
        }

        public void RemoveWaypoint(ManagedWaypoint waypoint)
        {
            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(waypoint.LocomotiveId);
            int index = state.Waypoints.FindIndex(w => w.Id == waypoint.Id);

            if (index < 0)
            {
                Loader.LogError($"Failed to find waypoint to remove by id {waypoint.Id}");
                return;
            }

            ManagedWaypoint waypointToRemove = state.Waypoints[index];

            if (waypointToRemove.Id == state.UnresolvedWaypoint.Id)
            {
                state.UnresolvedWaypoint = null;
            }

            state.Waypoints.RemoveAt(index);

            Loader.LogDebug($"Saving loco queue state after removing waypoint {waypoint.Id} for loco id: {waypoint.LocomotiveId}");
            ModStateManager.Shared.SaveLocoWaypointState(state.LocomotiveId, state);
        }

        public void RemoveCurrentWaypoint(string locoId)
        {
            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(locoId);
            if (state.Waypoints.Count > 0)
            {
                RemoveWaypoint(state.Waypoints[0]);
            }
        }

        public void UpdateWaypoint(ManagedWaypoint waypoint)
        {
            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(waypoint.LocomotiveId);

            int index = state.Waypoints.FindIndex(w => w.Id == waypoint.Id);

            if (index < 0)
            {
                Loader.LogError($"Failed to find waypoint to update by id {waypoint.Id}");
                return;
            }

            state.Waypoints[index] = waypoint;

            state.UnresolvedWaypoint = state.Waypoints.FirstOrDefault();

            Loader.LogDebug($"Saving loco queue state after updating waypoint {waypoint.Id} for loco id: {waypoint.LocomotiveId}");
            ModStateManager.Shared.SaveLocoWaypointState(state.LocomotiveId, state);
        }

        public void ReorderWaypoint(ManagedWaypoint waypoint, int newIndex)
        {
            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(waypoint.LocomotiveId);
            int oldIndex = state.Waypoints.FindIndex(w => w.Id == waypoint.Id);
            if (oldIndex < 0) return;

            state.Waypoints.RemoveAt(oldIndex);

            if (newIndex > oldIndex)
            {
                newIndex--; // the actual index could have shifted due to the removal
            }

            state.Waypoints.Insert(newIndex, waypoint);

            state.UnresolvedWaypoint = state.Waypoints.FirstOrDefault();

            Loader.LogDebug($"Saving loco queue state after reordering waypoint {waypoint.Id} for loco id: {waypoint.LocomotiveId}");
            ModStateManager.Shared.SaveLocoWaypointState(state.LocomotiveId, state);
        }

        public void RerouteCurrentWaypoint(BaseLocomotive locomotive)
        {
            StateManager.ApplyLocal(new AutoEngineerWaypointRerouteRequest(locomotive.id));
        }

        public void RefreshCurrentWaypoint(BaseLocomotive locomotive, AutoEngineerOrdersHelper ordersHelper)
        {
            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(locomotive.id);
            if (state.Waypoints.Count > 0)
            {
                Loader.Log($"Resetting current waypoint as active");
                ManagedWaypoint nextWaypoint = state.Waypoints.First();
                state.UnresolvedWaypoint = nextWaypoint;
                SendToWaypointFromQueue(nextWaypoint, ordersHelper);
            }
        }

        public bool TryGetActiveWaypointFor(BaseLocomotive loco, out ManagedWaypoint waypoint)
        {
            waypoint = null;

            if (loco == null)
                return false;

            // Find the LocoWaypointState for this locomotive
            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(loco.id);

            // The "active" waypoint is the unresolved one if present, otherwise the first in the list
            var active = state.UnresolvedWaypoint ?? state.Waypoints.FirstOrDefault();
            if (active == null)
                return false;

            waypoint = active;
            return true;
        }

        internal void SendToWaypointFromQueue(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.Log($"Sending next waypoint for {waypoint.Locomotive.Ident} to {waypoint.Location}");
            _waypointResolver.ApplyTimetableSymbolIfRequested(waypoint);
            waypoint.StatusLabel = "Running to waypoint";
            UpdateWaypoint(waypoint);
            _autoEngineerService.SendToWaypoint(ordersHelper, waypoint.Location, waypoint.CoupleToCarId);
        }

        internal void SendToFirstWaypoint(LocoWaypointState state)
        {
            SendToWaypointFromQueue(state.Waypoints[0], _autoEngineerService.GetOrdersHelper(state.Locomotive));
        }

        internal void RestartCoroutine()
        {
            if (Multiplayer.IsHost)
            {
                if (_coroutine == null)
                {
                    Loader.LogDebug($"Starting waypoint coroutine");
                    _coroutine = StartCoroutine(Ticker());
                }
                else
                {
                    Loader.LogDebug($"Restarting waypoint coroutine");
                    StopCoroutine(_coroutine);
                    _coroutine = StartCoroutine(Ticker());
                }
            }
        }
    }

}
