using System;

namespace WaypointQueue.Model
{
    internal class WaypointProcessingException : Exception
    {
        public string WaypointId { get; set; }
        public string LocomotiveIdent { get; set; }

        public WaypointProcessingException(string message) : base(message) { }

        public WaypointProcessingException(string message, ManagedWaypoint waypoint) : base(message)
        {
            LocomotiveIdent = waypoint.Locomotive.Ident.ToString();
            WaypointId = waypoint.Id;
        }

        public WaypointProcessingException(string message, ManagedWaypoint waypoint, Exception innerException) : base(message, innerException)
        {
            LocomotiveIdent = waypoint.Locomotive.Ident.ToString();
            WaypointId = waypoint.Id;
        }
    }
}
