using MessagePack;
using System;

namespace WaypointQueue
{
    [Serializable]
    [MessagePackObject(false)]
    public class RouteAssignment(string locoId, string routeId, bool loop)
    {
        [Key(0)]
        public string LocoId = locoId;
        [Key(1)]
        public string RouteId = routeId;
        [Key(2)]
        public bool Loop = loop;
    }
}
