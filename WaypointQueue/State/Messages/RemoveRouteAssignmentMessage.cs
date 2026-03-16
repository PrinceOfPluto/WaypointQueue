using Game.AccessControl;
using Game.Messages;
using MessagePack;

namespace WaypointQueue.State.Messages
{
    [MinimumAccessLevel(AccessLevel.Crew)]
    [MessagePackObject(false)]
    public struct RemoveRouteAssignmentMessage(string locomotiveId) : IGameMessage
    {
        [Key(0)]
        public string LocomotiveId { get; set; } = locomotiveId;
    }
}
