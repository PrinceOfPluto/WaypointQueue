using Game.AccessControl;
using Game.Messages;
using MessagePack;

namespace WaypointQueue.State.Messages
{
    [MinimumAccessLevel(AccessLevel.Crew)]
    [MessagePackObject(false)]
    public struct UpdateLocoQueueMessage(string locomotiveId, LocoWaypointState state) : IGameMessage
    {
        [Key(0)]
        public string LocomotiveId { get; set; } = locomotiveId;
        [Key(1)]
        public LocoWaypointState State { get; set; } = state;
    }
}
