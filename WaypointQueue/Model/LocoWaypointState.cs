using KeyValue.Runtime;
using Model;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using WaypointQueue.Model;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    public class LocoWaypointState : IStorableProperty
    {
        [JsonProperty]
        public string LocomotiveId { get; private set; }
        public List<ManagedWaypoint> Waypoints { get; set; } = [];
        public ManagedWaypoint UnresolvedWaypoint { get; set; }

        [JsonIgnore]
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
                Waypoints.SequenceEqual(other.Waypoints);
        }

        public override int GetHashCode()
        {
            return LocomotiveId.GetHashCode();
        }

        public Value ToPropertyValue()
        {
            Value waypointsValue = Value.Array([.. Waypoints.Select(w => w.ToPropertyValue())]);

            Value unresolvedWaypointValue = UnresolvedWaypoint == null ? Value.Null() : UnresolvedWaypoint.ToPropertyValue();

            var dictionary = new Dictionary<string, Value>
            {
                [ValueKeys.LocomotiveId] = Value.String(LocomotiveId),
                [ValueKeys.Waypoints] = waypointsValue,
                [ValueKeys.UnresolvedWaypoint] = unresolvedWaypointValue,
            };

            return Value.Dictionary(dictionary.ToDictionary(k => k.Key, v => v.Value));
        }

        public static LocoWaypointState FromPropertyValue(Value value)
        {
            if (value.IsNull || value.Type != KeyValue.Runtime.ValueType.Dictionary) { return null; }

            var dict = value.DictionaryValue;

            string locoId = dict[ValueKeys.LocomotiveId].StringValue;
            List<ManagedWaypoint> waypoints = dict[ValueKeys.Waypoints].ArrayValue.ToList().Select(v => ManagedWaypoint.FromPropertyValue(v)).ToList();
            ManagedWaypoint unresolved = ManagedWaypoint.FromPropertyValue(dict[ValueKeys.UnresolvedWaypoint]);

            return new LocoWaypointState(locoId, waypoints, unresolved);
        }

        private static class ValueKeys
        {
            internal static readonly string LocomotiveId = "locomotive_id";
            internal static readonly string Waypoints = "waypoints";
            internal static readonly string UnresolvedWaypoint = "unresolved_waypoint";
        }
    }
}
