using GalaSoft.MvvmLight.Messaging;
using Game.Events;
using Game.Persistence;
using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    [HarmonyPatch]
    internal class ModSaveManager : MonoBehaviour
    {
        public static ModSaveManager Shared { get; private set; }

        private string _waypointSaveDir;
        private string _routeSaveDir;

        private string _saveName = String.Empty;

        private void Awake()
        {
            _waypointSaveDir = Path.Combine(Application.persistentDataPath, "Waypoints");
            _routeSaveDir = Path.Combine(Application.persistentDataPath, "Routes");
        }

        private void OnEnable()
        {
            Shared = this;
            Messenger.Default.Register<MapDidUnloadEvent>(this, OnMapDidUnload);
        }

        private void OnDisable()
        {
            Shared = null;
            Messenger.Default.Unregister(this);
        }

        private void OnMapDidUnload(MapDidUnloadEvent mapDidUnloadEvent)
        {
            _saveName = String.Empty;
        }

        public class WaypointSaveState
        {
            public int Version { get; set; }

            public List<LocoWaypointState> WaypointStates { get; set; } = [];
        }

        public class RouteAssignmentSaveState
        {
            public int Version { get; set; }
            public List<RouteAssignment> Assignments { get; set; } = [];
        }

        public class RouteDefinitionSaveState
        {
            public int Version { get; set; }
            public List<RouteDefinition> RouteDefinitions { get; set; } = [];
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WorldStore), nameof(WorldStore.Load))]
        static void LoadPostfix(string saveName)
        {
            Shared._saveName = saveName;
        }

        internal List<LocoWaypointState> LoadLocoWaypointStatesFromSave()
        {
            try
            {
                if (_saveName == null || _saveName.Length == 0)
                {
                    throw new InvalidOperationException("No save name detected when trying to load waypoint state");
                }
                string fullSavePath = PathForSavedWaypoints(_saveName);
                if (!File.Exists(fullSavePath))
                {
                    Loader.Log($"No saved waypoints file found for {fullSavePath}");
                    return [];
                }

                Loader.Log($"Loading from {fullSavePath}");
                string json = File.ReadAllText(fullSavePath);
                var waypointSaveState = JsonConvert.DeserializeObject<WaypointSaveState>(json);
                Loader.Log($"Deserialized waypoints v{waypointSaveState.Version} with {waypointSaveState.WaypointStates.Count} entries");
                return waypointSaveState.WaypointStates;
            }
            catch (Exception e)
            {
                Loader.Log($"Failed to load waypoints for {_saveName}: {e}");
                return [];
            }
        }

        internal List<RouteAssignment> LoadRouteAssignmentsFromSave()
        {
            try
            {
                if (_saveName == null || _saveName.Length == 0)
                {
                    throw new InvalidOperationException("No save name detected when trying to load route assignment state");
                }
                string fullSavePath = PathForSavedAssignments(_saveName);

                if (!File.Exists(fullSavePath))
                {
                    Loader.Log($"[RouteAssign] No assignments for save '{_saveName}', cleared.");
                    return [];
                }

                var json = File.ReadAllText(fullSavePath);
                var data = JsonConvert.DeserializeObject<RouteAssignmentSaveState>(json);
                Loader.Log($"[RouteAssign] Loaded {data.Assignments?.Count ?? 0} assignments for '{_saveName}'.");
                return data.Assignments;
            }
            catch (Exception e)
            {
                Loader.Log($"[RouteAssign] Load failed for '{_saveName}': {e}");
                return [];
            }
        }

        internal List<RouteDefinition> LoadRoutesFromSave()
        {
            try
            {
                if (_saveName == null || _saveName.Length == 0)
                {
                    throw new InvalidOperationException("No save name detected when trying to load routes state");
                }
                string fullSavePath = PathForSavedRoutes(_saveName);

                if (!File.Exists(fullSavePath))
                {
                    Loader.Log($"[Routes] No routes for save '{_saveName}', cleared.");
                    return [];
                }

                string json = File.ReadAllText(fullSavePath);
                RouteDefinitionSaveState data = JsonConvert.DeserializeObject<RouteDefinitionSaveState>(json);
                Loader.Log($"[Routes] Loaded {data.RouteDefinitions?.Count ?? 0} routes for '{_saveName}'.");
                return data.RouteDefinitions;
            }
            catch (Exception e)
            {
                Loader.Log($"[Routes] Load failed for '{_saveName}': {e}");
                return [];
            }
        }

        private string PathForSavedWaypoints(string saveName)
        {
            return Path.Combine(_waypointSaveDir, saveName + ".waypoints.json");
        }

        private string PathForSavedAssignments(string saveName)
        {
            return Path.Combine(_routeSaveDir, saveName + ".route_assignments.json");
        }

        private string PathForSavedRoutes(string saveName)
        {
            return Path.Combine(_routeSaveDir, saveName + ".routes.json");
        }
    }
}
