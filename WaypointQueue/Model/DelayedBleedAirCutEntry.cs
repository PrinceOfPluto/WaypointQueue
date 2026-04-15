using KeyValue.Runtime;
using System.Collections.Generic;
using System.Linq;

namespace WaypointQueue.Model
{
    internal struct DelayedBleedAirCutEntry(double delayBleedUntilGameTotalSeconds, List<string> carIds) : IStorableProperty
    {
        public double DelayBleedUntilGameTotalSeconds = delayBleedUntilGameTotalSeconds;
        public List<string> CarIds = carIds;

        private static readonly string KeyDelayBleedUntilGameTotalSeconds = "delay_bleed_until_game_total_seconds";
        private static readonly string KeyCarIds = "car_ids";

        public Value ToPropertyValue()
        {
            var dictionary = new Dictionary<string, Value>
            {
                [KeyDelayBleedUntilGameTotalSeconds] = Value.String(DelayBleedUntilGameTotalSeconds.ToString()),
                [KeyCarIds] = Value.Array(CarIds.Select(c => Value.String(c)).ToList())
            };

            return Value.Dictionary(dictionary.ToDictionary(k => k.Key, v => v.Value));
        }

        public static DelayedBleedAirCutEntry FromPropertyValue(Value value)
        {
            if (value.IsNull || value.Type != ValueType.Dictionary) { return new DelayedBleedAirCutEntry(0, []); }

            return new DelayedBleedAirCutEntry(System.Convert.ToDouble(value.DictionaryValue[KeyDelayBleedUntilGameTotalSeconds].StringValue), value.DictionaryValue[KeyCarIds].ArrayValue.Select(v => v.StringValue).ToList());
        }
    }
}
