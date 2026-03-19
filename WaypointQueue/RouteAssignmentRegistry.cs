using System.Collections.Generic;
using WaypointQueue.State;

namespace WaypointQueue
{
    public static class RouteAssignmentRegistry
    {
        public static IReadOnlyDictionary<string, RouteAssignment> RouteAssignments => ModStateManager.Shared.RouteAssignments;

        public static (string routeId, bool loop) Get(string locoId)
        {
            if (string.IsNullOrEmpty(locoId)) return (null, false);
            if (RouteAssignments.TryGetValue(locoId, out var a)) return (a.RouteId, a.Loop);
            return (null, false);
        }

        public static void Set(string locoId, string routeId, bool loop)
        {
            ModStateManager.Shared.SaveRouteAssignment(new RouteAssignment(locoId, routeId, loop));
        }

        public static void Remove(string locoId)
        {
            ModStateManager.Shared.RemoveRouteAssignment(locoId);
        }
    }
}
