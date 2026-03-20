using MessagePack;
using System;
using System.Collections.Generic;

namespace WaypointQueue
{
    [Serializable]
    [MessagePackObject(false)]
    public class RouteDefinition
    {
        [Key(0)]
        public string Id { get; set; } = Guid.NewGuid().ToString();
        [Key(1)]
        public string Name { get; set; } = "New Route";
        [Key(2)]
        public List<ManagedWaypoint> Waypoints { get; set; } = [];
        [Key(3)]
        public string Section { get; set; } = "Unsorted section";
        [Key(4)]
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        [Key(5)]
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
