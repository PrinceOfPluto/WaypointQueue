using Game.AccessControl;
using Game.Messages;
using MessagePack;

namespace WaypointQueue.State.Messages
{
    [MinimumAccessLevel(AccessLevel.Crew)]
    [MessagePackObject(false)]
    public struct RemoveRouteMessage(string routeId) : IGameMessage
    {
        [Key(0)]
        public string RouteId { get; set; } = routeId;
    }
}
