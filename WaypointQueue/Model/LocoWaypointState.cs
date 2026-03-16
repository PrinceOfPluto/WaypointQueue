using MessagePack;
using Model;
using Newtonsoft.Json;
using System.Collections.Generic;
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
    }
}
