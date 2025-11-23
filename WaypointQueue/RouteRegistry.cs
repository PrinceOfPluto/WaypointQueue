using System;
using System.Collections.Generic;
using System.Linq;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    public static class RouteRegistry
    {
        public static List<RouteDefinition> Routes { get; private set; } = new List<RouteDefinition>();

        public static event Action OnChanged;

        private static void RaiseChanged()
        {
            OnChanged?.Invoke();
        }
        public static void LoadWaypointsForRoutes()
        {
            foreach (var route in Routes)
            {
                if (route?.Waypoints == null) continue;

                foreach (var wp in route.Waypoints)
                {
                    try
                    {
                        wp?.Load();
                    }
                    catch (Exception ex)
                    {
                        Loader.Log($"[Routes] Failed to hydrate waypoint {wp?.Id} for route '{route?.Name}': {ex}");
                    }
                }
            }

            RaiseChanged();
        }

        public static void ReplaceAll(List<RouteDefinition> routes)
        {
            Routes = routes ?? new List<RouteDefinition>();
            RaiseChanged();
        }

        public static RouteDefinition GetById(string id) => Routes.FirstOrDefault(r => r.Id == id);

        public static RouteDefinition CreateNewRoute()
        {
            var r = new RouteDefinition { Name = $"Route {Routes.Count + 1}" };
            Routes.Add(r);
            RaiseChanged();
            return r;
        }

        public static void Remove(string id)
        {
            var r = GetById(id);
            if (r == null) return;
            Routes.RemoveAll(x => x.Id == id);
            RaiseChanged();
        }

        public static void Rename(RouteDefinition route, string newName)
        {
            if (route == null) return;
            newName = (newName ?? "").Trim();
            if (newName.Length == 0 || newName == route.Name) return;

            route.Name = newName.Trim();

            RaiseChanged();
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
            Loader.LogDebug($"Invoking RaiseChanged in RouteRegistry.ReorderWaypointInRoute");
            RaiseChanged();
        }
    }
}
