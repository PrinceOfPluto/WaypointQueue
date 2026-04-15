using KeyValue.Runtime;
using System.Collections.Generic;
using System.Linq;
using WaypointQueue.Model;

namespace WaypointQueue
{
    public class RouteAssignment(string locoId, string routeId, bool loop) : IStorableProperty
    {
        public string LocoId = locoId;
        public string RouteId = routeId;
        public bool Loop = loop;

        public Value ToPropertyValue()
        {
            var dictionary = new Dictionary<string, Value>
            {
                [ValueKeys.LocoId] = Value.String(LocoId),
                [ValueKeys.RouteId] = Value.String(RouteId),
                [ValueKeys.Loop] = Value.Bool(Loop),
            };

            return Value.Dictionary(dictionary.ToDictionary(k => k.Key, v => v.Value));
        }

        public static RouteAssignment FromPropertyValue(Value value)
        {
            if (value.IsNull || value.Type != ValueType.Dictionary) { return null; }

            var dict = value.DictionaryValue;

            return new RouteAssignment(dict[ValueKeys.LocoId], dict[ValueKeys.RouteId], dict[ValueKeys.Loop]);
        }

        private static class ValueKeys
        {
            internal static string LocoId = "loco_id";
            internal static string RouteId = "route_id";
            internal static string Loop = "loop";
        }
    }
}
