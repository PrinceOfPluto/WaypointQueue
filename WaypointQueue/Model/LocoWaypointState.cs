using MessagePack;
using Model;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    [MessagePackObject(false)]
    public class LocoWaypointState
    {
        [JsonProperty]
        [Key(0)]
        public string LocomotiveId { get; private set; }
        [Key(1)]
        public List<ManagedWaypoint> Waypoints { get; set; } = [];
        [Key(2)]
        public ManagedWaypoint UnresolvedWaypoint { get; set; }
        [Key(3)]
        public bool PeriodicReroute { get; set; } = false;

        [JsonIgnore]
        [IgnoreMember]
        public BaseLocomotive Locomotive
        {
            get
            {
                TrainController.Shared.TryGetCarForId(LocomotiveId, out Car carLoco);
                if (carLoco is BaseLocomotive loco)
                {
                    return loco;
                }
                Loader.LogError($"Failed to resolve locomotive {LocomotiveId} for waypoint state entry");
                return null;
            }
        }

        public LocoWaypointState(string locoId)
        {
            LocomotiveId = locoId;
            Waypoints = [];
        }

        [JsonConstructor]
        public LocoWaypointState(string locomotiveId, List<ManagedWaypoint> waypoints, ManagedWaypoint unresolvedWaypoint)
        {
            LocomotiveId = locomotiveId;
            Waypoints = waypoints ?? [];
            UnresolvedWaypoint = unresolvedWaypoint;
        }

        public override bool Equals(object obj)
        {
            if (obj == null || GetType() != obj.GetType())
            {
                return false;
            }

            var other = (LocoWaypointState)obj;

            return LocomotiveId == other.LocomotiveId &&
                UnresolvedWaypoint.Equals(other.UnresolvedWaypoint) &&
                Waypoints.SequenceEqual(other.Waypoints) &&
                PeriodicReroute == other.PeriodicReroute;
        }

        public override int GetHashCode()
        {
            return LocomotiveId.GetHashCode();
        }
    }
}
