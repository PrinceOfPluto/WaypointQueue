using GalaSoft.MvvmLight.Messaging;
using Game.Events;
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
using UI.Common;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.UUM;
using static WaypointQueue.ModSaveManager;

namespace WaypointQueue
{
    public class WaypointQueueController : MonoBehaviour
    {
        public static event Action<string> LocoWaypointStateDidUpdate;
        public static event Action<ManagedWaypoint> WaypointDidUpdate;

        private Coroutine _coroutine;

        public Dictionary<string, LocoWaypointState> WaypointStateMap { get; private set; } = new();

        public List<CarLoadTargetLoader> CarLoadTargetLoaders { get; private set; } = new List<CarLoadTargetLoader>();

        public List<CarLoaderSequencer> CarLoaderSequencers { get; private set; } = new List<CarLoaderSequencer>();

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
        }

        private void OnMapWillUnload(MapWillUnloadEvent @event)
        {
            if (_coroutine != null)
            {
                Loader.LogDebug($"OnMapWillUnload stopping coroutine in WaypointQueueController OnMapWillUnload");
                StopCoroutine(_coroutine);
                WaypointStateMap.Clear();
            }
        }

        private IEnumerator Ticker()
        {
            WaitForSeconds t = new WaitForSeconds(WaypointTickInterval);
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
                Loader.LogError(e.Message);
                string errorModalTitle = "Waypoint Queue Error";
                string errorModalMessage = $"Waypoint Queue encountered an unexpected error while handling game tick updates.";
                Loader.ShowErrorModal(errorModalTitle, errorModalMessage);
                StopCoroutine(_coroutine);
                throw;
            }
        }

        public void InitCarLoaders(bool reload = false)
        {
            if (reload || CarLoadTargetLoaders == null || CarLoadTargetLoaders.Count <= 0)
            {
                Loader.LogDebug($"Initializing list of car load target loaders");
                CarLoadTargetLoaders = FindObjectsOfType<CarLoadTargetLoader>().ToList();
            }
            if (reload || CarLoaderSequencers == null || CarLoaderSequencers.Count <= 0)
            {
                CarLoaderSequencers = FindObjectsOfType<CarLoaderSequencer>().ToList();
            }
        }

        private void DoQueueTickUpdate()
        {
            if (WaypointStateMap == null)
            {
                WaypointStateMap = [];
            }
            HandleLoopingRoutes();

            List<LocoWaypointState> listForRemoval = new List<LocoWaypointState>();

            foreach (LocoWaypointState entry in WaypointStateMap.Values)
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
                    if (!WaypointResolver.TryHandleUnresolvedWaypoint(entry.UnresolvedWaypoint, ordersHelper, WaypointDidUpdate))
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
                    Loader.Log($"Marking {entry.Locomotive.Ident} waypoint queue for removal");
                    listForRemoval.Add(entry);
                }
            }

            // Update list of states
            foreach (var entry in listForRemoval)
            {
                WaypointStateMap.Remove(entry.LocomotiveId);
            }
        }

        public void AddWaypoint(Car loco, Location location, string coupleToCarId, bool isReplacing, bool isInsertingNext)
        {
            bool isCoupling = coupleToCarId != null && coupleToCarId.Length > 0;
            string couplingLogSegment = isCoupling ? $"coupling to ${coupleToCarId}" : "no coupling";
            string actionName = "add";
            if (isReplacing) actionName = "replace";
            if (isInsertingNext) actionName = "insert next";
            Loader.Log($"Trying to {actionName} waypoint for loco {loco.Ident} to {location} with {couplingLogSegment}");

            LocoWaypointState entry = GetOrAddLocoWaypointState(loco);

            ManagedWaypoint waypoint = new ManagedWaypoint(loco, location, coupleToCarId);
            WaypointResolver.CheckNearbyFuelLoaders(waypoint);

            if (isReplacing && entry.Waypoints.Count > 0)
            {
                if (entry.Waypoints[0].Id == entry.UnresolvedWaypoint.Id)
                {
                    WaypointResolver.CleanupBeforeRemovingWaypoint(entry.UnresolvedWaypoint);
                    entry.UnresolvedWaypoint = waypoint;
                }
                entry.Waypoints[0] = waypoint;
                RefreshCurrentWaypoint(loco, GetOrdersHelper(loco));
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
            OnWaypointWasAdded(loco.id);
        }

        private void HandleLoopingRoutes()
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

        public void ClearWaypointState(Car loco)
        {
            Loader.Log($"Trying to clear waypoint state for {loco.Ident}");

            if (WaypointStateMap.TryGetValue(loco.id, out LocoWaypointState entry))
            {
                if (entry.UnresolvedWaypoint != null)
                {
                    WaypointResolver.CleanupBeforeRemovingWaypoint(entry.UnresolvedWaypoint);
                }

                WaypointStateMap.Remove(loco.id);
                Loader.Log($"Removed waypoint state entry for {loco.Ident}");
            }
            CancelActiveOrders(loco);
            Loader.LogDebug($"Invoking LocoWaypointStateDidUpdate in ClearWaypointState");
            LocoWaypointStateDidUpdate.Invoke(loco.id);
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

            if (WaypointStateMap.TryGetValue(waypoint.Locomotive.id, out LocoWaypointState entry))
            {
                string waypointId = waypoint.Id;

                WaypointResolver.CleanupBeforeRemovingWaypoint(waypoint);

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
                    WaypointResolver.CleanupBeforeRemovingWaypoint(state.UnresolvedWaypoint);
                    Loader.LogDebug($"Resetting unresolved waypoint after reordering waypoint list");
                    state.UnresolvedWaypoint = waypoint;
                    SendToWaypointFromQueue(waypoint, GetOrdersHelper(waypoint.Locomotive));
                }

                Loader.LogDebug($"Invoking LocoWaypointStateDidUpdate in ReorderWaypoint");
                LocoWaypointStateDidUpdate.Invoke(waypoint.LocomotiveId);
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

        public List<ManagedWaypoint> GetWaypointList(Car loco)
        {
            WaypointStateMap.TryGetValue(loco.id, out LocoWaypointState state);
            return state?.Waypoints ?? [];
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
            if (!WaypointStateMap.TryGetValue(loco.id, out LocoWaypointState state))
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
            waypoint.StatusLabel = "Running to waypoint";
            UpdateWaypoint(waypoint);
            SendToWaypoint(ordersHelper, waypoint.Location, waypoint.CoupleToCarId);
        }

        internal void SendToWaypoint(AutoEngineerOrdersHelper ordersHelper, Location location, string coupleToCarId = null)
        {
            (Location, string)? maybeWaypoint = (location, coupleToCarId);
            ordersHelper.SetOrdersValue(null, null, null, null, maybeWaypoint);
        }

        internal void LoadWaypointSaveState(WaypointSaveState saveState)
        {
            WaypointStateMap.Clear();

            List<string> unresolvedLocomotiveIds = [];
            Dictionary<string, List<ManagedWaypoint>> unresolvedLocationsByLocoId = [];
            Dictionary<string, List<ManagedWaypoint>> unresolvedCoupleToCarIdsByLocoId = [];

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
                    waypoint.TryResolveCouplingSearchText(out Car _);

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

            if (unresolvedLocomotiveIds.Count > 0 || unresolvedLocationsByLocoId.Count > 0 || unresolvedCoupleToCarIdsByLocoId.Count > 0)
            {
                ModalAlertController.PresentOkay("Failed to load waypoints", $"Waypoint Queue ran into an issue while trying to load waypoint data." +
                    $"\n\n{unresolvedLocoIdsLogLine}{unresolvedLocationsByLocoLogLines}{unresolvedCoupleToCarsByLocoLogLines}" +
                    $"\nSometimes this may happen if any rolling stock or track mods were modified or removed in this save, or if you are loading an earlier version of a save with a mismatched waypoints.json file." +
                    $"\n\nWaypoint Queue should still work normally with this save game, though some waypoints may be missing.");
            }

            if (TrainController.Shared.SelectedLocomotive)
            {
                Loader.LogDebug($"Invoking LocoWaypointStateDidUpdate in LoadWaypointSaveState");
                LocoWaypointStateDidUpdate.Invoke(TrainController.Shared.SelectedLocomotive.id);
            }

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
