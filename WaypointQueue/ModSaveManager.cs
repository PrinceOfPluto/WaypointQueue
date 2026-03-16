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
    internal static class ModSaveManager
    {
        private static readonly string WaypointSaveDir = Path.Combine(Application.persistentDataPath, "Waypoints");
        private static readonly string RouteSaveDir = Path.Combine(Application.persistentDataPath, "Routes");

        private static string SaveName { get; set; }

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
        [HarmonyPatch(typeof(WorldStore), nameof(WorldStore.Load))]
        static void LoadPostfix(string saveName)
        {
            SaveName = saveName;
        }

        internal static List<LocoWaypointState> LoadLocoWaypointStatesFromSave()
        {
            try
            {
                if (SaveName == null || SaveName.Length == 0)
                {
                    throw new InvalidOperationException("No save name detected when trying to load waypoint state");
                }
                string fullSavePath = PathForSavedWaypoints(SaveName);
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
                Loader.Log($"Failed to load waypoints for {SaveName}: {e}");
                return [];
            }
        }

        internal static List<RouteAssignment> LoadRouteAssignmentsFromSave()
        {
            try
            {
                if (SaveName == null || SaveName.Length == 0)
                {
                    throw new InvalidOperationException("No save name detected when trying to load route assignment state");
                }
                string fullSavePath = PathForSavedAssignments(SaveName);

                if (!File.Exists(fullSavePath))
                {
                    Loader.Log($"[RouteAssign] No assignments for save '{SaveName}', cleared.");
                    return [];
                }

                var json = File.ReadAllText(fullSavePath);
                var data = JsonConvert.DeserializeObject<RouteAssignmentSaveState>(json);
                Loader.Log($"[RouteAssign] Loaded {data?.Assignments?.Count ?? 0} assignments for '{SaveName}'.");
                return data?.Assignments;
            }
            catch (Exception e)
            {
                Loader.Log($"[RouteAssign] Load failed for '{SaveName}': {e}");
                return [];
            }
        }

        internal static List<RouteDefinition> LoadRoutesFromSave()
        {
            try
            {
                if (SaveName == null || SaveName.Length == 0)
                {
                    throw new InvalidOperationException("No save name detected when trying to load routes state");
                }
                string fullSavePath = PathForSavedRoutes(SaveName);

                if (!File.Exists(fullSavePath))
                {
                    Loader.Log($"[Routes] No routes for save '{SaveName}', cleared.");
                    return [];
                }

                string json = File.ReadAllText(fullSavePath);
                RouteDefinitionSaveState data = JsonConvert.DeserializeObject<RouteDefinitionSaveState>(json);
                Loader.Log($"[Routes] Loaded {data?.RouteDefinitions?.Count ?? 0} routes for '{SaveName}'.");
                return data.RouteDefinitions;
            }
            catch (Exception e)
            {
                Loader.Log($"[Routes] Load failed for '{SaveName}': {e}");
                return [];
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
    }
}
