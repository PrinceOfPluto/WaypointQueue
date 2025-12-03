using UnityEngine;
using UnityModManagerNet;

namespace WaypointQueue
{
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

        [Header("Coupling settings")]
        [Draw("Nearby coupling search radius", DrawType.Slider, Precision = 0, Min = 1, Max = 500, Tooltip = "Radius to search for nearby coupling")] public float NearbyCouplingSearchRadius = 50;

        [Header("Uncoupling settings")]
        [Draw("Handbrake percentage", Precision = 2, Min = 0, Max = 1, Tooltip = "Handbrakes will be set on this percentage of uncoupled cars")] public float HandbrakePercentOnUncouple = 0.1f;
        [Draw("Handbrake minimum", Precision = 0, Min = 1, Max = 20, Tooltip = "At least this amount of handbrakes will always be set on uncoupled cars regardless of cut length ")] public int MinimumHandbrakesOnUncouple = 2;

        [Header("Custom Defaults")]
        [Draw("Connect air by default when coupling")] public bool ConnectAirByDefault = true;
        [Draw("Release handbrakes by default when coupling")] public bool ReleaseHandbrakesByDefault = true;
        [Draw("Bleed air cylinders by default when uncoupling")] public bool BleedAirByDefault = true;
        [Draw("Apply handbrakes by default when uncoupling")] public bool ApplyHandbrakesByDefault = true;
        [Draw("Show post-coupling cut options by default")] public bool ShowPostCouplingCutByDefault = false;
        [Draw("Show time info in dropdown for timetable train symbol")] public bool ShowTimeInTrainSymbolDropdown = true;
        [Draw("Passing speed limit for kicking cars", Min = 0, Max = 45)] public int PassingSpeedForKickingCars = 7;


        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }

        public void OnChange()
        {
        }
    }
}
