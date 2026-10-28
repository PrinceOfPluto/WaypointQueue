using System.IO;
using UnityEngine;
using WaypointQueue.Model;

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

    [System.Serializable]
    public class WaypointQueueSettings
    {
        public int Version { get; set; } = 2;

        // Setting a waypoint while this key is pressed will add it to the end of the locomotive's waypoint queue
        public KeyBinding queuedWaypointModeKey = new KeyBinding() { keyCode = KeyCode.None };

        // Setting a waypoint while this key is pressed will replace the current waypoint but keep the rest of the existing waypoint queue
        public KeyBinding replaceWaypointModeKey = new KeyBinding() { keyCode = KeyCode.None };

        // Setting a waypoint while this key is pressed will insert it in the queue directly after the current waypoint
        public KeyBinding insertNextWaypointModeKey = new KeyBinding() { keyCode = KeyCode.None };

        // Setting a waypoint while this key is pressed will create a waypoint with couple to nearest enabled
        public KeyBinding CoupleToNearestShortcutHotkey = new() { keyCode = KeyCode.LeftControl };

        // Setting a waypoint while this key is pressed will create a waypoint with uncoupling enabled
        public KeyBinding UncoupleShortcutHotkey = new() { keyCode = KeyCode.LeftAlt };

        // Setting a waypoint while this key is pressed will create a waypoint with kicking enabled
        public KeyBinding KickingShortcutHotkey = new() { keyCode = KeyCode.LeftShift };

        public KeyBinding ToggleWaypointPanelKey = new KeyBinding() { modifiers = 2, keyCode = KeyCode.G };

        public KeyBinding ToggleRoutesPanelKey = new KeyBinding() { modifiers = 1, keyCode = KeyCode.Z };

        public bool MakeAppendDefaultWithoutHotkeybinding = true;

        // Show time info in dropdown for timetable train symbol
        public bool ShowTimeInTrainSymbolDropdown = true;

        // Connect air by default when coupling
        public bool ConnectAirByDefault = true;

        // Release handbrakes by default when coupling
        public bool ReleaseHandbrakesByDefault = true;

        // Nearby coupling search distance in car lengths
        // DrawType.Slider, Precision = 0, Min = 1, Max = 100
        // Distance to search for nearby coupling. Measured in car lengths of about 40 ft each
        public float NearbyCouplingSearchDistanceInCarLengths = 10;

        public CoupleSearchModeDefaultOptions DefaultCouplingSearchMode = CoupleSearchModeDefaultOptions.BySpecificCar;

        // Bleed air cylinders by default when uncoupling
        public bool BleedAirByDefault = true;

        // Leave anglecock open by default after cutting cars (do not bottle air)
        public bool DoNotBottleAir = false;

        //Apply handbrakes by default when uncoupling
        public bool ApplyHandbrakesByDefault = true;

        // Max seconds to wait for train to fully stop before uncoupling
        // DrawType.Slider, Precision = 0, Min = 5, Max = 120
        // Trains will wait to uncouple until the consist is considered at fully rest or until this many seconds have passed, whichever happens sooner
        // If you are encountering trains unexpectedly re-coupling after being uncoupled, it is recommended to increase this timeout value
        public float WaitBeforeCuttingTimeout = 15f;

        // Handbrake percentage
        // DrawType.Slider, Precision = 2, Min = 0, Max = 1
        // Handbrakes will be set on this percentage of uncoupled cars
        public float HandbrakePercentOnUncouple = 0.1f;

        // Handbrake minimum
        // At least this amount of handbrakes will always be set on uncoupled cars regardless of cut length
        public int MinimumHandbrakesOnUncouple = 2;

        public UncoupleModeDefaultOptions DefaultUncouplingMode = UncoupleModeDefaultOptions.ByCount;

        public PostCoupleCutModeDefaultOptions DefaultPostCouplingCutMode = PostCoupleCutModeDefaultOptions.Pickup;

        public UncoupleModeDefaultOptions DefaultPostCouplingCutUncouplingMode = UncoupleModeDefaultOptions.ByCount;

        public int PassingSpeedForKickingCars = 7;

        // Kick button unchecks bleeding air and applying handbrakes on uncouple
        public bool UncheckAirAndBrakesForKick = true;

        // Do not automatically set passing speed limit when not stopping at waypoint
        public bool DoNotLimitPassingSpeedDefault = true;

        // Prevent tenders being uncoupled from locomotive
        public bool PreventTenderUncoupling = true;

        public static WaypointQueueSettings Load(string path)
        {
            if (File.Exists(path)) {
                try
                {
                    string json = File.ReadAllText(path);
                    return JsonUtility.FromJson<WaypointQueueSettings>(json);
                }
                catch (System.Exception)
                {
                }
            }
            return new WaypointQueueSettings();
        }

        public void Save(string path)
        {
            string json = JsonUtility.ToJson(this, prettyPrint: true);
            File.WriteAllText(path, json);
        }
    }
}
