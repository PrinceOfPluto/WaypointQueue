using Model;
using Newtonsoft.Json;
using System.Collections.Generic;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    public class LocoWaypointState
    {
        [JsonProperty]
        public string LocomotiveId { get; private set; }
        public List<ManagedWaypoint> Waypoints { get; set; }
        public ManagedWaypoint UnresolvedWaypoint { get; set; }

        [JsonIgnore]
        public Car Locomotive { get; private set; }

        public bool HasAnyWaypoints()
        {
            return Waypoints != null && Waypoints.Count > 0;
        }

        public bool TryResolveLocomotive(out Car loco)
        {
            // loco is null if false
            if (TrainController.Shared.TryGetCarForId(LocomotiveId, out loco))
            {
                Loader.LogDebug($"Loaded locomotive {loco.Ident} for LocoWaypointState");
                Locomotive = loco;
            }
            else
            {
                Loader.LogError($"Failed to resolve locomotive {LocomotiveId} for waypoint state entry");
            }
            return loco != null;
        }

        public LocoWaypointState(Car loco)
        {
            LocomotiveId = loco.id;
            Locomotive = loco;
            Waypoints = new List<ManagedWaypoint>();
        }

        [JsonConstructor]
        public LocoWaypointState(string locomotiveId, List<ManagedWaypoint> waypoints, ManagedWaypoint unresolvedWaypoint)
        {
            LocomotiveId = locomotiveId;
            Waypoints = waypoints;
            UnresolvedWaypoint = unresolvedWaypoint;
        }
    }
}
