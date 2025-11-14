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
    public static class WaypointSaveManager
    {
        private static string SavePath = Path.Combine(Application.persistentDataPath, "Waypoints");

        public static WaypointSaveState UnappliedSaveState { get; private set; }

        private static bool _timeAlreadyStarted = false;

        public static string RoutesSaveDir { get; private set; }
        public class WaypointSaveState
        {
            public int Version { get; set; }

            public List<LocoWaypointState> WaypointStates { get; set; }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WorldStore), nameof(WorldStore.Save))]
        static void SavePostfix(string saveName)
        {
            Loader.LogDebug($"Save postfix for save name: {saveName}");
            RoutesSaveDir = ExtractSaveName(saveName);
            try
            {
                WaypointSaveState saveState = CaptureSaveState();
                string savePath = SavePath;
                if (!Directory.Exists(savePath))
                {
                    Loader.Log($"Creating directory {savePath}");
                    Directory.CreateDirectory(savePath);
                }

                string fullSavePath = PathForSaveName(saveName);
                Loader.LogDebug($"Snapshot of waypoint state has {saveState.WaypointStates.Count} members");
                string json = JsonConvert.SerializeObject(saveState);
                File.WriteAllText(fullSavePath, json);
                Loader.Log($"Wrote {json.Length} to {fullSavePath}");
            }
            catch (Exception e)
            {
                Loader.Log($"Failed to save waypoint manager data: {e}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WorldStore), nameof(WorldStore.Load))]
        static void LoadPostfix(string saveName)
        {
            Loader.LogDebug($"Load postfix for save name: {saveName}");
            RoutesSaveDir = ExtractSaveName(saveName);
            RouteRegistry.ReloadFromDisk();
            RouteAssignmentSaveManager.LoadForSave(RoutesSaveDir);
            _timeAlreadyStarted = false;
            try
            {
                if (saveName == null || saveName.Length == 0)
                {
                    throw new InvalidOperationException("No save name detected when trying to load waypoint state");
                }
                string fullSavePath = PathForSaveName(saveName);
                if (!File.Exists(fullSavePath))
                {
                    Loader.Log($"No saved waypoints file found for {fullSavePath}");
                    return;
                }

                Loader.Log($"Loading from {fullSavePath}");
                string json = File.ReadAllText(fullSavePath);
                UnappliedSaveState = JsonConvert.DeserializeObject<WaypointSaveState>(json);
                Loader.Log($"Deserialized waypoints v{UnappliedSaveState.Version} with {UnappliedSaveState.WaypointStates.Count} entries");
            }
            catch (Exception e)
            {
                Loader.Log($"Failed to load waypoints for {saveName}: {e}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TimeObserver), nameof(TimeObserver.StartObservering))]
        static void StartObserveringPostfix()
        {
            Loader.LogDebug($"StartObservering postfix");
            if(_timeAlreadyStarted) return;
            _timeAlreadyStarted = true;
            Loader.LogDebug($"The dawn of time");

            WaypointQueueController.Shared.InitCarLoaders(reload: true);

            if (WaypointSaveManager.UnappliedSaveState != null)
            {
                WaypointQueueController.Shared.LoadWaypointSaveState(UnappliedSaveState);
            }
            else
            {
                Loader.Log($"No save state to load for WaypointQueueController");
            }
        }

        private static string PathForSaveName(string saveName)
        {
            return Path.Combine(SavePath, saveName + ".waypoints.json");
        }
        private static WaypointSaveState CaptureSaveState()
        {
            WaypointSaveState saveState = new WaypointSaveState();
            saveState.Version = 1;
            saveState.WaypointStates = WaypointQueueController.Shared.WaypointStateList;
            return saveState;
        }
        private static string ExtractSaveName(string saveName)
        {
            if (string.IsNullOrWhiteSpace(saveName))
                return "Global";

            var trimmed = saveName.Trim();
            var autoIndex = trimmed.IndexOf("_auto", StringComparison.OrdinalIgnoreCase);
            if (autoIndex > 0)
            {
                trimmed = trimmed.Substring(0, autoIndex);
            }
            return trimmed;
        }

    }
}
