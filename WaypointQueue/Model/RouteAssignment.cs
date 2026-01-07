using System;

namespace WaypointQueue
{
    [Serializable]
    public class RouteAssignment
    {
        public string LocoId;
        public string RouteId;
        public bool Loop;
    }
}
