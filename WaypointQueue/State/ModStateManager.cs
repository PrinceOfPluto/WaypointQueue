using GalaSoft.MvvmLight.Messaging;
using Game.Events;
using Game.Messages;
using Game.State;
using HarmonyLib;
using KeyValue.Runtime;
using Model;
using Network;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using Track;
using UI.Common;
using UnityEngine;
using WaypointQueue.Services;
using WaypointQueue.State.Events;
using WaypointQueue.UUM;

namespace WaypointQueue.State
{
    [HarmonyPatch]
    internal class ModStateManager : MonoBehaviour
    {
        public static ModStateManager Shared { get; private set; }

        // Ideally state updates would be handled through custom IGameMessages, but any custom messages
        // are not included within the MessagePack union attributes on IGameMessage which means any messages
        // sent from client to host will appear as a null message to the host. Due to this, multiplayer mods
        // appear limited to using the PropertyChange message.

        // Mod data is separated into different key value objects so PropertyChanges can have better performance
        // (i.e. so a single route update can be sent using a single PropertyChange rather than sending the entire
        // route list which may be quite large)
        private WaypointModStorage _waypointModStorage;
        private QueueStateStorage _queueStateStorage;
        private RouteStorage _routeStorage;

        private Dictionary<string, LocoWaypointState> _locoWaypointStates = [];
        private Dictionary<string, RouteDefinition> _routes = [];
        private Dictionary<string, RouteAssignment> _routeAssignments = [];

        private readonly Dictionary<string, IDisposable> _queueObservers = [];
        private readonly Dictionary<string, IDisposable> _routeObservers = [];
        private readonly List<IDisposable> _observers = [];

        public IReadOnlyDictionary<string, LocoWaypointState> LocoWaypointStates => _queueStateStorage.GetAll();

        public IReadOnlyDictionary<string, RouteDefinition> Routes => _routes;

        public IReadOnlyDictionary<string, RouteAssignment> RouteAssignments => _routeAssignments;

        private WaypointResolver _waypointResolver;
        private RefuelService _refuelService;
        private AutoEngineerService _autoEngineerService;

        private void OnEnable()
        {
            Shared = this;
            Messenger.Default.Register<MapWillLoadEvent>(this, OnMapWillLoad);
            Messenger.Default.Register<MapDidUnloadEvent>(this, OnMapDidUnload);
            Messenger.Default.Register<PropertiesDidRestore>(this, OnPropertiesDidRestore);
            _waypointResolver = Loader.ServiceProvider.GetService<WaypointResolver>();
            _refuelService = Loader.ServiceProvider.GetService<RefuelService>();
            _autoEngineerService = Loader.ServiceProvider.GetService<AutoEngineerService>();
        }

        private void OnDisable()
        {
            Shared = null;
            Messenger.Default.Unregister(this);
        }

        private void OnMapWillLoad(MapWillLoadEvent @event)
        {
            PrepareStorageKeyValueObjects();
        }

        private void OnMapDidUnload(MapDidUnloadEvent mapDidUnloadEvent)
        {
            DestroyStorageKeyValueObjects();

            _queueObservers.Values.ToList().ForEach(o => o.Dispose());
            _queueObservers.Clear();

            _routeObservers.Values.ToList().ForEach(o => o.Dispose());
            _routeObservers.Clear();

            _observers.ForEach(o => o.Dispose());
            _observers.Clear();

            _locoWaypointStates.Clear();
            _routes.Clear();
            _routeAssignments.Clear();
        }

        private void PrepareStorageKeyValueObjects()
        {
            DestroyStorageKeyValueObjects();
            KeyValueObject modStorageKeyValueObject = base.gameObject.AddComponent<KeyValueObject>();
            _waypointModStorage = new WaypointModStorage(modStorageKeyValueObject);
            // Apparently a game object can only have one key value object,
            // so two other game objects are used for state update optimization
            KeyValueObject queueStateKeyValueObject = Loader.QueueStorageGO.AddComponent<KeyValueObject>();
            _queueStateStorage = new QueueStateStorage(queueStateKeyValueObject);
            KeyValueObject routeKeyValueObject = Loader.RouteStorageGO.AddComponent<KeyValueObject>();
            _routeStorage = new RouteStorage(routeKeyValueObject);
        }

        private void DestroyStorageKeyValueObjects()
        {
            _waypointModStorage?.Dispose();
            _waypointModStorage = null;
            _queueStateStorage?.Dispose();
            _queueStateStorage = null;
            _routeStorage?.Dispose();
            _routeStorage = null;
        }

        private void OnPropertiesDidRestore(PropertiesDidRestore evt)
        {
            _refuelService.RebuildCollections();

            using (StateManager.TransactionScope())
            {
                if (Multiplayer.IsHost)
                {
                    // If there is no loaded data at this point, check if old save data needs to be migrated
                    if (_queueStateStorage.Count == 0 && _routeStorage.Count == 0 && _waypointModStorage.RouteAssignments.Count == 0)
                    {
                        MigrateFromJsonSaveToStorage();
                    }
                }
                StorageToRuntime();

                RouteRegistry.LoadWaypointsForRoutes();
                HydrateLocoWaypointStates();
            }

            _observers.Add(_queueStateStorage.ObserveKeyChanges((string key, KeyChange keyChange) =>
            {
                if (keyChange == KeyChange.Add)
                {
                    OnQueueStorageKeyAdded(key);
                }
                else
                {
                    OnQueueStorageKeyRemoved(key);
                }
            }));

            _observers.Add(_routeStorage.ObserveKeyChanges((string key, KeyChange keyChange) =>
            {
                if (keyChange == KeyChange.Add)
                {
                    OnRouteStorageKeyAdded(key);
                }
                else
                {
                    OnRouteStorageKeyRemoved(key);
                }
            }));

            foreach (var locoId in _locoWaypointStates.Keys)
            {
                _queueObservers[locoId] = _queueStateStorage.ObserveQueueState(locoId, OnLocoQueueDidChange, false);
            }

            foreach (var routeId in _routes.Keys)
            {
                _routeObservers[routeId] = _routeStorage.ObserveRoute(routeId, OnRouteDidChange, false);
            }

            _observers.Add(_waypointModStorage.ObserveRouteAssignments(routeAssignments =>
            {
                _routeAssignments = routeAssignments;
            }, false));

            WaypointQueueController.Shared.RestartCoroutine();
        }

        private void StorageToRuntime()
        {
            Loader.LogDebug($"Populating runtime from storage data");
            _locoWaypointStates = new Dictionary<string, LocoWaypointState>(_queueStateStorage.GetAll());
            _routeAssignments = new Dictionary<string, RouteAssignment>(_waypointModStorage.RouteAssignments);
            _routes = new Dictionary<string, RouteDefinition>(_routeStorage.GetAll());
        }

        private void OnQueueStorageKeyAdded(string key)
        {
            Loader.LogDebug($"Queue added to storage for loco id: {key}");
            if (!_queueObservers.ContainsKey(key))
            {
                _queueObservers[key] = _queueStateStorage.ObserveQueueState(key, OnLocoQueueDidChange, false);
            }
        }

        private void OnQueueStorageKeyRemoved(string key)
        {
            Loader.LogDebug($"Queue removed from storage for loco id: {key}");
            if (Multiplayer.IsHost && _locoWaypointStates.TryGetValue(key, out LocoWaypointState oldState))
            {
                using (StateManager.TransactionScope())
                {
                    if (oldState.UnresolvedWaypoint != null)
                    {
                        _waypointResolver.CleanupBeforeRemovingWaypoint(oldState.UnresolvedWaypoint);
                    }
                    _autoEngineerService.CancelActiveOrders(oldState.Locomotive);
                }
            }

            _locoWaypointStates.Remove(key);

            if (_queueObservers.ContainsKey(key))
            {
                _queueObservers[key].Dispose();
                _queueObservers.Remove(key);
            }
            Messenger.Default.Send(new QueueDidUpdate(key));
        }

        private void OnLocoQueueDidChange(LocoWaypointState newState)
        {
            if (newState == null)
            {
                // State is already removed from storage
                return;
            }

            if (!_locoWaypointStates.TryGetValue(newState.LocomotiveId, out var oldState))
            {
                // No old state to handle
                _locoWaypointStates[newState.LocomotiveId] = newState;
                Messenger.Default.Send(new QueueDidUpdate(newState.LocomotiveId));
                return;
            }

            if (Multiplayer.IsHost)
            {
                using (StateManager.TransactionScope())
                {
                    HostHandleQueueDidChange(oldState, newState);
                }
            }

            // Determine whether only a single waypoint changed for the UI refresh optimization
            if (newState.Waypoints.Count == oldState.Waypoints.Count)
            {
                List<ManagedWaypoint> waypointsThatChanged = [];
                for (int i = 0; i < newState.Waypoints.Count; i++)
                {
                    ManagedWaypoint oldWaypoint = oldState.Waypoints[i];
                    ManagedWaypoint newWaypoint = newState.Waypoints[i];

                    if (oldWaypoint.Id == newWaypoint.Id && !oldWaypoint.Equals(newWaypoint))
                    {
                        waypointsThatChanged.Add(newWaypoint);
                    }

                    if (waypointsThatChanged.Count > 1)
                    {
                        break;
                    }
                }

                if (waypointsThatChanged.Count == 1)
                {
                    _locoWaypointStates[newState.LocomotiveId] = newState;
                    Messenger.Default.Send(new WaypointDidUpdate(waypointsThatChanged[0].Id, waypointsThatChanged[0].LocomotiveId, null));
                    return;
                }
            }

            // Determine if a single waypoint was added at the end
            if (newState.Waypoints.Count - oldState.Waypoints.Count == 1)
            {
                bool allMatch = true;
                for (int i = 0; i < oldState.Waypoints.Count; i++)
                {
                    if (newState.Waypoints[i].Id != oldState.Waypoints[i].Id)
                    {
                        allMatch = false;
                        break;
                    }
                }

                if (allMatch)
                {
                    ManagedWaypoint appendedWaypoint = newState.Waypoints.Last();
                    _locoWaypointStates[newState.LocomotiveId] = newState;
                    Messenger.Default.Send(new WaypointWasAppended(appendedWaypoint.Id, newState.LocomotiveId, null));
                    return;
                }
            }

            _locoWaypointStates[newState.LocomotiveId] = newState;
            Messenger.Default.Send(new QueueDidUpdate(newState.LocomotiveId));
        }

        private void HostHandleQueueDidChange(LocoWaypointState oldState, LocoWaypointState newState)
        {
            if (oldState.UnresolvedWaypoint == null)
            {
                // Don't need to handle anything here because the queue processor will handle it
                return;
            }

            // When all waypoints are deleted or if the first waypoint got deleted
            if (newState.Waypoints.Count == 0 || newState.UnresolvedWaypoint == null)
            {
                _waypointResolver.CleanupBeforeRemovingWaypoint(oldState.UnresolvedWaypoint);
                _autoEngineerService.CancelActiveOrders(oldState.Locomotive);
                return;
            }

            // When first waypoint changed
            if (newState.UnresolvedWaypoint != null && oldState.UnresolvedWaypoint.Id != newState.UnresolvedWaypoint.Id)
            {
                _waypointResolver.CleanupBeforeRemovingWaypoint(oldState.UnresolvedWaypoint);
                WaypointQueueController.Shared.SendToFirstWaypoint(newState);
                return;
            }

            // When first waypoint was adjusted
            (Location oldOrdersLocation, string oldOrdersCoupleToCarId) = _autoEngineerService.GetCurrentOrderWaypoint(oldState.Locomotive);

            if (oldOrdersLocation.IsValid)
            {
                string oldLocString = Graph.Shared.LocationToString(oldOrdersLocation);
                string newLocString = newState.UnresolvedWaypoint.LocationString;

                string oldCoupleId = oldOrdersCoupleToCarId ?? "";
                string newCoupleId = newState.UnresolvedWaypoint.CoupleToCarId ?? "";

                if (oldLocString != newLocString || oldCoupleId != newCoupleId)
                {
                    WaypointQueueController.Shared.SendToFirstWaypoint(newState);
                    return;
                }
            }
        }

        private void OnRouteStorageKeyAdded(string routeId)
        {
            Loader.LogDebug($"Route added to storage with id: {routeId}");
            RouteDefinition route = _routeStorage.GetByRouteId(routeId);
            if (route != null)
            {
                _routes[route.Id] = route;
                if (!_routeObservers.ContainsKey(routeId))
                {
                    _routeObservers[route.Id] = _routeStorage.ObserveRoute(route.Id, OnRouteDidChange, false);
                }
            }
        }

        private void OnRouteStorageKeyRemoved(string routeId)
        {
            Loader.LogDebug($"Route removed from storage with id: {routeId}");
            _routes.Remove(routeId);

            if (_routeObservers.ContainsKey(routeId))
            {
                _routeObservers[routeId]?.Dispose();
                _routeObservers.Remove(routeId);
            }

            List<string> assignmentsToRemove = [.. _routeAssignments.Where(ra => ra.Value.RouteId == routeId).Select(ra => ra.Key)];
            if (assignmentsToRemove.Count > 0)
            {
                assignmentsToRemove.ForEach(key => _routeAssignments.Remove(key));
                string json = JsonConvert.SerializeObject(_routeAssignments);
                StateManager.ApplyLocal(new PropertyChange(_waypointModStorage.ObjectId, _waypointModStorage.KeyRouteAssignments, new StringPropertyValue(json)));
            }

            Messenger.Default.Send(new RouteDidUpdate(routeId));
        }

        private void OnRouteDidChange(RouteDefinition route)
        {
            if (route == null)
            {
                Loader.LogDebug($"Route changed but new route def was null so the storage key should already be removed");
                return;
            }

            Loader.LogDebug($"Route changed for route id: {route.Id}");
            _routes[route.Id] = route;
            Messenger.Default.Send(new RouteDidUpdate(route.Id));
        }

        public LocoWaypointState GetLocoWaypointState(string locoId)
        {
            var state = _queueStateStorage.GetQueueByLocoId(locoId);
            return state ?? new LocoWaypointState(locoId);
        }

        public ManagedWaypoint GetWaypointById(string waypointId, string ownerId, bool forRoute)
        {
            if (forRoute)
            {
                var route = RouteRegistry.GetById(ownerId);
                if (route == null) { return null; }
                int index = route.Waypoints.FindIndex(w => w.Id == waypointId);
                return index < 0 ? null : route.Waypoints[index];
            }
            else
            {
                var state = _queueStateStorage.GetQueueByLocoId(ownerId);
                int index = state.Waypoints.FindIndex(w => w.Id == waypointId);
                return index < 0 ? null : state.Waypoints[index];
            }
        }

        public void SaveLocoWaypointState(string locoId, LocoWaypointState newState)
        {
            string json = JsonConvert.SerializeObject(newState);
            StateManager.ApplyLocal(new PropertyChange(_queueStateStorage.ObjectId, locoId, new StringPropertyValue(json)));
        }

        public void RemoveLocoWaypointState(string locoId)
        {
            if (_queueStateStorage.ContainsQueueForLocoId(locoId))
            {
                StateManager.ApplyLocal(new PropertyChange(_queueStateStorage.ObjectId, locoId, new NullPropertyValue()));
            }
        }

        public void SaveRoute(RouteDefinition routeDefinition)
        {
            routeDefinition.UpdatedAt = DateTime.Now;
            string json = JsonConvert.SerializeObject(routeDefinition);
            StateManager.ApplyLocal(new PropertyChange(_routeStorage.ObjectId, routeDefinition.Id, new StringPropertyValue(json)));
        }

        public void RemoveRoute(string routeId)
        {
            StateManager.ApplyLocal(new PropertyChange(_routeStorage.ObjectId, routeId, new NullPropertyValue()));
        }

        public void SaveRouteAssignment(RouteAssignment routeAssignment)
        {
            _routeAssignments[routeAssignment.LocoId] = routeAssignment;
            string json = JsonConvert.SerializeObject(_routeAssignments);
            StateManager.ApplyLocal(new PropertyChange(_waypointModStorage.ObjectId, _waypointModStorage.KeyRouteAssignments, new StringPropertyValue(json)));

        }

        public void RemoveRouteAssignment(string locoId)
        {
            _routeAssignments.Remove(locoId);
            string json = JsonConvert.SerializeObject(_routeAssignments);
            StateManager.ApplyLocal(new PropertyChange(_waypointModStorage.ObjectId, _waypointModStorage.KeyRouteAssignments, new StringPropertyValue(json)));
        }

        private void MigrateFromJsonSaveToStorage()
        {
            Loader.Log($"Migrating pre 1.6 save data from json files to property storage");

            foreach (var state in ModSaveManager.Shared.LoadLocoWaypointStatesFromSave())
            {
                _queueStateStorage.SetLocoQueue(state);
            }

            foreach (var route in ModSaveManager.Shared.LoadRoutesFromSave())
            {
                _routeStorage.SetRoute(route);
            }

            Dictionary<string, RouteAssignment> routeAssignmentLookup = [];
            foreach (var routeAssignment in ModSaveManager.Shared.LoadRouteAssignmentsFromSave())
            {
                routeAssignmentLookup.Add(routeAssignment.LocoId, routeAssignment);
            }
            _waypointModStorage.RouteAssignments = routeAssignmentLookup;
        }

        private void HydrateLocoWaypointStates()
        {
            List<string> unresolvedLocomotiveIds = [];
            Dictionary<string, List<ManagedWaypoint>> unresolvedLocationsByLocoId = [];
            Dictionary<string, List<ManagedWaypoint>> unresolvedCoupleToCarIdsByLocoId = [];
            Dictionary<string, List<ManagedWaypoint>> unresolvedDestinationIdsByLocoId = [];

            List<LocoWaypointState> validStates = [];
            foreach (var state in _locoWaypointStates.Values)
            {
                Loader.LogDebug($"Loading waypoint state for locomotive id {state.LocomotiveId}");

                if (state.Locomotive == null)
                {
                    unresolvedLocomotiveIds.Add(state.LocomotiveId);
                    break;
                }

                List<ManagedWaypoint> validWaypoints = [];
                foreach (var waypoint in state.Waypoints)
                {
                    Loader.LogDebug($"Loading waypoint {waypoint.Id}");
                    if (!waypoint.TryResolveLocomotive(out Car loco) && !unresolvedLocomotiveIds.Contains(waypoint.LocomotiveId))
                    {
                        unresolvedLocomotiveIds.Add(waypoint.LocomotiveId);
                        break;
                    }

                    if (!waypoint.TryResolveLocation(out Track.Location loc))
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
                state.Waypoints = validWaypoints;
                validStates.Add(state);

                if (state.UnresolvedWaypoint != null)
                {
                    Loader.LogDebug($"Loading unresolved waypoint {state.UnresolvedWaypoint.Id}");
                    if (!state.UnresolvedWaypoint.IsValidWithLoco())
                    {
                        Loader.LogError($"Failed to hydrate unresolved waypoint {state.UnresolvedWaypoint?.Id}");
                    }
                }
            }

            foreach (var state in validStates)
            {
                Loader.LogDebug($"Saving loco queue state after hydrating waypoints for loco id: {state.LocomotiveId}");
                SaveLocoWaypointState(state.LocomotiveId, state);
            }

            LogHydrateWaypointFailures(unresolvedLocomotiveIds, unresolvedLocationsByLocoId, unresolvedCoupleToCarIdsByLocoId, unresolvedDestinationIdsByLocoId);
        }

        private void LogHydrateWaypointFailures(List<string> unresolvedLocomotiveIds,
        Dictionary<string, List<ManagedWaypoint>> unresolvedLocationsByLocoId,
        Dictionary<string, List<ManagedWaypoint>> unresolvedCoupleToCarIdsByLocoId,
        Dictionary<string, List<ManagedWaypoint>> unresolvedDestinationIdsByLocoId)
        {
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

        }
    }
}
