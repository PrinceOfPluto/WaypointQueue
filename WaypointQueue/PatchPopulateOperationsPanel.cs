using HarmonyLib;
using Model;
using Model.Definition;
using System.Linq;
using UI.Builder;
using UI.CarInspector;
using UI.Common;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    [HarmonyPatch(typeof(CarInspector), "PopulateOperationsPanel")]
    internal static class PatchPopulateOperationsPanel
    {
        static void Postfix(CarInspector __instance, UIPanelBuilder builder, ref Car ____car)
        {

            var car = ____car;
            if (car == null) return;

            var carID = car.id;

            if (!car.Archetype.IsLocomotive()) return;

            builder.Spacer(2f);

            builder.AddSection("Routes", section =>
            {
                var routes = RouteRegistry.Routes;


                var names = new System.Collections.Generic.List<string> { "(select route)" };
                names.AddRange(routes.Select(r => r.Name));


                var (currentRouteId, currentLoop) = RouteAssignmentRegistry.Get(carID);


                int selectedIndex = 0;
                if (!string.IsNullOrEmpty(currentRouteId))
                {
                    int idx = routes.FindIndex(r => r.Id == currentRouteId);
                    if (idx >= 0) selectedIndex = idx + 1;
                }


                section.HStack(row =>
                {
                    row.AddLabel("Route").Width(75f);

                    var dd = row.AddDropdown(
                        names,
                        selectedIndex,
                        (int newIdx) =>
                        {

                            string newRouteId = null;
                            if (newIdx > 0)
                            {
                                int routeIdx = newIdx - 1;
                                if (routeIdx >= 0 && routeIdx < routes.Count)
                                    newRouteId = routes[routeIdx].Id;
                            }


                            var (_, prevLoop) = RouteAssignmentRegistry.Get(carID);
                            RouteAssignmentRegistry.Set(carID, newRouteId, prevLoop);


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


                if (!string.IsNullOrEmpty(currentRouteId))
                {
                    section.HStack(loopRow =>
                    {
                        loopRow.AddToggle(
                            () => RouteAssignmentRegistry.Get(carID).loop,
                            (bool v) =>
                            {

                                var (rid, _) = RouteAssignmentRegistry.Get(carID);
                                RouteAssignmentRegistry.Set(carID, rid, v);
                            });
                        loopRow.AddLabel("Loop");
                    });
                }
            });
        }
    }
}
