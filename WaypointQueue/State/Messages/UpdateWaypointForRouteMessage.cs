using Game.AccessControl;
using Game.Messages;
using MessagePack;

namespace WaypointQueue.State.Messages
{
    [MinimumAccessLevel(AccessLevel.Crew)]
    [MessagePackObject(false)]
    public struct UpdateWaypointForRouteMessage(string routeId, ManagedWaypoint waypoint) : IGameMessage
    {
        [Key(0)]
        public string RouteId { get; set; } = routeId;
        [Key(1)]
        public ManagedWaypoint Waypoint { get; set; } = waypoint;
    }
}
