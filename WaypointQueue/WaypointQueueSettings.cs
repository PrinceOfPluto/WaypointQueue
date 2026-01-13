using UnityEngine;
using UnityModManagerNet;

namespace WaypointQueue
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

    public class WaypointQueueSettings : UnityModManager.ModSettings, IDrawable
    {
        [Header("Keybindings")]
        [Draw("Activate append waypoint mode", Tooltip = "Setting a waypoint while this key is pressed will add it to the end of the locomotive's waypoint queue.")] public KeyBinding queuedWaypointModeKey = new KeyBinding() { keyCode = KeyCode.LeftControl };
        [Draw("Activate replace waypoint mode", Tooltip = "Setting a waypoint while this key is pressed will replace the current waypoint but keep the rest of the existing waypoint queue.")] public KeyBinding replaceWaypointModeKey = new KeyBinding() { keyCode = KeyCode.LeftAlt };
        [Draw("Activate insert next waypoint mode", Tooltip = "Setting a waypoint while this key is pressed will insert it in the queue directly after the current waypoint.")] public KeyBinding insertNextWaypointModeKey = new KeyBinding() { keyCode = KeyCode.LeftShift };
        [Draw("Toggle Waypoints window")] public KeyBinding toggleWaypointPanelKey = new KeyBinding() { modifiers = 2, keyCode = KeyCode.G };
        [Draw("Toggle Route Manager window")] public KeyBinding toggleRoutesPanelKey = new KeyBinding() { modifiers = 1, keyCode = KeyCode.Z };

        [Header("UI")]
        [Draw("Use compact layout")] public bool UseCompactLayout = true;
        [Draw("Show time info in dropdown for timetable train symbol")] public bool ShowTimeInTrainSymbolDropdown = true;
        [Draw("Enable post-coupling cut by default")] public bool ShowPostCouplingCutByDefault = false;
        [Draw("Enable \"Then uncouple\" by default")] public bool EnableThenUncoupleByDefault = false;

        [Header("Coupling settings")]
        [Draw("Connect air by default when coupling")] public bool ConnectAirByDefault = true;
        [Draw("Release handbrakes by default when coupling")] public bool ReleaseHandbrakesByDefault = true;
        [Draw("Nearby coupling search distance in car lengths", DrawType.Slider, Precision = 0, Min = 1, Max = 100, Tooltip = "Distance to search for nearby coupling. Measured in car lengths of about 40 ft each")] public float NearbyCouplingSearchDistanceInCarLengths = 10;
        [Draw("Default coupling search mode", DrawType.ToggleGroup)] public CoupleSearchModeDefaultOptions DefaultCouplingSearchMode = CoupleSearchModeDefaultOptions.BySpecificCar;

        [Header("Uncoupling settings")]
        [Draw("Bleed air cylinders by default when uncoupling")] public bool BleedAirByDefault = true;
        [Draw("Apply handbrakes by default when uncoupling")] public bool ApplyHandbrakesByDefault = true;
        [Draw("Handbrake percentage", Precision = 2, Min = 0, Max = 1, Tooltip = "Handbrakes will be set on this percentage of uncoupled cars")] public float HandbrakePercentOnUncouple = 0.1f;
        [Draw("Handbrake minimum", Precision = 0, Min = 1, Max = 20, Tooltip = "At least this amount of handbrakes will always be set on uncoupled cars regardless of cut length ")] public int MinimumHandbrakesOnUncouple = 2;
        [Draw("Default uncoupling mode", DrawType.ToggleGroup)] public UncoupleModeDefaultOptions DefaultUncouplingMode = UncoupleModeDefaultOptions.ByCount;
        [Draw("Default post-coupling cut mode", DrawType.ToggleGroup)] public PostCoupleCutModeDefaultOptions DefaultPostCouplingCutMode = PostCoupleCutModeDefaultOptions.Pickup;
        [Draw("Default uncoupling mode when performing post-coupling cuts", DrawType.ToggleGroup)] public UncoupleModeDefaultOptions DefaultPostCouplingCutUncouplingMode = UncoupleModeDefaultOptions.ByCount;

        [Header("Custom Defaults")]
        [Draw("Passing speed limit for kicking cars", Min = 0, Max = 45)] public int PassingSpeedForKickingCars = 7;
        [Draw("Kick button unchecks bleeding air and applying handbrakes on uncouple")] public bool UncheckAirAndBrakesForKick = true;
        [Draw("Do not automatically set passing speed limit when not stopping at waypoint")] public bool DoNotLimitPassingSpeedDefault = true;


        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }

        public void OnChange()
        {
        }
    }
}
