using Game.Persistence;
using Game.State;
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
    internal static class ModSaveManager
    {
        private static string WaypointSaveDir = Path.Combine(Application.persistentDataPath, "Waypoints");
        private static string RouteSaveDir = Path.Combine(Application.persistentDataPath, "Routes");

        public static WaypointSaveState UnappliedWaypointSaveState { get; private set; }

        private static bool _timeAlreadyStarted = false;

        public class WaypointSaveState
        {
            public int Version { get; set; }

            public List<LocoWaypointState> WaypointStates { get; set; }
        }

        public class RouteAssignmentSaveState
        {
            public int Version { get; set; }
            public List<RouteAssignment> Assignments { get; set; }
        }

        public class RouteDefinitionSaveState
        {
            public int Version { get; set; }
            public List<RouteDefinition> RouteDefinitions { get; set; }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WorldStore), nameof(WorldStore.Save))]
        static void SavePostfix(string saveName)
        {
            Loader.LogDebug($"Save postfix for save name: {saveName}");

            SaveWaypoints(saveName);
            SaveRouteAssignments(saveName);
            SaveRoutes(saveName);
        }

        static void SaveWaypoints(string saveName)
        {
            try
            {
                WaypointSaveState saveState = CaptureWaypointSaveState();
                string saveDir = WaypointSaveDir;
                if (!Directory.Exists(saveDir))
                {
                    Loader.Log($"Creating directory {saveDir}");
                    Directory.CreateDirectory(saveDir);
                }

                string fullSavePath = PathForSavedWaypoints(saveName);
                Loader.LogDebug($"Snapshot of waypoint state has {saveState.WaypointStates.Count} members");
                string json = JsonConvert.SerializeObject(saveState, Formatting.Indented);
                File.WriteAllText(fullSavePath, json);
                Loader.Log($"Wrote {json.Length} to {fullSavePath}");
            }
            catch (Exception e)
            {
                Loader.Log($"Failed to save waypoint manager data: {e}");
            }
        }

        static void SaveRouteAssignments(string saveName)
        {
            try
            {
                RouteAssignmentSaveState saveState = CaptureRouteAssignmentSaveState();
                string saveDir = RouteSaveDir;
                if (!Directory.Exists(saveDir))
                {
                    Loader.Log($"Creating directory {saveDir}");
                    Directory.CreateDirectory(saveDir);
                }

                string fullSavePath = PathForSavedAssignments(saveName);
                string json = JsonConvert.SerializeObject(saveState, Formatting.Indented);
                File.WriteAllText(fullSavePath, json);
                Loader.Log($"[RouteAssign] Saved {saveState.Assignments.Count} assignments for '{saveName}' → {fullSavePath}");
            }
            catch (Exception e)
            {
                Loader.Log($"[RouteAssign] Save failed for '{saveName}': {e}");
            }
        }

        static void SaveRoutes(string saveName)
        {
            try
            {
                RouteDefinitionSaveState saveState = CaptureRouteDefinitionSaveState();
                string saveDir = RouteSaveDir;
                if (!Directory.Exists(saveDir))
                {
                    Loader.Log($"Creating directory {saveDir}");
                    Directory.CreateDirectory(saveDir);
                }

                string fullSavePath = PathForSavedRoutes(saveName);
                string json = JsonConvert.SerializeObject(saveState, Formatting.Indented);
                File.WriteAllText(fullSavePath, json);
                Loader.Log($"[Routes] Saved {saveState.RouteDefinitions.Count} routes for '{saveName}' → {fullSavePath}");
            }
            catch (Exception e)
            {
                Loader.Log($"[Routes] Save failed for '{saveName}': {e}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WorldStore), nameof(WorldStore.Load))]
        static void LoadPostfix(string saveName)
        {
            Loader.LogDebug($"Load postfix for save name: {saveName}");
            _timeAlreadyStarted = false;

            LoadWaypointsFromSave(saveName);
            LoadRoutesFromSave(saveName);
            LoadRouteAssignmentsFromSave(saveName);
        }

        static void LoadWaypointsFromSave(string saveName)
        {
            try
            {
                if (saveName == null || saveName.Length == 0)
                {
                    throw new InvalidOperationException("No save name detected when trying to load waypoint state");
                }
                string fullSavePath = PathForSavedWaypoints(saveName);
                if (!File.Exists(fullSavePath))
                {
                    Loader.Log($"No saved waypoints file found for {fullSavePath}");
                    return;
                }

                Loader.Log($"Loading from {fullSavePath}");
                string json = File.ReadAllText(fullSavePath);
                UnappliedWaypointSaveState = JsonConvert.DeserializeObject<WaypointSaveState>(json);
                Loader.Log($"Deserialized waypoints v{UnappliedWaypointSaveState.Version} with {UnappliedWaypointSaveState.WaypointStates.Count} entries");
            }
            catch (Exception e)
            {
                Loader.Log($"Failed to load waypoints for {saveName}: {e}");
            }
        }

        static void LoadRouteAssignmentsFromSave(string saveName)
        {
            try
            {
                if (saveName == null || saveName.Length == 0)
                {
                    throw new InvalidOperationException("No save name detected when trying to load route assignment state");
                }
                string fullSavePath = PathForSavedAssignments(saveName);

                if (!File.Exists(fullSavePath))
                {
                    RouteAssignmentRegistry.ReplaceAll(null);
                    Loader.Log($"[RouteAssign] No assignments for save '{saveName}', cleared.");
                    return;
                }

                var json = File.ReadAllText(fullSavePath);
                var data = JsonConvert.DeserializeObject<RouteAssignmentSaveState>(json);
                RouteAssignmentRegistry.ReplaceAll(data?.Assignments);
                Loader.Log($"[RouteAssign] Loaded {data?.Assignments?.Count ?? 0} assignments for '{saveName}'.");
            }
            catch (Exception e)
            {
                Loader.Log($"[RouteAssign] Load failed for '{saveName}': {e}");
                RouteAssignmentRegistry.ReplaceAll(null);
            }
        }

        static void LoadRoutesFromSave(string saveName)
        {
            try
            {
                if (saveName == null || saveName.Length == 0)
                {
                    throw new InvalidOperationException("No save name detected when trying to load routes state");
                }
                string fullSavePath = PathForSavedRoutes(saveName);

                if (!File.Exists(fullSavePath))
                {
                    RouteRegistry.ReplaceAll(null);
                    Loader.Log($"[Routes] No routes for save '{saveName}', cleared.");
                    return;
                }

                string json = File.ReadAllText(fullSavePath);
                RouteDefinitionSaveState data = JsonConvert.DeserializeObject<RouteDefinitionSaveState>(json);
                RouteRegistry.ReplaceAll(data.RouteDefinitions);
                Loader.Log($"[Routes] Loaded {data?.RouteDefinitions?.Count ?? 0} routes for '{saveName}'.");
            }
            catch (Exception e)
            {
                Loader.Log($"[Routes] Load failed for '{saveName}': {e}");
                RouteRegistry.ReplaceAll(null);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TimeObserver), nameof(TimeObserver.StartObservering))]
        static void StartObserveringPostfix()
        {
            Loader.LogDebug($"StartObservering postfix");
            if (_timeAlreadyStarted) return;
            _timeAlreadyStarted = true;
            Loader.LogDebug($"The dawn of time");

            WaypointQueueController.Shared.InitCarLoaders(reload: true);
            RouteRegistry.LoadWaypointsForRoutes();

            if (ModSaveManager.UnappliedWaypointSaveState != null)
            {
                WaypointQueueController.Shared.LoadWaypointSaveState(UnappliedWaypointSaveState);
            }
            else
            {
                Loader.Log($"No save state to load for WaypointQueueController");
            }
        }

        private static string PathForSavedWaypoints(string saveName)
        {
            return Path.Combine(WaypointSaveDir, saveName + ".waypoints.json");
        }

        private static string PathForSavedAssignments(string saveName)
        {
            return Path.Combine(RouteSaveDir, saveName + ".route_assignments.json");
        }

        private static string PathForSavedRoutes(string saveName)
        {
            return Path.Combine(RouteSaveDir, saveName + ".routes.json");
        }

        private static WaypointSaveState CaptureWaypointSaveState()
        {
            WaypointSaveState saveState = new WaypointSaveState();
            saveState.Version = 1;
            saveState.WaypointStates = [.. WaypointQueueController.Shared.WaypointStateMap.Values];
            return saveState;
        }

        private static RouteAssignmentSaveState CaptureRouteAssignmentSaveState()
        {
            RouteAssignmentSaveState saveState = new RouteAssignmentSaveState
            {
                Version = 1,
                Assignments = RouteAssignmentRegistry.All()
            };
            return saveState;
        }

        private static RouteDefinitionSaveState CaptureRouteDefinitionSaveState()
        {
            RouteDefinitionSaveState saveState = new RouteDefinitionSaveState
            {
                Version = 1,
                RouteDefinitions = RouteRegistry.Routes
            };
            return saveState;
        }
    }
}
