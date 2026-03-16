using GalaSoft.MvvmLight.Messaging;
using Game;
using Game.Events;
using Game.Messages;
using Game.State;
using HarmonyLib;
using KeyValue.Runtime;
using Model;
using Network;
using System;
using System.Collections.Generic;
using System.Linq;
using UI.Common;
using UnityEngine;
using WaypointQueue.Services;
using WaypointQueue.State.Events;
using WaypointQueue.State.Messages;
using WaypointQueue.UUM;

namespace WaypointQueue.State
{
    [HarmonyPatch]
    internal class ModStateManager : MonoBehaviour
    {
        public static ModStateManager Shared { get; private set; }

        private WaypointModStorage _waypointModStorage;

        private readonly Dictionary<string, LocoWaypointState> _locoWaypointStates = [];
        private readonly Dictionary<string, RouteDefinition> _routes = [];
        private readonly Dictionary<string, RouteAssignment> _routeAssignments = [];

        public IReadOnlyDictionary<string, LocoWaypointState> LocoWaypointStates => _locoWaypointStates;

        public IReadOnlyDictionary<string, RouteDefinition> Routes => _routes;

        public IReadOnlyDictionary<string, RouteAssignment> RouteAssignments => _routeAssignments;

        private WaypointResolver _waypointResolver;
        private RefuelService _refuelService;
        private AutoEngineerService _autoEngineerService;

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
        }

        private void OnMapDidUnload(MapDidUnloadEvent mapDidUnloadEvent)
        {
            DestroyWaypointModKeyValueObject();
        }

        private void OnPropertiesDidRestore(PropertiesDidRestore evt)
        {
            _refuelService.RebuildCollections();

            using (StateManager.TransactionScope())
            {
                if (Multiplayer.IsHost)
                {
                    // If there is no loaded data at this point, check if old save data needs to be migrated
                    if (_waypointModStorage.LocoWaypointStates.Count == 0 && _waypointModStorage.Routes.Count == 0 && _waypointModStorage.RouteAssignments.Count == 0)
                    {
                        MigrateSaveDataFromJson();
                        RuntimeToStorage();
                    }
                }
                StorageToRuntime();

                RouteRegistry.LoadWaypointsForRoutes();
                HydrateLocoWaypointStates();
            }

            WaypointQueueController.Shared.RestartCoroutine();
        }

        [HarmonyPatch(typeof(StateManager), nameof(StateManager.PopulateSnapshotForSave))]
        [HarmonyPrefix]
        static bool PrefixPopulateSnapshotForSave()
        {
            // Write runtime objects to properties for save
            Shared.RuntimeToStorage();
            return true;
        }

        private void StorageToRuntime()
        {
            Loader.Log($"Loading storage data for runtime");
            _locoWaypointStates.Clear();
            foreach (var state in _waypointModStorage.LocoWaypointStates.Values)
            {
                _locoWaypointStates.Add(state.LocomotiveId, state);
            }

            _routeAssignments.Clear();
            foreach (var routeAssignment in _waypointModStorage.RouteAssignments.Values)
            {
                _routeAssignments.Add(routeAssignment.LocoId, routeAssignment);
            }

            _routes.Clear();
            foreach (var route in _waypointModStorage.Routes)
            {
                _routes.Add(route.Id, route);
            }
        }

        private void RuntimeToStorage()
        {
            Loader.Log($"Writing runtime data to storage");
            _waypointModStorage.Version = "1";
            _waypointModStorage.LocoWaypointStates = _locoWaypointStates;
            _waypointModStorage.Routes = [.. _routes.Values];
            _waypointModStorage.RouteAssignments = _routeAssignments;
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

        // Handling custom IGameMessages rather than operating through PropertyChanges for better state update performance
        [HarmonyPatch(typeof(StateManager), nameof(StateManager.Handle), [typeof(IGameMessage), typeof(IPlayer)])]
        [HarmonyPrefix]
        static bool PrefixPatchHandle(IGameMessage gameMessage, IPlayer sender)
        {
            if (sender == null)
            {
                throw new ArgumentException("null sender", "sender");
            }
            if (gameMessage is UpdateWaypointForQueueMessage updateWaypointForQueueMessage)
            {
                Shared.HandleUpdateWaypointForQueueMessage(updateWaypointForQueueMessage);
                return false;
            }
            if (gameMessage is UpdateWaypointForRouteMessage updateWaypointForRouteMessage)
            {
                Shared.HandleUpdateWaypointForRouteMessage(updateWaypointForRouteMessage);
                return false;
            }
            if (gameMessage is UpdateLocoQueueMessage updateLocoQueueMessage)
            {
                Shared.HandleUpdateLocoQueue(updateLocoQueueMessage);
                return false;
            }
            if (gameMessage is RemoveLocoQueueMessage removeLocoQueueMessage)
            {
                Shared.HandleRemoveLocoQueue(removeLocoQueueMessage);
                return false;
            }
            if (gameMessage is UpdateRouteMessage updateRouteMessage)
            {
                Shared.HandleUpdateRoute(updateRouteMessage);
                return false;
            }
            if (gameMessage is RemoveRouteMessage removeRouteMessage)
            {
                Shared.HandleRemoveRoute(removeRouteMessage);
                return false;
            }
            if (gameMessage is UpdateRouteAssignmentMessage updateRouteAssignmentMessage)
            {
                Shared.HandleUpdateRouteAssignment(updateRouteAssignmentMessage);
                return false;
            }
            if (gameMessage is RemoveRouteAssignmentMessage removeRouteAssignmentMessage)
            {
                Shared.HandleRemoveRouteAssignment(removeRouteAssignmentMessage);
                return false;
            }
            return true;
        }

        private void HandleUpdateWaypointForRouteMessage(UpdateWaypointForRouteMessage message)
        {
            if (_routes.TryGetValue(message.RouteId, out RouteDefinition route))
            {
                int index = route.Waypoints.FindIndex(w => w.Id == message.Waypoint.Id);

                if (index >= 0)
                {
                    route.Waypoints[index] = message.Waypoint;
                }
            }
        }

        private void HandleUpdateWaypointForQueueMessage(UpdateWaypointForQueueMessage message)
        {
            Loader.LogDebug($"Handling update waypoint message");
            ManagedWaypoint updatedWaypoint = message.Waypoint;

            if (!_locoWaypointStates.TryGetValue(updatedWaypoint.LocomotiveId, out LocoWaypointState state))
            {
                Loader.LogError($"No existing LocoWaypointState found to handle update waypoint for loco id {updatedWaypoint.LocomotiveId} ");
            }

            int index = state.Waypoints.FindIndex(w => w.Id == updatedWaypoint.Id);

            if (index >= 0)
            {
                state.Waypoints[index] = updatedWaypoint;

                if (updatedWaypoint.Id == state.UnresolvedWaypoint.Id)
                {
                    Loader.LogDebug($"Updated unresolved waypoint");
                    state.UnresolvedWaypoint = updatedWaypoint;

                    (Track.Location? currentOrdersLocation, string currentOrdersCoupleToCarId) = _autoEngineerService.GetCurrentOrderWaypoint(updatedWaypoint.Locomotive);

                    if (currentOrdersLocation != updatedWaypoint.Location || currentOrdersCoupleToCarId != updatedWaypoint.CoupleToCarId)
                    {
                        updatedWaypoint.StatusLabel = "Running to waypoint";
                        if (Multiplayer.IsHost)
                        {
                            var ordersHelper = _autoEngineerService.GetOrdersHelper(updatedWaypoint.Locomotive);
                            _autoEngineerService.SendToWaypoint(ordersHelper, updatedWaypoint.Location, updatedWaypoint.CoupleToCarId);
                        }
                    }
                }

                Loader.LogDebug($"Invoking WaypointDidUpdate in UpdateWaypoint");
                Messenger.Default.Send(new WaypointDidUpdate(updatedWaypoint.Id, updatedWaypoint.LocomotiveId));
            }
            else
            {
                Loader.LogError($"Failed to find waypoint for update by id {updatedWaypoint.Id}");
            }
        }

        private void HandleUpdateRoute(UpdateRouteMessage updateRouteMessage)
        {
            _routes[updateRouteMessage.RouteId] = updateRouteMessage.RouteDefinition;
            Messenger.Default.Send(new RouteDidUpdate(updateRouteMessage.RouteId));
        }

        private void HandleRemoveRoute(RemoveRouteMessage removeRouteMessage)
        {
            _routes.Remove(removeRouteMessage.RouteId);
            List<string> keysToRemove = [];
            foreach (var entry in _routeAssignments)
            {
                if (entry.Value.RouteId == removeRouteMessage.RouteId)
                {
                    keysToRemove.Add(entry.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                _routeAssignments.Remove(key);
            }
            Messenger.Default.Send(new RouteDidUpdate(removeRouteMessage.RouteId));
        }

        private void HandleUpdateRouteAssignment(UpdateRouteAssignmentMessage updateRouteAssignmentMessage)
        {
            _routeAssignments[updateRouteAssignmentMessage.RouteAssignment.LocoId] = updateRouteAssignmentMessage.RouteAssignment;
        }

        private void HandleRemoveRouteAssignment(RemoveRouteAssignmentMessage removeRouteAssignmentMessage)
        {
            _routeAssignments.Remove(removeRouteAssignmentMessage.LocomotiveId);
        }

        private void HandleUpdateLocoQueue(UpdateLocoQueueMessage updateLocoQueueMessage)
        {
            LocoWaypointState newState = updateLocoQueueMessage.State;

            if (Multiplayer.IsHost && _locoWaypointStates.TryGetValue(updateLocoQueueMessage.LocomotiveId, out LocoWaypointState oldState))
            {
                using (StateManager.TransactionScope())
                {
                    // when all waypoints are deleted
                    if (newState.Waypoints.Count == 0 && oldState.UnresolvedWaypoint != null)
                    {
                        _waypointResolver.CleanupBeforeRemovingWaypoint(oldState.UnresolvedWaypoint);

                    }
                    // when the first waypoint changed
                    else if (newState.Waypoints.Count > 0 && oldState.UnresolvedWaypoint != null && oldState.UnresolvedWaypoint.Id != newState.Waypoints[0].Id)
                    {
                        _waypointResolver.CleanupBeforeRemovingWaypoint(oldState.UnresolvedWaypoint);
                        WaypointQueueController.Shared.SendToFirstWaypoint(newState);
                    }
                }
            }

            _locoWaypointStates[updateLocoQueueMessage.LocomotiveId] = newState;
            Messenger.Default.Send(new QueueDidUpdate(updateLocoQueueMessage.LocomotiveId));
        }

        private void HandleRemoveLocoQueue(RemoveLocoQueueMessage updateLocoQueueMessage)
        {
            if (Multiplayer.IsHost && _locoWaypointStates.TryGetValue(updateLocoQueueMessage.LocomotiveId, out LocoWaypointState oldState))
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

            _locoWaypointStates.Remove(updateLocoQueueMessage.LocomotiveId);
            Messenger.Default.Send(new QueueDidUpdate(updateLocoQueueMessage.LocomotiveId));
        }

        public LocoWaypointState AddLocoWaypointState(string locoId)
        {
            var state = new LocoWaypointState(locoId);
            StateManager.ApplyLocal(new UpdateLocoQueueMessage(locoId, state));
            return state;
        }

        public LocoWaypointState GetLocoWaypointState(string locoId)
        {
            if (_locoWaypointStates.TryGetValue(locoId, out var state)) return state;

            return AddLocoWaypointState(locoId);
        }

        public void SaveLocoWaypointState(string locoId, LocoWaypointState newState)
        {
            StateManager.ApplyLocal(new UpdateLocoQueueMessage(locoId, newState));
        }

        public void RemoveLocoWaypointState(string locoId)
        {
            StateManager.ApplyLocal(new RemoveLocoQueueMessage(locoId));
        }

        private void MigrateSaveDataFromJson()
        {
            Loader.Log($"Migrating legacy WPQ save data from json files");

            foreach (var state in ModSaveManager.LoadLocoWaypointStatesFromSave())
            {
                _locoWaypointStates[state.LocomotiveId] = state;
            }

            foreach (var route in ModSaveManager.LoadRoutesFromSave())
            {
                _routes.Add(route.Id, route);
            }


            foreach (var routeAssignment in ModSaveManager.LoadRouteAssignmentsFromSave())
            {
                _routeAssignments.Add(routeAssignment.LocoId, routeAssignment);
            }
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
