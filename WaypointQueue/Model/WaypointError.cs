using MessagePack;

namespace WaypointQueue.Model
{
    [MessagePackObject]
    public class WaypointError(string errorType, string message)
    {
        [Key(0)]
        public string ErrorType { get; set; } = errorType;
        [Key(1)]
        public string Message { get; set; } = message;
    }
}
