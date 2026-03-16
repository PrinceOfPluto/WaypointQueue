using Game.AccessControl;
using Game.Messages;
using MessagePack;

namespace WaypointQueue.State.Messages
{
    [MinimumAccessLevel(AccessLevel.Crew)]
    [MessagePackObject(false)]
    public struct UpdateRouteAssignmentMessage(RouteAssignment routeAssignment) : IGameMessage
    {
        [Key(0)]
        public RouteAssignment RouteAssignment { get; set; } = routeAssignment;
    }
}
