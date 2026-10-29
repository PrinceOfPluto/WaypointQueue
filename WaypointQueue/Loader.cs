using Game.Events;
using HarmonyLib;
using Railroader.Modding;
using System;
using System.Reflection;
using UnityEngine;
using WaypointQueue.Services;
using WaypointQueue.Settings;
using WaypointQueue.State;
using WaypointQueue.UI;
using WaypointQueue.Wrappers;

namespace WaypointQueue
{
    public class Loader : IModEntryPoint
    {
        public IModContext ModContext { get; private set; }
        public Harmony HarmonyInstance { get; private set; }
        public WaypointQueueController Instance { get; private set; }

        public WaypointWindow WaypointWindow { get; private set; }
        public RouteManagerWindow RouteManagerWindow { get; private set; }
        public ModSettingsWindow ModSettingsWindow { get; private set; }

        internal ServiceProvider ServiceProvider { get; private set; }

        private bool MapHasLoaded = false;

        public GameObject QueueStorageGO { get; private set; }

        public GameObject RouteStorageGO { get; private set; }

        public static string QueueStorageGOName = "WaypointQueueStateStorage";
        public static string RouteStorageGOName = "WaypointQueueRouteStorage";

        public void OnModLoad(IModContext modContext)
        {
            if (ModContext != null)
            {
                modContext.Log.Warning("WaypointQueue is already loaded!");
            }
            Assembly assembly = Assembly.GetExecutingAssembly();
            if (assembly == null)
            {
                modContext.Log.Error("WaypointQueue assembly is null");
            }
            AssemblyName assemblyName = assembly.GetName();
            if (assemblyName == null)
            {
                modContext.Log.Error("WaypointQueue assembly name is null");
            }
            //Log($"Loading WaypointQueue assembly version {Assembly.GetExecutingAssembly().GetName().Version}");

            ModContext = modContext;
            ModLocator.Loader = this;

            ModLocator.Settings = WaypointQueueSettings.Load();

            ServiceProvider = new ServiceProvider();
            ModLocator.ServiceProvider = ServiceProvider;
            ConfigureServices();

            EventBus.Subscribe<MapDidLoadEvent>(OnMapDidLoad);

            HarmonyInstance = new Harmony(ModContext.ModIdentifier);
            //Harmony.DEBUG = true;

            try
            {
                HarmonyInstance.PatchAll(Assembly.GetExecutingAssembly());

                var waypointQueueGO = new GameObject("WaypointQueue");
                Instance = waypointQueueGO.AddComponent<WaypointQueueController>();
                ModLocator.QueueController = Instance;
                waypointQueueGO.AddComponent<WaypointCarPicker>();
                waypointQueueGO.AddComponent<ErrorModalController>();
                waypointQueueGO.AddComponent<WaypointPicker>();
                waypointQueueGO.AddComponent<ModStateManager>();
                WaypointWindow = waypointQueueGO.AddComponent<WaypointWindow>();
                RouteManagerWindow = waypointQueueGO.AddComponent<RouteManagerWindow>();
                ModSettingsWindow = waypointQueueGO.AddComponent<ModSettingsWindow>();

                QueueStorageGO = new GameObject(QueueStorageGOName);
                RouteStorageGO = new GameObject(RouteStorageGOName);

                UnityEngine.Object.DontDestroyOnLoad(waypointQueueGO);
                UnityEngine.Object.DontDestroyOnLoad(QueueStorageGO);
                UnityEngine.Object.DontDestroyOnLoad(RouteStorageGO);

                if (MapHasLoaded && (WaypointWindow == null || RouteManagerWindow == null || ModSettingsWindow == null))
                {
                    InitWindows();
                }
            }
            catch (Exception ex)
            {
                modContext.Log.Error(ex, $"Failed to load {modContext.ModIdentifier}:");
            }
        }

        private void ConfigureServices()
        {
            // This is a bit ugly but it provides the benefit of dependency injection
            // without the overhead of requiring an extra DI package


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

        public void OnModUnload()
        {
            HarmonyInstance.UnpatchAll(ModContext.ModIdentifier);

            if (Instance != null) UnityEngine.Object.DestroyImmediate(Instance.gameObject);
            Instance = null;

            if (QueueStorageGO != null) UnityEngine.Object.DestroyImmediate(QueueStorageGO.gameObject);
            QueueStorageGO = null;

            if (RouteStorageGO != null) UnityEngine.Object.DestroyImmediate(RouteStorageGO.gameObject);
            RouteStorageGO = null;
        }

        private void OnMapDidLoad(MapDidLoadEvent @event)
        {
            MapHasLoaded = true;

            InitWindows();
        }

        private void InitWindows()
        {
            WaypointWindow.Register();
            RouteManagerWindow.Register();
            ModSettingsWindow.Register();
        }

        public void Log(string str)
        {
            ModContext.Log.Info(str);
        }

        public void LogDebug(string str)
        {
            ModContext.Log.Debug(str);
        }

        public void LogError(string str)
        {
            ModContext.Log.Error(str);
        }
    }
}
