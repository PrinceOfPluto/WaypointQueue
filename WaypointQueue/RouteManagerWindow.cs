using System.Collections.Generic;
using System.Linq;
using UI;
using UI.Builder;
using UI.Common;
using UnityEngine;
using UnityEngine.UI;
using WaypointQueue.UUM;


namespace WaypointQueue
{
    [RequireComponent(typeof(Window))]
    public class RouteManagerWindow : WindowBase
    {
        public override string WindowIdentifier => "RouteManagerPanel";
        public override string Title => "Routes";
        public override Vector2Int DefaultSize => new Vector2Int(800, 560);
        public override Window.Position DefaultPosition => Window.Position.Center;
        public override Window.Sizing Sizing => Window.Sizing.Resizable(DefaultSize, new Vector2Int(1100, Screen.height));

        public static RouteManagerWindow Shared => WindowManager.Shared.GetWindow<RouteManagerWindow>();

        private readonly UIState<string> _selectedRouteId = new UIState<string>(null);
        private readonly List<float> _scrollPositions = new List<float>();


        private void OnEnable()
        {
            if (string.IsNullOrEmpty(_selectedRouteId.Value) && RouteRegistry.Routes.Count > 0)
                _selectedRouteId.Value = RouteRegistry.Routes[0].Id;
        }
        public void Show()
        {
            Loader.LogDebug("Showing RouteManager window");
            Rebuild();

            WindowPersistence.SetInitialPositionSize(
                Window, WindowIdentifier, DefaultSize, DefaultPosition, Sizing
            );
            Window.ShowWindow();
        }

        public void Hide()
        {
            Window.CloseWindow();
        }

        public void Toggle()
        {
            if (Window.IsShown) Hide();
            else Show();
        }

        private void RebuildWithScrolls()
        {
            var scrollRects = Window.contentRectTransform.GetComponentsInChildren<ScrollRect>(true);

            _scrollPositions.Clear();
            foreach (var sr in scrollRects)
            {
                _scrollPositions.Add(sr.verticalNormalizedPosition);
            }


            Rebuild();


            Invoke(nameof(RestoreScrolls), 0f);
        }

        private void RestoreScrolls()
        {
            var scrollRects = Window.contentRectTransform.GetComponentsInChildren<ScrollRect>(true);

            var count = Mathf.Min(scrollRects.Length, _scrollPositions.Count);
            for (int i = 0; i < count; i++)
            {

                scrollRects[i].verticalNormalizedPosition = Mathf.Clamp01(_scrollPositions[i]);
            }
        }

        public override void Populate(UIPanelBuilder builder)
        {
            builder.Spacing = 0f;
            var items = RouteRegistry.Routes
                .Select(r => new UIPanelBuilder.ListItem<RouteDefinition>(r.Id, r, "Routes", r.Name))
                .ToList();

            builder.AddListDetail<RouteDefinition>(
                items,
                _selectedRouteId,
                (detail, route) =>
                {
                    detail.Spacing = 12f;

                    if (route == null)
                    {
                        detail.AddLabel("Select a route from the list.");
                        detail.AddExpandingVerticalSpacer();
                        return;
                    }
                    detail.AddTitle(route.Name, "");
                    detail.FieldLabelWidth = 110f;
                    detail.AddField("Name", detail.AddInputField(route.Name, newName =>
                    {
                        RouteRegistry.Rename(route, newName);
                        RebuildWithScrolls();
                    }, placeholder: "Route name"));

                    detail.Spacer(8f);
                    detail.ButtonStrip(row =>
                    {
                        row.AddButtonCompact("Replace from Loco", () =>
                        {
                            SetFromSelectedLoco(route);
                        });

                        row.AddButtonCompact("Add from Loco", () =>
                        {
                            AppendFromSelectedLoco(route);
                        });

                        row.Spacer();

                        row.AddButtonCompact("Copy to Loco", () => AssignToSelectedLoco(route, append: true));
                    });

                    detail.Spacer(12f);
                    detail.VScrollView(scroll =>
                    {
                        if (route.Waypoints == null || route.Waypoints.Count == 0)
                        {
                            scroll.AddLabel("<i>Route has no waypoints.</i>").HorizontalTextAlignment(TMPro.HorizontalAlignmentOptions.Center);
                            return;
                        }

                        scroll.HStack(head =>
                        {
                            head.AddLabel($"Showing waypoints for route: {route.Name}");
                            head.Spacer();

                            var ops = new List<DropdownMenu.RowData>
                            {
                                new DropdownMenu.RowData("Refresh", "Refreshes the panel"),
                                new DropdownMenu.RowData("Delete all", "")
                            };
                            head.AddOptionsDropdown(ops, (int v) =>
                            {
                                switch (v)
                                {
                                    case 0:
                                        RebuildWithScrolls();
                                        break;
                                    case 1:
                                        PresentDeleteAllModal(route);
                                        break;
                                }
                            });
                            head.Spacer(8f);
                        });

                        scroll.Spacer(20f);
                        for (int i = 0; i < route.Waypoints.Count; i++)
                        {
                            var mw = route.Waypoints[i];
                            BuildRouteWaypointSection(route, mw, i + 1, scroll);
                            scroll.Spacer(20f);
                        }
                    });

                }
            );

            builder.Spacer(12f);

            builder.ButtonStrip(row =>
            {
                row.AddButton("New Route", () =>
                {
                    var r = RouteRegistry.CreateNewRoute();
                    _selectedRouteId.Value = r.Id;
                    RebuildWithScrolls();
                });

                row.AddButton("Delete", () =>
                {
                    if (string.IsNullOrEmpty(_selectedRouteId.Value)) return;
                    RouteRegistry.Remove(_selectedRouteId.Value);
                    _selectedRouteId.Value = RouteRegistry.Routes.FirstOrDefault()?.Id;
                    RebuildWithScrolls();
                });
            });
        }

        private void PresentDeleteAllModal(RouteDefinition route)
        {
            ModalAlertController.Present($"Delete all waypoints for route '{route.Name}'?", "This cannot be undone.",
                new (bool, string)[] { (true, "Delete"), (false, "Cancel") },
                (bool b) =>
                {
                    if (b)
                    {
                        route.Waypoints.Clear();
                        RebuildWithScrolls();
                    }
                });
        }

        private void BuildRouteWaypointSection(RouteDefinition route, ManagedWaypoint mw, int number, UIPanelBuilder builder)
        {
            if (!mw.IsValid())
            {
                return;
            }

            WaypointWindow.Shared.BuildWaypointSection(
             mw,
             number,
             builder,
             onWaypointChange: (ManagedWaypoint waypoint) =>
             {
                 RebuildWithScrolls();
             },
             onWaypointDelete: (ManagedWaypoint waypoint) =>
             {
                 route.Waypoints.Remove(waypoint);
                 RebuildWithScrolls();
             },
             isRouteWindow: true);
        }

        private void AssignToSelectedLoco(RouteDefinition route, bool append)
        {
            var loco = TrainController.Shared.SelectedLocomotive;
            WaypointQueueController.Shared.AddWaypointsFromRoute(loco, route, append);
        }

        private void SetFromSelectedLoco(RouteDefinition route)
        {
            var loco = TrainController.Shared.SelectedLocomotive;
            if (loco == null) return;

            var list = WaypointQueueController.Shared.GetWaypointList(loco);

            route.Waypoints.Clear();

            if (list != null)
            {
                foreach (var mw in list)
                {
                    if (mw.IsValid())
                    {
                        ManagedWaypoint copy = mw.CopyForRoute();
                        route.Waypoints.Add(copy);
                    }
                }
            }

            RebuildWithScrolls();
        }

        private void AppendFromSelectedLoco(RouteDefinition route)
        {
            var loco = TrainController.Shared.SelectedLocomotive;
            if (loco == null || route == null) return;

            var list = WaypointQueueController.Shared.GetWaypointList(loco);
            if (list == null || list.Count == 0) return;

            foreach (var mw in list)
            {
                if (mw.IsValid())
                {
                    ManagedWaypoint copy = mw.CopyForRoute();
                    route.Waypoints.Add(copy);
                }
            }

            RebuildWithScrolls();
        }
    }
}
