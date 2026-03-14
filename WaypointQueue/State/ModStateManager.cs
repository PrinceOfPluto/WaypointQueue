using GalaSoft.MvvmLight.Messaging;
using Game.Events;
using Game.Messages;
using Game.State;
using KeyValue.Runtime;
using Model;
using Network;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using UI.Common;
using UnityEngine;
using WaypointQueue.Services;
using WaypointQueue.UI;
using WaypointQueue.UUM;

namespace WaypointQueue.State
{
    internal class ModStateManager : MonoBehaviour
    {
        public static ModStateManager Shared { get; private set; }

        private WaypointModStorage _waypointModStorage;

        private readonly HashSet<IDisposable> _observers = [];

        private readonly Dictionary<string, LocoWaypointState> _waypointStateByLocoId = [];
        private readonly Dictionary<string, IDisposable> _observersByLocoId = [];
        private readonly Dictionary<string, IDisposable> _keyObserversByLocoId = [];
        private readonly HashSet<string> _activeQueueLocoIds = [];

        public IReadOnlyCollection<string> LocoIdsWithActiveWaypointQueues => _waypointStateByLocoId.Keys;
        public IReadOnlyCollection<LocoWaypointState> ActiveWaypointQueues => _waypointStateByLocoId.Values;

        private readonly List<RouteDefinition> _routes;
        private readonly List<RouteAssignment> _routeAssignments;

        private WaypointResolver _waypointResolver;
        private RefuelService _refuelService;
        private ICarService _carService;
        private AutoEngineerService _autoEngineerService;

        public readonly string KeyLocoWaypointQueueState = "waypointqueuemod.state";

        private void OnEnable()
        {
            Shared = this;
            Messenger.Default.Register<MapWillLoadEvent>(this, OnMapWillLoad);
            Messenger.Default.Register<MapDidLoadEvent>(this, OnMapDidLoad);
            Messenger.Default.Register<MapWillUnloadEvent>(this, OnMapWillUnload);
            Messenger.Default.Register<MapDidUnloadEvent>(this, OnMapDidUnload);
            Messenger.Default.Register<PropertiesDidRestore>(this, OnPropertiesDidRestore);
            _waypointResolver = Loader.ServiceProvider.GetService<WaypointResolver>();
            _refuelService = Loader.ServiceProvider.GetService<RefuelService>();
            _carService = Loader.ServiceProvider.GetService<ICarService>();
            _autoEngineerService = Loader.ServiceProvider.GetService<AutoEngineerService>();
        }

        private void OnDisable()
        {
            Shared = null;
            Messenger.Default.Unregister(this);
        }

        private void OnMapWillLoad(MapWillLoadEvent @event)
        {
            PrepareWaypointModKeyValueObject();
        }

        private void OnMapDidLoad(MapDidLoadEvent @event)
        {
            Loader.LogDebug($"ModStateManager: Map loaded.");
        }

        private void OnMapWillUnload(MapWillUnloadEvent mapWillUnloadEvent)
        {
            Loader.LogDebug($"ModStateManager: Map will unload.");
            foreach (IDisposable observer in _observers)
            {
                observer.Dispose();
            }

            foreach (IDisposable observer in _observersByLocoId.Values)
            {
                observer.Dispose();
            }

            _observers.Clear();
            _observersByLocoId.Clear();
            _waypointStateByLocoId.Clear();
        }

        private void OnMapDidUnload(MapDidUnloadEvent mapDidUnloadEvent)
        {
            DestroyWaypointModKeyValueObject();
        }

        private void OnPropertiesDidRestore(PropertiesDidRestore evt)
        {
            _refuelService.RebuildCollections();

            foreach (BaseLocomotive loco in TrainController.Shared.Cars.Where(c => c is BaseLocomotive).Cast<BaseLocomotive>())
            {
                RegisterObserversForLoco(loco);
            }

            _observers.Add(_waypointModStorage.ObserveRoutes(delegate (List<RouteDefinition> routes)
            {
                Loader.Log($"Observed Routes update");
                RouteRegistry.ReplaceAll(routes);
            }, initial: true));

            _observers.Add(_waypointModStorage.ObserveRouteAssignments(delegate (Dictionary<string, RouteAssignment> routeAssignments)
            {
                Loader.Log($"Observed Route Assignment update");
                RouteAssignmentRegistry.ReplaceAll(routeAssignments.Values);
            }, initial: true));

            using (StateManager.TransactionScope())
            {
                if (Multiplayer.IsHost)
                {
                    // If there is no loaded data at this point, check if old save data needs to be migrated
                    if (_waypointStateByLocoId.Count == 0 && RouteAssignmentRegistry.All().Count == 0 && RouteRegistry.Routes.Count == 0)
                    {
                        MigrateSaveState();
                    }
                }

                RouteRegistry.LoadWaypointsForRoutes();
                HydrateLocoWaypointStates();
            }

            WaypointQueueController.Shared.RestartCoroutine();
        }

        internal void RegisterObserversForLoco(BaseLocomotive loco, bool initial = false)
        {
            // Observe when a queue gets added or removed from a locomotive
            _keyObserversByLocoId.Add(loco.id, loco.KeyValueObject.ObserveKeyChanges((string key, KeyChange keyChange) =>
            {
                if (key == _waypointModStorage.KeyLocoWaypointState && keyChange == KeyChange.Add)
                {
                    if (keyChange == KeyChange.Add)
                    {
                        // When a waypoint queue gets added to this loco, start observing
                        _observersByLocoId[loco.id] = loco.KeyValueObject.Observe(_waypointModStorage.KeyLocoWaypointState, OnLocoWaypointStatePropertyChange(loco), callInitial: true);
                    }
                    else
                    {
                        HandleWaypointStateDelete(loco.id);
                    }
                }
            }));

            bool locoHasWaypointQueue = loco.KeyValueObject.Keys.Contains(_waypointModStorage.KeyLocoWaypointState) && !loco.KeyValueObject[_waypointModStorage.KeyLocoWaypointState].IsNull;
            if (locoHasWaypointQueue)
            {
                _observersByLocoId[loco.id] = loco.KeyValueObject.Observe(_waypointModStorage.KeyLocoWaypointState, OnLocoWaypointStatePropertyChange(loco), callInitial: true);
            }
        }

        private Action<Value> OnLocoWaypointStatePropertyChange(BaseLocomotive loco)
        {
            void handler(Value value)
            {
                Loader.LogDebug($"Observed update to waypoint state of {loco.Ident} with id {loco.id}");
                LocoWaypointState newState = JsonConvert.DeserializeObject<LocoWaypointState>(value);
                if (_waypointStateByLocoId.ContainsKey(loco.id))
                {
                    LocoWaypointState oldState = _waypointStateByLocoId[loco.id];
                    HandleWaypointStateChange(oldState, newState);
                }
                _waypointStateByLocoId[loco.id] = newState;
                WaypointWindow.Shared.OnLocoWaypointStateDidUpdate(loco.id);
            }
            return handler;
        }

        private void HandleWaypointStateDelete(string locoId)
        {
            if (Multiplayer.IsHost && _waypointStateByLocoId.TryGetValue(locoId, out LocoWaypointState oldState))
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

            _waypointStateByLocoId.Remove(locoId);
            _observersByLocoId[locoId].Dispose();
            _observersByLocoId.Remove(locoId);
        }

        private void HandleWaypointStateChange(LocoWaypointState oldState, LocoWaypointState newState)
        {
            if (!Multiplayer.IsHost)
            {
                return;
            }

            using (StateManager.TransactionScope())
            {
                if (newState.Waypoints.Count > 0 && oldState.UnresolvedWaypoint != null && oldState.UnresolvedWaypoint.Id != newState.Waypoints[0].Id)
                {
                    _waypointResolver.CleanupBeforeRemovingWaypoint(oldState.UnresolvedWaypoint);
                    WaypointQueueController.Shared.SendToWaypointFromQueue(newState.Waypoints[0], _autoEngineerService.GetOrdersHelper(newState.Locomotive));
                }
            }
        }

        private void PrepareWaypointModKeyValueObject()
        {
            DestroyWaypointModKeyValueObject();
            KeyValueObject keyValueObject = base.gameObject.AddComponent<KeyValueObject>();
            _waypointModStorage = new WaypointModStorage(keyValueObject);
        }

        private void DestroyWaypointModKeyValueObject()
        {
            _waypointModStorage?.Dispose();
            _waypointModStorage = null;
        }

        public LocoWaypointState AddLocoWaypointState(BaseLocomotive loco)
        {
            var state = new LocoWaypointState(loco);
            string json = JsonConvert.SerializeObject(state);
            StateManager.ApplyLocal(new PropertyChange(_waypointModStorage.ObjectId, _waypointModStorage.KeyActiveQueueLocoIds, new StringPropertyValue(json)));
            return state;
        }

        public LocoWaypointState GetLocoWaypointState(BaseLocomotive loco)
        {
            if (_waypointStateByLocoId.TryGetValue(loco.id, out var state)) return state;

            if (loco.KeyValueObject.Keys.Contains(_waypointModStorage.KeyLocoWaypointState))
            {
                string json = loco.KeyValueObject[_waypointModStorage.KeyLocoWaypointState].ToString();
                return JsonConvert.DeserializeObject<LocoWaypointState>(json);
            }
            return AddLocoWaypointState(loco);
        }

        public void SaveLocoWaypointState(BaseLocomotive loco, LocoWaypointState newState)
        {
            string json = JsonConvert.SerializeObject(newState);
            StateManager.ApplyLocal(new PropertyChange(loco.id, _waypointModStorage.KeyLocoWaypointState, new StringPropertyValue(json)));
        }

        public void RemoveLocoWaypointState(string locoId)
        {
            StateManager.ApplyLocal(new PropertyChange(locoId, _waypointModStorage.KeyLocoWaypointState, null));
        }

        private void MigrateSaveState()
        {
            if (ModSaveManager.TryLoadLocoWaypointStatesFromSave(out List<LocoWaypointState> loadedStates))
            {
                foreach (var state in loadedStates)
                {
                    if (TrainController.Shared.TryGetCarForId(state.LocomotiveId, out Car loco) && loco is BaseLocomotive)
                    {
                        SaveLocoWaypointState((BaseLocomotive)loco, state);
                    }
                }
            }
            ModSaveManager.LoadRoutesFromSave();
            ModSaveManager.LoadRouteAssignmentsFromSave();
        }

        private void HydrateLocoWaypointStates()
        {
            List<string> unresolvedLocomotiveIds = [];
            Dictionary<string, List<ManagedWaypoint>> unresolvedLocationsByLocoId = [];
            Dictionary<string, List<ManagedWaypoint>> unresolvedCoupleToCarIdsByLocoId = [];
            Dictionary<string, List<ManagedWaypoint>> unresolvedDestinationIdsByLocoId = [];

            foreach (var state in _waypointStateByLocoId.Values)
            {
                Loader.LogDebug($"Loading waypoint state for locomotive id {state.LocomotiveId}");

                if (!state.TryResolveLocomotive(out Car loco))
                {
                    unresolvedLocomotiveIds.Add(state.LocomotiveId);
                    break;
                }

                List<ManagedWaypoint> validWaypoints = [];
                foreach (var waypoint in state.Waypoints)
                {
                    Loader.LogDebug($"Loading waypoint {waypoint.Id}");
                    if (!waypoint.TryResolveLocomotive(out loco) && !unresolvedLocomotiveIds.Contains(waypoint.LocomotiveId))
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
                string json = JsonConvert.SerializeObject(state);
                StateManager.ApplyLocal(new PropertyChange(_waypointModStorage.ObjectId, _waypointModStorage.KeyActiveQueueLocoIds, new StringPropertyValue(json)));

                if (state.UnresolvedWaypoint != null)
                {
                    Loader.LogDebug($"Loading unresolved waypoint {state.UnresolvedWaypoint.Id}");
                    if (!state.UnresolvedWaypoint.IsValidWithLoco())
                    {
                        Loader.LogError($"Failed to hydrate unresolved waypoint {state.UnresolvedWaypoint?.Id}");
                    }
                }
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
