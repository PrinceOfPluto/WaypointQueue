using Game.State;
using System;
using System.Collections.Generic;
using WaypointQueue.State;
using WaypointQueue.State.Messages;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    public static class RouteRegistry
    {
        public static IReadOnlyDictionary<string, RouteDefinition> Routes => ModStateManager.Shared.Routes;

        public static void LoadWaypointsForRoutes()
        {
            foreach (var route in Routes.Values)
            {
                if (route?.Waypoints == null) continue;

                foreach (var wp in route.Waypoints)
                {
                    try
                    {
                        wp?.LoadForRoute();
                    }
                    catch (Exception ex)
                    {
                        Loader.LogError($"[Routes] Failed to hydrate waypoint {wp?.Id} for route '{route?.Name}': {ex}");
                    }
                }
            }
        }

        public static RouteDefinition GetById(string routeId)
        {
            if (Routes.TryGetValue(routeId, out var route)) return route;
            return null;
        }

        public static RouteDefinition CreateNewRoute()
        {
            var route = new RouteDefinition { Name = $"Route {Routes.Count + 1}" };
            StateManager.ApplyLocal(new UpdateRouteMessage(route.Id, route));
            return route;
        }

        public static void Remove(string routeId)
        {
            StateManager.ApplyLocal(new RemoveRouteMessage(routeId));
        }

        public static void Rename(RouteDefinition route, string newName)
        {
            if (route == null) return;
            newName = (newName ?? "").Trim();
            if (newName.Length == 0 || newName == route.Name) return;

            route.Name = newName.Trim();

            StateManager.ApplyLocal(new UpdateRouteMessage(route.Id, route));
        }

        public static void ReorderWaypointInRoute(RouteDefinition route, ManagedWaypoint waypoint, int newIndex)
        {
            if (route != null && route.Waypoints != null)
            {
                int oldIndex = route.Waypoints.IndexOf(waypoint);
                if (oldIndex < 0) return;

                route.Waypoints.RemoveAt(oldIndex);
                if (newIndex > oldIndex)
                {
                    newIndex--; // the actual index could have shifted due to the removal
                }

                route.Waypoints.Insert(newIndex, waypoint);
            }
            StateManager.ApplyLocal(new UpdateRouteMessage(route.Id, route));
        }
    }
}
