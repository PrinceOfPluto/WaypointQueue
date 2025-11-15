using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace WaypointQueue
{
    [Serializable]
    public class RouteDefinition
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "New Route";
        public List<ManagedWaypoint> Waypoints { get; set; } = new List<ManagedWaypoint>();
    }
}
