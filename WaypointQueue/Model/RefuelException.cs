using System;

namespace WaypointQueue.Model
{
    internal class RefuelException : WaypointProcessingException
    {
        public RefuelException(string message, ManagedWaypoint waypoint) : base(message, waypoint) { }

        public RefuelException(string message, ManagedWaypoint waypoint, Exception innerException) : base(message, waypoint, innerException) { }
    }
}
