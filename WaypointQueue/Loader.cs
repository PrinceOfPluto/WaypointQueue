using GalaSoft.MvvmLight.Messaging;
using Game.Events;
using HarmonyLib;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using System.Reflection;
using UI.Common;
using UnityEngine;
using UnityModManagerNet;
using WaypointQueue.Services;

namespace WaypointQueue.UUM
{
    public static class Loader
    {
        public static UnityModManager.ModEntry ModEntry { get; private set; }
        public static Harmony HarmonyInstance { get; private set; }
        public static WaypointQueueController Instance { get; private set; }

        public static WaypointWindow WaypointWindow { get; private set; }
        public static RouteManagerWindow RouteManagerWindow { get; private set; }

        public static WaypointQueueSettings Settings { get; private set; }

        /** This intentionally implements a service locator pattern against the recommendations for using Microsoft Dependency Injection because the normal usage doesn't integrate DI easily into Unity game objects
        */
        public static ServiceProvider ServiceProvider { get; private set; }

        private static bool MapHasLoaded = false;

        private static bool Load(UnityModManager.ModEntry modEntry)
        {
            if (ModEntry != null)
            {
                modEntry.Logger.Warning("WaypointQueue is already loaded!");
                return false;
            }
            Log($"Loading WaypointQueue assembly version {Assembly.GetExecutingAssembly().GetName().Version}");

            ModEntry = modEntry;
            Settings = UnityModManager.ModSettings.Load<WaypointQueueSettings>(modEntry);

            ConfigureServices();

            Messenger.Default.Register<MapDidLoadEvent>(modEntry, OnMapDidLoad);

            ModEntry.OnUnload = Unload;
            ModEntry.OnGUI = OnGUI;
            ModEntry.OnSaveGUI = OnSaveGUI;
            ModEntry.OnUpdate = OnUpdate;

            HarmonyInstance = new Harmony(modEntry.Info.Id);
            //Harmony.DEBUG = true;

            try
            {
                HarmonyInstance.PatchAll(Assembly.GetExecutingAssembly());

                var waypointQueueGO = new GameObject("WaypointQueue");
                Instance = waypointQueueGO.AddComponent<WaypointQueueController>();
                waypointQueueGO.AddComponent<WaypointCarPicker>();
                UnityEngine.Object.DontDestroyOnLoad(waypointQueueGO);

                if (MapHasLoaded && (WaypointWindow == null || RouteManagerWindow == null))
                {
                    InitWindows();
                }
            }
            catch (Exception ex)
            {
                modEntry.Logger.LogException($"Failed to load {modEntry.Info.DisplayName}:", ex);
                Unload(modEntry);
                return false;
            }

            return true;
        }

        private static void ConfigureServices()
        {
            var services = new ServiceCollection();
            services.AddSingleton<WaypointResolver>();
            services.AddSingleton<UncouplingService>();
            services.AddSingleton<RefuelService>();
            services.AddSingleton<CouplingService>();
            ServiceProvider = services.BuildServiceProvider();
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float delta)
        {
            if (Settings.toggleWaypointPanelKey.Down() && WaypointWindow != null)
            {
                WaypointWindow.Toggle();
            }

            if (Settings.toggleRoutesPanelKey.Down() && RouteManagerWindow != null)
            {
                RouteManagerWindow.Toggle();
            }

        }

        private static bool Unload(UnityModManager.ModEntry modEntry)
        {
            HarmonyInstance.UnpatchAll(modEntry.Info.Id);

            if (Instance != null) UnityEngine.Object.DestroyImmediate(Instance.gameObject);
            Instance = null;

            if (WaypointWindow != null) UnityEngine.Object.DestroyImmediate(WaypointWindow.gameObject);
            WaypointWindow = null;

            if (RouteManagerWindow != null) UnityEngine.Object.DestroyImmediate(RouteManagerWindow.gameObject);
            RouteManagerWindow = null;

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

            InitWindows();
        }

        private static void InitWindows()
        {
            WindowHelper.CreateWindow<WaypointWindow>(null);
            WaypointWindow = WaypointWindow.Shared;

            WindowHelper.CreateWindow<RouteManagerWindow>(null);
            RouteManagerWindow = RouteManagerWindow.Shared;
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

        public static void LogError(string str)
        {
            ModEntry?.Logger.Error(str);
        }

        public static void ShowErrorModal(string title, string message)
        {
            bool railloaderIsActive = IsRailloaderActive();
            string attachLogFilePrompt = railloaderIsActive ? "both your Player.log and Railloader.log files" : "your Player.log file";
            message = $"{message}\n\nPlease create a bug report on GitHub or Discord and attach {attachLogFilePrompt} to help this bug get resolved faster. Thank you!";

            string playerLogFilePrompt = "Open Player.log file";

            ModalAlertController.Present(title, message, [(0, playerLogFilePrompt), (1, "Close")], (int value) =>
            {
                if (value == 0)
                {
                    OpenPlayerLogFile();
                }
            });
        }

        private static bool IsRailloaderActive()
        {
            string assemblyName = "Railloader";
            Assembly[] loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (Assembly assembly in loadedAssemblies)
            {
                if (assembly.GetName().Name == assemblyName)
                {
                    Loader.LogDebug($"Assembly {assemblyName} is loaded");
                    return true;
                }
            }
            Loader.LogDebug($"Assembly {assemblyName} is NOT loaded");
            return false;
        }

        private static void OpenPlayerLogFile()
        {
            string filePath = Path.Combine(Application.persistentDataPath, "Player.log");
            if (File.Exists(filePath))
            {
                Application.OpenURL(filePath);
            }
        }

        private static void OpenRailloaderLogFile()
        {
            string filePath = Path.Combine(AppContext.BaseDirectory, "Railloader.log");
            if (File.Exists(filePath))
            {
                Application.OpenURL(filePath);
            }
        }
    }
}
