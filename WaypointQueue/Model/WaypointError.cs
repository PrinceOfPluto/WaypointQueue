namespace WaypointQueue.Model
{
    public class WaypointError(string errorType, string message)
    {
        public string ErrorType { get; set; } = errorType;
        public string Message { get; set; } = message;
    }
}
