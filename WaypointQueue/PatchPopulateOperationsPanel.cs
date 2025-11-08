using System.Linq;
using System.Reflection;
using HarmonyLib;
using UI.Builder;
using WaypointQueue;          // RouteRegistry, RouteManagerWindow, RouteAssignmentRegistry
using WaypointQueue.UUM;     // WindowHelper
using Model;                 // Car
using Model.Definition;      // CarArchetypeExtensions.IsLocomotive()
using UI.CarInspector;
using UI.Common;

namespace WaypointQueue
{
    [HarmonyPatch(typeof(CarInspector), "PopulateOperationsPanel")]
    internal static class PatchPopulateOperationsPanel
    {
        static void Postfix(CarInspector __instance, UIPanelBuilder builder, ref Car ___car)
        {
            var car = ___car;
            if (car == null) return;

            // Only for locomotives
            if (!car.Archetype.IsLocomotive()) return;

            var carID = car.id;

            builder.Spacer(2f);

            builder.AddSection("Routes", section =>
            {
                var routes = RouteRegistry.Routes;

                // Build display list with a placeholder at index 0
                var names = new System.Collections.Generic.List<string> { "(select route)" };
                names.AddRange(routes.Select(r => r.Name));

                // Read current assignment from registry
                var (currentRouteId, currentLoop) = RouteAssignmentRegistry.Get(carID);  // :contentReference[oaicite:2]{index=2}

                // Calculate selected index (0 = placeholder)
                int selectedIndex = 0;
                if (!string.IsNullOrEmpty(currentRouteId))
                {
                    int idx = routes.FindIndex(r => r.Id == currentRouteId);
                    if (idx >= 0) selectedIndex = idx + 1; // shift because of placeholder
                }

                // Row: "Route" label, dropdown, and "Show" button inline
                section.HStack(row =>
                {
                    row.AddLabel("Route").Width(75f);

                    var dd = row.AddDropdown(
                        names,
                        selectedIndex,
                        (int newIdx) =>
                        {
                            // Map selection back to routeId (or null)
                            string newRouteId = null;
                            if (newIdx > 0)
                            {
                                int routeIdx = newIdx - 1;
                                if (routeIdx >= 0 && routeIdx < routes.Count)
                                    newRouteId = routes[routeIdx].Id;
                            }

                            // Preserve existing loop flag; write through the registry so it persists
                            var (_, prevLoop) = RouteAssignmentRegistry.Get(carID);
                            RouteAssignmentRegistry.Set(carID, newRouteId, prevLoop);  // :contentReference[oaicite:3]{index=3}

                            // Refresh the UI so the Loop toggle visibility updates immediately
                            section.Rebuild();
                        });
                    dd.Width(240f);

                    row.Spacer(8f);

                    row.AddButton("Show", () =>
                    {
                        var win = Loader.RouteManagerWindow;
                        if (win == null)
                        {
                            WindowHelper.CreateWindow<RouteManagerWindow>(null);
                            win = WindowManager.Shared.GetWindow<RouteManagerWindow>();
                        }
                        win.Show();
                    }).Width(80f);
                });

                section.Spacer(6f);

                // Only show Loop when a route is actually selected
                if (!string.IsNullOrEmpty(currentRouteId))
                {
                    section.HStack(loopRow =>
                    {
                        loopRow.AddToggle(
                            () => RouteAssignmentRegistry.Get(carID).loop,   // live read
                            (bool v) =>
                            {
                                // Preserve current route; update loop; persist via registry
                                var (rid, _) = RouteAssignmentRegistry.Get(carID);
                                RouteAssignmentRegistry.Set(carID, rid, v);   // :contentReference[oaicite:4]{index=4}
                            });
                        loopRow.AddLabel("Loop");
                    });
                }
            });
        }
    }
}
