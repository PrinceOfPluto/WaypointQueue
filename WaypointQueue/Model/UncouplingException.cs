using System;

namespace WaypointQueue.Model
{
    internal class UncouplingException : WaypointProcessingException
    {
        public UncouplingException(string message, ManagedWaypoint waypoint) : base(message, waypoint) { }

        public UncouplingException(string message, ManagedWaypoint waypoint, Exception innerException) : base(message, waypoint, innerException) { }
    }
}
