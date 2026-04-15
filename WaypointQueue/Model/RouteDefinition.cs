using KeyValue.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using WaypointQueue.Model;

namespace WaypointQueue
{
    public class RouteDefinition : IStorableProperty
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "New Route";
        public List<ManagedWaypoint> Waypoints { get; set; } = [];
        public string Section { get; set; } = "Unsorted section";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        public Value ToPropertyValue()
        {
            var dictionary = new Dictionary<string, Value>
            {
                [ValueKeys.Id] = Value.String(Id),
                [ValueKeys.Name] = Value.String(Name),
                [ValueKeys.Waypoints] = Value.Array([.. Waypoints.Select(w => w.ToPropertyValue())]),
                [ValueKeys.Section] = Value.String(Section),
                [ValueKeys.CreatedAt] = Value.String(CreatedAt.ToString("O")),
                [ValueKeys.UpdatedAt] = Value.String(UpdatedAt.ToString("O")),
            };

            return Value.Dictionary(dictionary.ToDictionary(k => k.Key, v => v.Value));
        }

        public static RouteDefinition FromPropertyValue(Value value)
        {
            if (value.IsNull || value.Type != KeyValue.Runtime.ValueType.Dictionary) { return null; }

            var dict = value.DictionaryValue;

            RouteDefinition route = new RouteDefinition();

            route.Id = dict[ValueKeys.Id].StringValue;
            route.Name = dict[ValueKeys.Name].StringValue;
            route.Waypoints = dict[ValueKeys.Waypoints].ArrayValue.ToList().Select(v => ManagedWaypoint.FromPropertyValue(v)).ToList();
            route.Section = dict[ValueKeys.Section].StringValue;
            route.CreatedAt = DateTime.Parse(dict[ValueKeys.CreatedAt].StringValue, null, System.Globalization.DateTimeStyles.RoundtripKind);
            route.UpdatedAt = DateTime.Parse(dict[ValueKeys.UpdatedAt].StringValue, null, System.Globalization.DateTimeStyles.RoundtripKind);

            return route;
        }

        private static class ValueKeys
        {
            internal static readonly string Id = "id";
            internal static readonly string Name = "name";
            internal static readonly string Waypoints = "waypoints";
            internal static readonly string Section = "section";
            internal static readonly string CreatedAt = "created_at";
            internal static readonly string UpdatedAt = "updated_at";
        }
    }
}
