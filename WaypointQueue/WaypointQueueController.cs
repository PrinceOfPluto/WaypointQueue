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
        public static event Action<ManagedWaypoint> WaypointDidUpdate;

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

            foreach (LocoWaypointState entry in ModStateManager.Shared.ActiveWaypointQueues)
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

            LocoWaypointState entry = ModStateManager.Shared.GetLocoWaypointState(loco);

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
            }
            else if (isInsertingNext && entry.Waypoints.Count > 0)
            {
                entry.Waypoints.Insert(1, waypoint);
            }
            else
            {
                entry.Waypoints.Add(waypoint);
            }

            ModStateManager.Shared.SaveLocoWaypointState(loco, entry);
            Loader.Log($"Added waypoint for {waypoint.Locomotive.Ident} to {waypoint.Location}");
            RestartCoroutine();
        }

        public void InsertWaypoint(BaseLocomotive loco, Location location, string coupledToCarId, string beforeWaypointId)
        {
            Location clampedLocation = location.Clamped();
            LocoWaypointState locoState = ModStateManager.Shared.GetLocoWaypointState(loco);
            ManagedWaypoint waypoint = new ManagedWaypoint(loco, location, coupledToCarId);
            _refuelService.CheckNearbyFuelLoaders(waypoint);

            int beforeWaypointIndex = locoState.Waypoints?.FindIndex(w => w.Id == beforeWaypointId) ?? 0;
            locoState.Waypoints.Insert(beforeWaypointIndex, waypoint);

            if (beforeWaypointIndex == 0)
            {
                _waypointResolver.CleanupBeforeRemovingWaypoint(locoState.UnresolvedWaypoint);
                locoState.UnresolvedWaypoint = waypoint;
                SendToWaypointFromQueue(waypoint, _autoEngineerService.GetOrdersHelper(loco));
            }

            ModStateManager.Shared.SaveLocoWaypointState(loco, locoState);
            Loader.Log($"Inserted waypoint for {waypoint.Locomotive.Ident} to {waypoint.Location} at index {beforeWaypointIndex}");
            RestartCoroutine();
        }

        public void AddWaypointsFromRoute(BaseLocomotive loco, RouteDefinition route, bool append)
        {
            if (loco == null || route == null) return;

            if (route.Waypoints == null || route.Waypoints.Count == 0) return;

            Loader.LogDebug($"Adding waypoints from {route.Name} to {loco.Ident} queue");

            var state = ModStateManager.Shared.GetLocoWaypointState(loco);

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
            ModStateManager.Shared.SaveLocoWaypointState(loco, state);
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
            List<RouteAssignment> assignmentList = RouteAssignmentRegistry
                .All()
                .Where(ra => ra.Loop && !ModStateManager.Shared.ActiveWaypointQueues.Any(s => s.Waypoints.Count == 0 && s.LocomotiveId == ra.LocoId))
                .ToList();

            foreach (var ra in assignmentList)
            {
                if (TrainController.Shared.TryGetCarForId(ra.LocoId, out Car loco) && loco is BaseLocomotive)
                {
                    RouteDefinition route = RouteRegistry.GetById(ra.RouteId);
                    if (route == null)
                    {
                        Loader.LogError($"Failed to find route matching id {ra.RouteId}");
                        continue;
                    }
                    AddWaypointsFromRoute((BaseLocomotive)loco, route, true);
                }
                else
                {
                    Loader.LogError($"Failed to find loco matching id {ra.LocoId}");
                    continue;
                }
            }
        }

        public void RemoveWaypoint(ManagedWaypoint waypoint)
        {
            Loader.Log($"Removing waypoint {waypoint.Id} {waypoint.Location} for {waypoint.Locomotive.Ident}");

            _waypointResolver.CleanupBeforeRemovingWaypoint(waypoint);

            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(waypoint.Locomotive);

            int indexOfWaypoint = state.Waypoints.FindIndex(w => w.Id == waypoint.Id);

            if (indexOfWaypoint >= 0)
            {
                state.Waypoints.RemoveAt(indexOfWaypoint);
                Loader.Log($"Removed waypoint {waypoint.Id}");
            }
            else
            {
                Loader.LogError($"Failed to find waypoint for removal by id {waypoint.Id}");
            }

            if (state.UnresolvedWaypoint.Id == waypoint.Id)
            {
                Loader.LogDebug($"Removed waypoint was unresolved. Resetting unresolved to null");
                state.UnresolvedWaypoint = null;
                _autoEngineerService.CancelActiveOrders(state.Locomotive);
            }

            ModStateManager.Shared.SaveLocoWaypointState(waypoint.Locomotive, state);
        }

        public void RemoveCurrentWaypoint(BaseLocomotive locomotive)
        {
            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(locomotive);
            if (state.Waypoints.Count > 0)
            {
                RemoveWaypoint(state.Waypoints[0]);
            }
        }

        public void UpdateWaypoint(ManagedWaypoint updatedWaypoint)
        {
            Loader.LogDebug($"Updating waypoint");
            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(updatedWaypoint.Locomotive);
            if (state.Waypoints != null && state.Waypoints.Count > 0)
            {
                int index = state.Waypoints.FindIndex(w => w.Id == updatedWaypoint.Id);
                if (index >= 0)
                {
                    state.Waypoints[index] = updatedWaypoint;

                    if (updatedWaypoint.Id == state.UnresolvedWaypoint.Id)
                    {
                        Loader.LogDebug($"Updated unresolved waypoint");
                        state.UnresolvedWaypoint = updatedWaypoint;

                        (Location? currentOrdersLocation, string currentOrdersCoupleToCarId) = _autoEngineerService.GetCurrentOrderWaypoint(updatedWaypoint.Locomotive);

                        if (currentOrdersLocation != updatedWaypoint.Location || currentOrdersCoupleToCarId != updatedWaypoint.CoupleToCarId)
                        {
                            updatedWaypoint.StatusLabel = "Running to waypoint";
                            var ordersHelper = _autoEngineerService.GetOrdersHelper(updatedWaypoint.Locomotive);
                            _autoEngineerService.SendToWaypoint(ordersHelper, updatedWaypoint.Location, updatedWaypoint.CoupleToCarId);
                        }
                    }

                    ModStateManager.Shared.SaveLocoWaypointState(updatedWaypoint.Locomotive, state);
                    Loader.LogDebug($"Invoking WaypointDidUpdate in UpdateWaypoint");
                    WaypointDidUpdate.Invoke(updatedWaypoint);
                }
                else
                {
                    Loader.LogError($"Failed to find waypoint for update by id {updatedWaypoint.Id}");
                }
            }
        }

        public void ReorderWaypoint(ManagedWaypoint waypoint, int newIndex)
        {
            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(waypoint.Locomotive);
            if (state.Waypoints != null && state.Waypoints.Count > 0)
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
                }


                ModStateManager.Shared.SaveLocoWaypointState(waypoint.Locomotive, state);
            }
        }

        public void RerouteCurrentWaypoint(BaseLocomotive locomotive)
        {
            AutoEngineerOrdersHelper ordersHelper = _autoEngineerService.GetOrdersHelper(locomotive);
            if (_autoEngineerService.HasActiveWaypoint(ordersHelper))
            {
                StateManager.ApplyLocal(new AutoEngineerWaypointRerouteRequest(locomotive.id));
            }
            else
            {
                RefreshCurrentWaypoint(locomotive, ordersHelper);
            }
        }

        public void RefreshCurrentWaypoint(BaseLocomotive locomotive, AutoEngineerOrdersHelper ordersHelper)
        {
            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(locomotive);
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
            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(loco);

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
