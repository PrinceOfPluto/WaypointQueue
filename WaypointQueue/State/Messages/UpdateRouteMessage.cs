using Game.AccessControl;
using Game.Messages;
using MessagePack;

namespace WaypointQueue.State.Messages
{
    [MinimumAccessLevel(AccessLevel.Crew)]
    [MessagePackObject(false)]
    public struct UpdateRouteMessage(string routeId, RouteDefinition routeDefinition) : IGameMessage
    {
        [Key(0)]
        public string RouteId { get; set; } = routeId;
        [Key(1)]
        public RouteDefinition RouteDefinition { get; set; } = routeDefinition;
    }
}
