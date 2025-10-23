using GalaSoft.MvvmLight.Messaging;
using Game.Events;
using HarmonyLib;
using System;
using System.Reflection;
using UI.Common;
using UnityEngine;
using UnityModManagerNet;

namespace WaypointQueue.UUM
{
#if DEBUG
    [EnableReloading]
#endif
    public static class Loader
    {
        public static UnityModManager.ModEntry ModEntry { get; private set; }
        public static Harmony HarmonyInstance { get; private set; }
        public static WaypointQueueController Instance { get; private set; }
        public static WaypointWindow WaypointWindow { get; private set; }
        public static WaypointQueueSettings Settings { get; private set; }
        private static bool MapHasLoaded = false;

        private static bool Load(UnityModManager.ModEntry modEntry)
        {
            if (ModEntry != null)
            {
                modEntry.Logger.Warning("WaypointQueue is already loaded!");
                return false;
            }

            ModEntry = modEntry;
            Settings = UnityModManager.ModSettings.Load<WaypointQueueSettings>(modEntry);
            Messenger.Default.Register<MapDidLoadEvent>(modEntry, OnMapDidLoad);
            ModEntry.OnUnload = Unload;
            ModEntry.OnToggle = OnToggle;
            ModEntry.OnGUI = OnGUI;
            ModEntry.OnSaveGUI = OnSaveGUI;
            ModEntry.OnUpdate = OnUpdate;

            HarmonyInstance = new Harmony(modEntry.Info.Id);
            Harmony.DEBUG = true;
            return true;
        }

        public static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            if (value)
            {
                try
                {
                    HarmonyInstance.PatchAll(Assembly.GetExecutingAssembly());
                    var waypointQueueGO = new GameObject("WaypointQueue");
                    Instance = waypointQueueGO.AddComponent<WaypointQueueController>();
                    UnityEngine.Object.DontDestroyOnLoad(waypointQueueGO);
                    if (MapHasLoaded && WaypointWindow == null)
                    {
                        InitWindow();
                    }
                }
                catch (Exception ex)
                {
                    modEntry.Logger.LogException($"Failed to load {modEntry.Info.DisplayName}:", ex);
                    HarmonyInstance?.UnpatchAll(modEntry.Info.Id);
                    if (Instance != null) UnityEngine.Object.DestroyImmediate(Instance.gameObject);
                    Instance = null;
                    if (WaypointWindow != null) UnityEngine.Object.DestroyImmediate(WaypointWindow.gameObject);
                    WaypointWindow = null;
                    return false;
                }
            }
            else
            {
                Unload(modEntry);
            }

            return true;
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float delta)
        {
            if (Settings.toggleWaypointPanelKey.Down() && WaypointWindow != null)
            {
                WaypointWindow.Toggle();
            }
        }

        private static bool Unload(UnityModManager.ModEntry modEntry)
        {
            HarmonyInstance.UnpatchAll(modEntry.Info.Id);
            if (Instance != null) UnityEngine.Object.DestroyImmediate(Instance.gameObject);
            Instance = null;
            return true;
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            Settings.Draw(modEntry);
        }

        private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            Settings.Save(modEntry);
        }

        private static void OnMapDidLoad(MapDidLoadEvent @event)
        {
            MapHasLoaded = true;
            InitWindow();
        }

        private static void InitWindow()
        {
            WindowHelper.CreateWindow<WaypointWindow>(null);
            WaypointWindow = WindowManager.Shared.GetWindow<WaypointWindow>();
        }

        public static void Log(string str)
        {
            ModEntry?.Logger.Log(str);
        }

        public static void LogDebug(string str)
        {
#if DEBUG
            ModEntry?.Logger.Log(str);
#endif
        }
    }
}
