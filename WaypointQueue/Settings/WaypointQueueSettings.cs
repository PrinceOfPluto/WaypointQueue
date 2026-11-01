using Game;
using Newtonsoft.Json;
using RailroaderModSettings;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine.InputSystem;

namespace WaypointQueue.Settings
{
    public enum CoupleSearchModeDefaultOptions
    {
        Nearest = ManagedWaypoint.CoupleSearchMode.Nearest,
        BySpecificCar = ManagedWaypoint.CoupleSearchMode.SpecificCar,
    }

    public enum UncoupleModeDefaultOptions
    {
        ByCount = ManagedWaypoint.UncoupleMode.ByCount,
        ByDestinationArea = ManagedWaypoint.UncoupleMode.ByDestinationArea,
        ByDestinationIndustry = ManagedWaypoint.UncoupleMode.ByDestinationIndustry,
        ByDestinationTrack = ManagedWaypoint.UncoupleMode.ByDestinationTrack,
        BySpecificCar = ManagedWaypoint.UncoupleMode.BySpecificCar,
        AllExceptLocomotives = ManagedWaypoint.UncoupleMode.AllExceptLocomotives,
    }

    public enum PostCoupleCutModeDefaultOptions
    {
        Pickup = ManagedWaypoint.PostCoupleCutType.Pickup,
        Dropoff = ManagedWaypoint.PostCoupleCutType.Dropoff,
    }

    public sealed class WaypointQueueSettings
    {
        [ModKeybind("Activate append waypoint mode", Section = "Keybindings",
            Tooltip = "Setting a waypoint while this key is pressed will add it to the end of the locomotive's waypoint queue.")]
        public ModKeyBinding queuedWaypointModeKey = new ModKeyBinding(Key.LeftCtrl);

        [ModKeybind("Activate replace waypoint mode", Section = "Keybindings",
            Tooltip = "Setting a waypoint while this key is pressed will replace the current waypoint but keep the rest of the existing waypoint queue.")]
        public ModKeyBinding replaceWaypointModeKey = new ModKeyBinding(Key.LeftAlt);

        [ModKeybind("Activate insert next waypoint mode", Section = "Keybindings",
            Tooltip = "Setting a waypoint while this key is pressed will insert it in the queue directly after the current waypoint.")]
        public ModKeyBinding insertNextWaypointModeKey = new ModKeyBinding(Key.LeftShift);

        [ModKeybind("Create waypoint with couple to nearest enabled", Section = "Keybindings",
            Tooltip = "Setting a waypoint while this key is pressed will create a waypoint with couple to nearest enabled.")]
        public ModKeyBinding CoupleToNearestShortcutHotkey = new ModKeyBinding();

        [ModKeybind("Create waypoint with uncouple enabled", Section = "Keybindings",
            Tooltip = "Setting a waypoint while this key is pressed will create a waypoint with uncoupling enabled.")]
        public ModKeyBinding UncoupleShortcutHotkey = new ModKeyBinding();

        [ModKeybind("Create waypoint with kicking enabled", Section = "Keybindings",
            Tooltip = "Setting a waypoint while this key is pressed will create a waypoint with kicking enabled.")]
        public ModKeyBinding KickingShortcutHotkey = new ModKeyBinding();

        [ModKeybind("Toggle Waypoints window", Section = "Keybindings")]
        public ModKeyBinding toggleWaypointPanelKey = new ModKeyBinding(Key.G, shift: true);

        [ModKeybind("Toggle Route Manager window", Section = "Keybindings")]
        public ModKeyBinding toggleRoutesPanelKey = new ModKeyBinding(Key.Z, ctrl: true);

        [ModSetting("Make append waypoint the default without keybinding", Section = "Keybindings")]
        public bool MakeAppendDefaultWithoutHotkeybinding = false;

        [ModSetting("Show time info in dropdown for timetable train symbol", Section = "UI")]
        public bool ShowTimeInTrainSymbolDropdown = true;

        [ModSetting("Connect air by default when coupling", Section = "Coupling settings")]
        public bool ConnectAirByDefault = true;

        [ModSetting("Release handbrakes by default when coupling", Section = "Coupling settings")]
        public bool ReleaseHandbrakesByDefault = true;

        [ModSetting("Nearby coupling search distance in car lengths", Section = "Coupling settings", Min = 1, Max = 100,
            Precision = 0, Tooltip = "Distance to search for nearby coupling. Measured in car lengths of about 40 ft each")]
        public float NearbyCouplingSearchDistanceInCarLengths = 10f;

        [ModSetting("Default coupling search mode", Section = "Coupling settings")]
        public CoupleSearchModeDefaultOptions DefaultCouplingSearchMode = CoupleSearchModeDefaultOptions.BySpecificCar;

        [ModSetting("Bleed air cylinders by default when uncoupling", Section = "Uncoupling settings")]
        public bool BleedAirByDefault = true;

        [ModSetting("Leave anglecock open by default after cutting cars (do not bottle air)", Section = "Uncoupling settings")]
        public bool DoNotBottleAir = false;

        [ModSetting("UseLegacyHotkeyOptions", Section = "UseLegacyHotkeyOptions")]
        public bool UseLegacyHotkeyOptions = false;

        [ModSetting("Apply handbrakes by default when uncoupling", Section = "Uncoupling settings")]
        public bool ApplyHandbrakesByDefault = true;

        [ModSetting("Max seconds to wait for train to fully stop before uncoupling", Section = "Uncoupling settings",
            Min = 5, Max = 120, Precision = 0,
            Tooltip = "Trains will wait to uncouple until the consist is considered at fully rest\nor until this many seconds have passed, whichever happens sooner.\nIf you are encountering trains unexpectedly re-coupling after being uncoupled,\nit is recommended to increase this timeout value.")]
        public float WaitBeforeCuttingTimeout = 15f;

        [ModSetting("Handbrake percentage", Section = "Uncoupling settings", Min = 0, Max = 1, Precision = 2,
            Tooltip = "Handbrakes will be set on this percentage of uncoupled cars")]
        public float HandbrakePercentOnUncouple = 0.1f;

        [ModSetting("Handbrake minimum", Section = "Uncoupling settings", Min = 1, Precision = 0,
            Tooltip = "At least this amount of handbrakes will always be set on uncoupled cars regardless of cut length")]
        public int MinimumHandbrakesOnUncouple = 2;

        [ModSetting("Default uncoupling mode", Section = "Uncoupling settings")]
        public UncoupleModeDefaultOptions DefaultUncouplingMode = UncoupleModeDefaultOptions.ByCount;

        [ModSetting("Default post-coupling cut mode", Section = "Uncoupling settings")]
        public PostCoupleCutModeDefaultOptions DefaultPostCouplingCutMode = PostCoupleCutModeDefaultOptions.Pickup;

        [ModSetting("Default uncoupling mode when performing post-coupling cuts", Section = "Uncoupling settings")]
        public UncoupleModeDefaultOptions DefaultPostCouplingCutUncouplingMode = UncoupleModeDefaultOptions.ByCount;

        [ModSetting("Passing speed limit for kicking cars", Section = "Custom Defaults", Min = 0, Max = 45)]
        public int PassingSpeedForKickingCars = 7;

        [ModSetting("Kick button unchecks bleeding air and applying handbrakes on uncouple", Section = "Custom Defaults")]
        public bool UncheckAirAndBrakesForKick = true;

        [ModSetting("Do not automatically set passing speed limit when not stopping at waypoint", Section = "Custom Defaults")]
        public bool DoNotLimitPassingSpeedDefault = true;

        [ModSetting("Prevent tenders being uncoupled from locomotive", Section = "Custom Defaults")]
        public bool PreventTenderUncoupling = true;

        [ModSettingsGroup("Advanced settings", Section = "Custom Defaults", Collapsible = true,
            Warning = "Warning! Do NOT enable these settings unless you understand what these options mean.")]
        public AdvancedSettings AdvancedSettings = new AdvancedSettings();
    }

    public sealed class AdvancedSettings
    {
        [ModSetting("Enable \"Then perform cut\" after coupling by default")]
        public bool ShowPostCouplingCutByDefault = false;

        [ModSetting("Enable \"Then uncouple\" by default")]
        public bool EnableThenUncoupleByDefault = false;

        [ModSetting("Show waypoint save data debug options")]
        public bool EnableSaveDataDebugOptions = false;
    }
}
