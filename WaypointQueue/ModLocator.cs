using WaypointQueue.Settings;

namespace WaypointQueue
{
    internal static class ModLocator
    {
        public static ServiceProvider ServiceProvider { get; set; }
        public static Loader Loader { get; set; }
        public static WaypointQueueController QueueController { get; set; }
        public static WaypointQueueSettings Settings { get; set; }
        public static string ModDirectory => Loader.ModContext.ModDirectory;
    }
}
