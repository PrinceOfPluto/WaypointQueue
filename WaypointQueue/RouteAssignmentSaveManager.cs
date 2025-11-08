using System;
using System.Collections.Generic;
using System.IO;
using Game.Persistence;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    [HarmonyPatch]
    public static class RouteAssignmentSavePatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(WorldStore), nameof(WorldStore.Save))]
        static void SavePostfix(string saveName)
        {
            RouteAssignmentSaveManager.SaveForSave(saveName);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WorldStore), nameof(WorldStore.Load))]
        static void LoadPostfix(string saveName)
        {
            RouteAssignmentSaveManager.LoadForSave(saveName);
        }
    }
    public static class RouteAssignmentSaveManager
    {
        private static string BaseDir => Path.Combine(Application.persistentDataPath, "Routes");

        [Serializable]
        private class AssignmentFile
        {
            public int version = 1;
            public List<RouteAssignment> items = new List<RouteAssignment>();
        }

        private static string PathFor(string saveName)
        {
            return Path.Combine(BaseDir, saveName + ".route_assignments.json");
        }

        public static void LoadForSave(string saveName)
        {
            try
            {
                if (!Directory.Exists(BaseDir))
                    Directory.CreateDirectory(BaseDir);

                var path = PathFor(saveName);
                if (!File.Exists(path))
                {
                    // no assignments for this save → clear
                    RouteAssignmentRegistry.ReplaceAll(null);
                    Loader.Log($"[RouteAssign] No assignments for save '{saveName}', cleared.");
                    return;
                }

                var json = File.ReadAllText(path);
                var data = JsonConvert.DeserializeObject<AssignmentFile>(json);
                RouteAssignmentRegistry.ReplaceAll(data?.items);
                Loader.Log($"[RouteAssign] Loaded {data?.items?.Count ?? 0} assignments for '{saveName}'.");
            }
            catch (Exception e)
            {
                Loader.Log($"[RouteAssign] Load failed for '{saveName}': {e}");
                RouteAssignmentRegistry.ReplaceAll(null);
            }
        }

        public static void SaveForSave(string saveName)
        {
            try
            {
                if (!Directory.Exists(BaseDir))
                    Directory.CreateDirectory(BaseDir);

                var data = new AssignmentFile
                {
                    version = 1,
                    items = RouteAssignmentRegistry.All()
                };

                var json = JsonConvert.SerializeObject(data, Formatting.Indented);
                var path = PathFor(saveName);
                File.WriteAllText(path, json);
                Loader.Log($"[RouteAssign] Saved {data.items.Count} assignments for '{saveName}' → {path}");
            }
            catch (Exception e)
            {
                Loader.Log($"[RouteAssign] Save failed for '{saveName}': {e}");
            }
        }
    }
}
