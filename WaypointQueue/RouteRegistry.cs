using System;
using System.Collections.Generic;
using WaypointQueue.State;
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
            ModStateManager.Shared.SaveRoute(route);
            return route;
        }

        public static void Remove(string routeId)
        {
            ModStateManager.Shared.RemoveRoute(routeId);
        }

        public static void Rename(RouteDefinition route, string newName)
        {
            if (route == null) return;
            newName = (newName ?? "").Trim();
            if (newName.Length == 0 || newName == route.Name) return;

            route.Name = newName.Trim();

            ModStateManager.Shared.SaveRoute(route);
        }

        public static void RenameSection(RouteDefinition route, string newSection)
        {
            if (route == null) return;
            newSection = (newSection ?? "").Trim();
            if (newSection.Length == 0 || newSection == route.Section) return;

            route.Section = newSection.Trim();

            ModStateManager.Shared.SaveRoute(route);
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
            ModStateManager.Shared.SaveRoute(route);
        }

        public static void InsertWaypointInRoute(ManagedWaypoint waypoint, string beforeWaypointId, string routeId)
        {
            if (!Routes.ContainsKey(routeId))
            {
                return;
            }

            RouteDefinition route = Routes[routeId];
            int beforeWaypointIndex = route.Waypoints?.FindIndex(w => w.Id == beforeWaypointId) ?? 0;
            route.Waypoints.Insert(beforeWaypointIndex, waypoint);

            ModStateManager.Shared.SaveRoute(route);
        }

        public static void AddWaypointToRoute(ManagedWaypoint waypoint, string routeId)
        {
            if (!Routes.ContainsKey(routeId))
            {
                return;
            }

            RouteDefinition route = Routes[routeId];
            route.Waypoints.Add(waypoint);
            ModStateManager.Shared.SaveRoute(route);
        }
    }
}
