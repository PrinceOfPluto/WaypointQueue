using KeyValue.Runtime;
using System.Collections.Generic;
using System.Linq;

namespace WaypointQueue.Model
{
    public class WaypointError(string errorType, string message)
    {
        public string ErrorType { get; set; } = errorType;
        public string Message { get; set; } = message;

        public Value ToPropertyValue()
        {
            var dictionary = new Dictionary<string, Value>
            {
                ["error_type"] = Value.String(ErrorType),
                ["message"] = Value.String(Message)
            };
            return Value.Dictionary(dictionary.ToDictionary(k => k.Key, v => v.Value));
        }

        public static WaypointError FromPropertyValue(Value value)
        {
            return new WaypointError(value.DictionaryValue["error_type"].StringValue, value.DictionaryValue["message"].StringValue);
        }
    }
}
