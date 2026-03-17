using Game.AccessControl;
using Game.Messages;
using MessagePack;

namespace WaypointQueue.State.Messages
{
    [MinimumAccessLevel(AccessLevel.Crew)]
    [MessagePackObject(false)]
    public struct RemoveWaypointForQueueMessage(string waypointId, string locomotiveId) : IGameMessage
    {
        [Key(0)]
        public string WaypointId { get; set; } = waypointId;
        [Key(1)]
        public string LocomotiveId { get; set; } = locomotiveId;

    }
}
