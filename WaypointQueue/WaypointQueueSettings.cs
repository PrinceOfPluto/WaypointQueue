using UnityEngine;
using UnityModManagerNet;

namespace WaypointQueue
{
    public class WaypointQueueSettings : UnityModManager.ModSettings, IDrawable
    {
        [Header("Keybindings")]
        [Draw("Activate queue waypoint mode")] public KeyBinding queuedWaypointModeKey = new KeyBinding() { keyCode = KeyCode.LeftControl };
        [Draw("Activate replace waypoint mode")] public KeyBinding replaceWaypointModeKey = new KeyBinding() { keyCode = KeyCode.LeftAlt };
        [Draw("Toggle Waypoints window")] public KeyBinding toggleWaypointPanelKey = new KeyBinding() { modifiers = 2, keyCode = KeyCode.G };

        [Header("UI")]
        [Draw("Use compact layout")] public bool UseCompactLayout = true;
        [Draw("Enable tooltips")] public bool EnableTooltips = true;

        [Header("Uncoupling settings")]
        [Draw("Handbrake percentage", Precision = 2, Min = 0, Max = 1, Tooltip = "Handbrakes will be set on this percentage of uncoupled cars")] public float HandbrakePercentOnUncouple = 0.1f;
        [Draw("Handbrake minimum", Precision = 0, Min = 1, Max = 20, Tooltip = "At least this amount of handbrakes will always be set on uncoupled cars regardless of cut length ")] public int MinimumHandbrakesOnUncouple = 2;

        [Header("Custom Defaults")]
        [Draw("Connect air by default when coupling")] public bool ConnectAirByDefault = true;
        [Draw("Release handbrakes by default when coupling")] public bool ReleaseHandbrakesByDefault = true;
        [Draw("Bleed air cylinders by default when uncoupling")] public bool BleedAirByDefault = true;
        [Draw("Apply handbrakes by default when uncoupling")] public bool ApplyHandbrakesByDefault = true;


        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }

        public void OnChange()
        {
        }
    }
}
