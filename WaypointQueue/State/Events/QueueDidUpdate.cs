namespace WaypointQueue.State.Events
{
    public readonly struct QueueDidUpdate(string locomotiveId)
    {
        public readonly string LocomotiveId = locomotiveId;
    }
}
