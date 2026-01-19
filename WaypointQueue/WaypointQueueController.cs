using GalaSoft.MvvmLight.Messaging;
using Game.Events;
using Game.Messages;
using Game.State;
using Model;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Track;
using UI.Common;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.Model;
using WaypointQueue.Services;
using WaypointQueue.UI;
using WaypointQueue.UUM;
using static WaypointQueue.ModSaveManager;

namespace WaypointQueue
{
    public class WaypointQueueController : MonoBehaviour
    {
        public static event Action<string> LocoWaypointStateDidUpdate;
        public static event Action<ManagedWaypoint> WaypointDidUpdate;

        private Coroutine _coroutine;

        public readonly Dictionary<string, LocoWaypointState> WaypointStateMap = [];

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
                WaypointStateMap.Clear();
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
                DoQueueTickUpdate();
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

            foreach (LocoWaypointState entry in WaypointStateMap.Values)
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
                        Loader.Log($"Finish resolving waypoint {entry.UnresolvedWaypoint.Id} {entry.UnresolvedWaypoint.Location} for {entry.UnresolvedWaypoint.Locomotive.Ident}");
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
                WaypointStateMap.Remove(entry.LocomotiveId);
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

        public void AddWaypoint(Car loco, Location location, string coupleToCarId, bool isReplacing, bool isInsertingNext)
        {
            Location clampedLocation = location.Clamped();
            bool isCoupling = coupleToCarId != null && coupleToCarId.Length > 0;
            string couplingLogSegment = isCoupling ? $"coupling to ${coupleToCarId}" : "no coupling";
            string actionName = "add";
            if (isReplacing) actionName = "replace";
            if (isInsertingNext) actionName = "insert next";
            Loader.Log($"Trying to {actionName} waypoint for loco {loco.Ident} to {clampedLocation} with {couplingLogSegment}");

            LocoWaypointState entry = GetOrAddLocoWaypointState(loco);

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
                RefreshCurrentWaypoint(loco, _autoEngineerService.GetOrdersHelper(loco));
            }
            else if (isInsertingNext && entry.Waypoints.Count > 0)
            {
                entry.Waypoints.Insert(1, waypoint);
            }
            else
            {
                entry.Waypoints.Add(waypoint);
            }
            Loader.Log($"Added waypoint for {waypoint.Locomotive.Ident} to {waypoint.Location}");

            OnWaypointWasAdded(loco.id);
        }

        public LocoWaypointState GetOrAddLocoWaypointState(Car loco)
        {
            if (WaypointStateMap.TryGetValue(loco.id, out LocoWaypointState entry))
            {
                Loader.LogDebug($"Found existing waypoint list for {loco.Ident}");
            }
            else
            {
                Loader.LogDebug($"No existing waypoint list found for {loco.Ident}");
                entry = new LocoWaypointState(loco);
                WaypointStateMap.Add(loco.id, entry);
            }
            return entry;
        }

        public void AddWaypointsFromRoute(Car loco, RouteDefinition route, bool append)
        {
            if (loco == null || route == null) return;

            if (route.Waypoints == null || route.Waypoints.Count == 0) return;

            if (!append)
            {
                ClearWaypointState(loco.id);
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
            OnWaypointWasAdded(loco.id);
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
                .Where(ra => ra.Loop && !WaypointStateMap.ContainsKey(ra.LocoId))
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

        private void OnWaypointWasAdded(string locoId)
        {
            LocoWaypointStateDidUpdate.Invoke(locoId);

            if (_coroutine == null)
            {
                Loader.Log($"Starting waypoint coroutine after adding waypoint");
                _coroutine = StartCoroutine(Ticker());
            }
        }

        public void ClearWaypointState(string locoId)
        {
            if (WaypointStateMap.TryGetValue(locoId, out LocoWaypointState entry))
            {
                if (entry.UnresolvedWaypoint != null)
                {
                    _waypointResolver.CleanupBeforeRemovingWaypoint(entry.UnresolvedWaypoint);
                }

                WaypointStateMap.Remove(locoId);
                Loader.Log($"Removed waypoint state entry for {entry.Locomotive}");
                _autoEngineerService.CancelActiveOrders(entry.Locomotive);
                Loader.LogDebug($"Invoking LocoWaypointStateDidUpdate in ClearWaypointState");
                LocoWaypointStateDidUpdate.Invoke(locoId);
            }
        }

        public void RemoveWaypoint(ManagedWaypoint waypoint)
        {
            Loader.Log($"Removing waypoint {waypoint.Id} {waypoint.Location} for {waypoint.Locomotive.Ident}");

            if (WaypointStateMap.TryGetValue(waypoint.Locomotive.id, out LocoWaypointState entry))
            {
                string waypointId = waypoint.Id;

                _waypointResolver.CleanupBeforeRemovingWaypoint(waypoint);

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
                    _autoEngineerService.CancelActiveOrders(entry.Locomotive);
                }

                Loader.LogDebug($"Invoking LocoWaypointStateDidUpdate in RemoveWaypoint");
                LocoWaypointStateDidUpdate.Invoke(entry.LocomotiveId);
            }
        }

        public void RemoveCurrentWaypoint(Car locomotive)
        {
            if (WaypointStateMap.TryGetValue(locomotive.id, out LocoWaypointState state) && state.Waypoints.Count > 0)
            {
                RemoveWaypoint(state.Waypoints[0]);
            }
        }

        public void UpdateWaypoint(ManagedWaypoint updatedWaypoint)
        {
            Loader.LogDebug($"Updating waypoint");
            if (WaypointStateMap.TryGetValue(updatedWaypoint.Locomotive.id, out LocoWaypointState state) && state.Waypoints != null)
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

                    Loader.LogDebug($"Invoking WaypointDidUpdate in UpdateWaypoint");
                    WaypointDidUpdate.Invoke(updatedWaypoint);
                }
            }
        }

        public void ReorderWaypoint(ManagedWaypoint waypoint, int newIndex)
        {
            if (WaypointStateMap.TryGetValue(waypoint.Locomotive.id, out LocoWaypointState state) && state.Waypoints != null)
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
                    _waypointResolver.CleanupBeforeRemovingWaypoint(state.UnresolvedWaypoint);
                    Loader.LogDebug($"Resetting unresolved waypoint after reordering waypoint list");
                    state.UnresolvedWaypoint = waypoint;
                    SendToWaypointFromQueue(waypoint, _autoEngineerService.GetOrdersHelper(waypoint.Locomotive));
                }

                Loader.LogDebug($"Invoking LocoWaypointStateDidUpdate in ReorderWaypoint");
                LocoWaypointStateDidUpdate.Invoke(waypoint.LocomotiveId);
            }
        }

        public void RerouteCurrentWaypoint(Car locomotive)
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

        public void RefreshCurrentWaypoint(Car locomotive, AutoEngineerOrdersHelper ordersHelper)
        {
            if (WaypointStateMap.TryGetValue(locomotive.id, out LocoWaypointState state) && state.Waypoints.Count > 0)
            {
                Loader.Log($"Resetting current waypoint as active");
                ManagedWaypoint nextWaypoint = state.Waypoints.First();
                state.UnresolvedWaypoint = nextWaypoint;
                SendToWaypointFromQueue(nextWaypoint, ordersHelper);
                Loader.LogDebug($"Invoking LocoWaypointStateDidUpdate in RemoveCurrentWaypoint");
                LocoWaypointStateDidUpdate.Invoke(locomotive.id);
            }
        }

        public bool HasWaypointState(string locoId)
        {
            return WaypointStateMap.ContainsKey(locoId);
        }

        public List<ManagedWaypoint> GetWaypointList(Car loco)
        {
            WaypointStateMap.TryGetValue(loco.id, out LocoWaypointState state);
            return state?.Waypoints ?? [];
        }

        public bool TryGetActiveWaypointFor(Car loco, out ManagedWaypoint waypoint)
        {
            waypoint = null;

            if (loco == null)
                return false;

            // Find the LocoWaypointState for this locomotive
            if (!WaypointStateMap.TryGetValue(loco.id, out LocoWaypointState state))
                return false;

            // The "active" waypoint is the unresolved one if present, otherwise the first in the list
            var active = state.UnresolvedWaypoint ?? state.Waypoints.FirstOrDefault();
            if (active == null)
                return false;

            waypoint = active;
            return true;
        }

        private void SendToWaypointFromQueue(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.Log($"Sending next waypoint for {waypoint.Locomotive.Ident} to {waypoint.Location}");
            _waypointResolver.ApplyTimetableSymbolIfRequested(waypoint);
            waypoint.StatusLabel = "Running to waypoint";
            UpdateWaypoint(waypoint);
            _autoEngineerService.SendToWaypoint(ordersHelper, waypoint.Location, waypoint.CoupleToCarId);
        }

        internal void LoadWaypointSaveState(WaypointSaveState saveState)
        {
            WaypointStateMap.Clear();

            List<string> unresolvedLocomotiveIds = [];
            Dictionary<string, List<ManagedWaypoint>> unresolvedLocationsByLocoId = [];
            Dictionary<string, List<ManagedWaypoint>> unresolvedCoupleToCarIdsByLocoId = [];
            Dictionary<string, List<ManagedWaypoint>> unresolvedDestinationIdsByLocoId = [];

            Loader.LogDebug($"Starting LoadWaypointSaveState");
            WaypointStateMap.Clear();
            foreach (var entry in saveState.WaypointStates)
            {
                Loader.LogDebug($"Loading waypoint state for {entry.LocomotiveId}");

                if (!entry.TryResolveLocomotive(out Car loco))
                {
                    unresolvedLocomotiveIds.Add(entry.LocomotiveId);
                    break;
                }

                List<ManagedWaypoint> validWaypoints = [];
                foreach (var waypoint in entry.Waypoints)
                {
                    Loader.LogDebug($"Loading waypoint {waypoint.Id}");
                    if (!waypoint.TryResolveLocomotive(out loco) && !unresolvedLocomotiveIds.Contains(waypoint.LocomotiveId))
                    {
                        unresolvedLocomotiveIds.Add(waypoint.LocomotiveId);
                        break;
                    }

                    if (!waypoint.TryResolveLocation(out Location loc))
                    {
                        if (unresolvedLocationsByLocoId.TryGetValue(loco.id, out List<ManagedWaypoint> waypoints))
                        {
                            waypoints.Add(waypoint);
                        }
                        else
                        {
                            unresolvedLocationsByLocoId.Add(waypoint.LocomotiveId, [waypoint]);
                        }
                        break;
                    }

                    if (!String.IsNullOrEmpty(waypoint.CoupleToCarId) && !waypoint.TryResolveCoupleToCar(out Car coupleToCar))
                    {
                        if (unresolvedCoupleToCarIdsByLocoId.TryGetValue(loco.id, out List<ManagedWaypoint> waypoints))
                        {
                            waypoints.Add(waypoint);
                        }
                        else
                        {
                            unresolvedCoupleToCarIdsByLocoId.Add(waypoint.LocomotiveId, [waypoint]);
                        }
                        break;
                    }
                    if (waypoint.WillUncoupleByDestination && !waypoint.CheckValidUncoupleDestinationId())
                    {
                        if (unresolvedDestinationIdsByLocoId.TryGetValue(loco.id, out List<ManagedWaypoint> waypoints))
                        {
                            waypoints.Add(waypoint);
                        }
                        else
                        {
                            unresolvedDestinationIdsByLocoId.Add(waypoint.LocomotiveId, [waypoint]);
                        }
                        break;
                    }
                    waypoint.TryResolveCouplingSearchText(out Car _);
                    waypoint.TryResolveUncouplingSearchText(out Car _);

                    validWaypoints.Add(waypoint);
                }
                entry.Waypoints = validWaypoints;

                if (entry.UnresolvedWaypoint != null)
                {
                    Loader.LogDebug($"Loading unresolved waypoint {entry.UnresolvedWaypoint.Id}");
                    if (!entry.UnresolvedWaypoint.IsValidWithLoco())
                    {
                        Loader.LogError($"Failed to hydrate unresolved waypoint {entry.UnresolvedWaypoint?.Id}");
                    }
                }

                WaypointStateMap.Add(entry.LocomotiveId, entry);
            }

            string unresolvedLocoIdsLogLine = "";
            if (unresolvedLocomotiveIds.Count > 0)
            {
                unresolvedLocoIdsLogLine = $"{unresolvedLocomotiveIds.Count} locomotive car ids could not be found.\n";
                Loader.LogError($"Failed to resolve {unresolvedLocomotiveIds.Count} locomotive car ids. {String.Join(",", unresolvedLocomotiveIds.Select(s => s))}");
            }

            string unresolvedLocationsByLocoLogLines = "";
            if (unresolvedLocationsByLocoId.Count > 0)
            {
                foreach (var item in unresolvedLocationsByLocoId.Values)
                {
                    string locoId = item[0].LocomotiveId;
                    string locoIdent = item[0].Locomotive.Ident.ToString();
                    unresolvedLocationsByLocoLogLines += $"{item.Count} waypoints for {locoIdent} failed to load track locations.\n";
                    Loader.LogError($"Failed to resolve track locations on {item.Count} waypoints for locomotive car id {locoId} with ident {locoIdent}. {String.Join(",", item.Select(w => $"[{w.Id}]"))}");
                }
            }

            string unresolvedCoupleToCarsByLocoLogLines = "";
            if (unresolvedCoupleToCarIdsByLocoId.Count > 0)
            {
                foreach (var item in unresolvedCoupleToCarIdsByLocoId.Values)
                {
                    string locoId = item[0].LocomotiveId;
                    string locoIdent = item[0].Locomotive.Ident.ToString();
                    unresolvedCoupleToCarsByLocoLogLines += $"{item.Count} waypoints for {locoIdent} failed to load couple to car ids.\n";
                    Loader.LogError($"Failed to resolve couple to car ids on {item.Count} waypoints for locomotive car id {locoId} with ident {locoIdent}. {String.Join(",", item.Select(w => $"[{w.Id}]"))}");
                }
            }

            string unresolvedDestinationIdsLogLines = "";
            if (unresolvedDestinationIdsByLocoId.Count > 0)
            {
                foreach (var item in unresolvedDestinationIdsByLocoId.Values)
                {
                    string locoId = item[0].LocomotiveId;
                    string locoIdent = item[0].Locomotive.Ident.ToString();
                    unresolvedDestinationIdsLogLines += $"{item.Count} waypoints for {locoIdent} failed to load uncoupling by destination ids.\n";
                    Loader.LogError($"Failed to resolve uncoupling by destination ids on {item.Count} waypoints for locomotive car id {locoId} with ident {locoIdent}. {String.Join(",", item.Select(w => $"[{w.Id}]"))}");
                }
            }

            if (unresolvedLocomotiveIds.Count > 0 || unresolvedLocationsByLocoId.Count > 0 || unresolvedCoupleToCarIdsByLocoId.Count > 0 || unresolvedDestinationIdsByLocoId.Count > 0)
            {
                ModalAlertController.PresentOkay("Failed to load waypoints", $"Waypoint Queue ran into an issue while trying to load waypoint data." +
                    $"\n\n{unresolvedLocoIdsLogLine}{unresolvedLocationsByLocoLogLines}{unresolvedCoupleToCarsByLocoLogLines}{unresolvedDestinationIdsLogLines}" +
                    $"\nSometimes this may happen if any rolling stock or track mods were modified or removed in this save, or if you are loading an earlier version of a save with a mismatched waypoints.json file." +
                    $"\n\nWaypoint Queue should still work normally with this save game, though some waypoints may be missing.");
            }

            if (TrainController.Shared.SelectedLocomotive)
            {
                Loader.LogDebug($"Invoking LocoWaypointStateDidUpdate in LoadWaypointSaveState");
                LocoWaypointStateDidUpdate.Invoke(TrainController.Shared.SelectedLocomotive.id);
            }

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
