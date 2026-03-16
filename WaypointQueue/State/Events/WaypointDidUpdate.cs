namespace WaypointQueue.State.Events
{
    public readonly struct WaypointDidUpdate(string waypointId, string locomotiveId)
    {
        public readonly string WaypointId = waypointId;
        public readonly string LocomotiveId = locomotiveId;
    }
}
