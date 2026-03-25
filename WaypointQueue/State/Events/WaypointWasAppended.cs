namespace WaypointQueue.State.Events
{
    public readonly struct WaypointWasAppended(string waypointId, string locomotiveId, string routeId)
    {
        public readonly string WaypointId = waypointId;
        public readonly string LocomotiveId = locomotiveId;
        public readonly string RouteId = routeId;
    }
}
