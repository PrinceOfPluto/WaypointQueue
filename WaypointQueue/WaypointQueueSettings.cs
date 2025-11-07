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

        [Header("Uncoupling settings")]
        [Draw("Handbrake percentage", Precision = 2, Min = 0, Max = 1, Tooltip = "Handbrakes will be set on this percentage of uncoupled cars")] public float HandbrakePercentOnUncouple = 0.1f;
        [Draw("Handbrake minimum", Precision = 0, Min = 1, Max = 20, Tooltip = "At least this amount of handbrakes will always be set on uncoupled cars regardless of cut length ")] public int MinimumHandbrakesOnUncouple = 2;

        [Draw("Toggle Routes window")]
        public KeyBinding toggleRoutesPanelKey = new KeyBinding() { modifiers = 2, keyCode = KeyCode.Z };

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }

        public void OnChange()
        {
        }
    }
}
