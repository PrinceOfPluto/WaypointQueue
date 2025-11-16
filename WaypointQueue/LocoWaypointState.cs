using Model;
using Newtonsoft.Json;
using System;
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

        public void Load()
        {
            if (TrainController.Shared.TryGetCarForId(LocomotiveId, out Car locomotive))
            {
                Loader.LogDebug($"Loaded locomotive {locomotive.Ident} for LocoWaypointState");
                Locomotive = locomotive;
            }
            else
            {
                throw new InvalidOperationException($"Could not find car for {LocomotiveId}");
            }
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
