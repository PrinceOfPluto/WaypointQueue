using GalaSoft.MvvmLight.Messaging;
using Game.Events;
using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;
using UnityModManagerNet;
using WaypointQueue.Services;
using WaypointQueue.UI;
using WaypointQueue.Wrappers;

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

        internal static ServiceProvider ServiceProvider { get; private set; }

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
                waypointQueueGO.AddComponent<ErrorModalController>();
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
            // This is a bit ugly but it provides the benefit of dependency injection
            // without the overhead of requiring an extra DI package
            ServiceProvider = new ServiceProvider();

            ServiceProvider.AddSingleton<IOpsControllerWrapper>(new OpsControllerWrapper());

            ServiceProvider.AddSingleton<TrainControllerWrapper>(new TrainControllerWrapper());

            ServiceProvider.AddSingleton<ICarService>(new CarService(
                ServiceProvider.GetService<TrainControllerWrapper>()
                ));

            ServiceProvider.AddSingleton<UncouplingService>(new UncouplingService(
                ServiceProvider.GetService<ICarService>(),
                ServiceProvider.GetService<IOpsControllerWrapper>()
                ));
            ServiceProvider.AddSingleton<AutoEngineerService>(new AutoEngineerService(
                ServiceProvider.GetService<ICarService>()
                ));

            ServiceProvider.AddSingleton<CouplingService>(new CouplingService(
                ServiceProvider.GetService<ICarService>(),
                ServiceProvider.GetService<AutoEngineerService>(),
                ServiceProvider.GetService<TrainControllerWrapper>()
                ));

            ServiceProvider.AddSingleton<RefuelService>(new RefuelService(
                ServiceProvider.GetService<ICarService>(),
                ServiceProvider.GetService<AutoEngineerService>(),
                ServiceProvider.GetService<IOpsControllerWrapper>()
                ));

            ServiceProvider.AddSingleton<WaypointResolver>(new WaypointResolver(
                ServiceProvider.GetService<UncouplingService>(),
                ServiceProvider.GetService<RefuelService>(),
                ServiceProvider.GetService<CouplingService>(),
                ServiceProvider.GetService<ICarService>(),
                ServiceProvider.GetService<AutoEngineerService>()
                ));
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
    }
}
