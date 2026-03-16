using Game.AccessControl;
using Game.Messages;
using MessagePack;

namespace WaypointQueue.State.Messages
{
    [MinimumAccessLevel(AccessLevel.Crew)]
    [MessagePackObject(false)]
    public struct UpdateWaypointForQueueMessage(string locomotiveId, ManagedWaypoint waypoint) : IGameMessage
    {
        [Key(0)]
        public string LocomotiveId { get; set; } = locomotiveId;
        [Key(1)]
        public ManagedWaypoint Waypoint { get; set; } = waypoint;
    }
}
