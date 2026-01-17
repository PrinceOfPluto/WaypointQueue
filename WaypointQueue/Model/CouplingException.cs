using System;

namespace WaypointQueue.Model
{
    internal class CouplingException : WaypointProcessingException
    {
        public CouplingException(string message, ManagedWaypoint waypoint) : base(message, waypoint) { }

        public CouplingException(string message, ManagedWaypoint waypoint, Exception innerException) : base(message, waypoint, innerException) { }
    }
}
