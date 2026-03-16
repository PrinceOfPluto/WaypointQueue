using Game.State;
using System.Collections.Generic;
using WaypointQueue.State;
using WaypointQueue.State.Messages;

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
            if (string.IsNullOrEmpty(locoId)) return;

            if (routeId == null)
            {
                StateManager.ApplyLocal(new RemoveRouteAssignmentMessage(locoId));
                return;
            }

            var routeAssignment = new RouteAssignment(locoId, routeId, loop);
            StateManager.ApplyLocal(new UpdateRouteAssignmentMessage(routeAssignment));
        }

        public static void Remove(string locoId)
        {
            if (string.IsNullOrEmpty(locoId)) return;
            StateManager.ApplyLocal(new RemoveRouteAssignmentMessage(locoId));
        }
    }
}
