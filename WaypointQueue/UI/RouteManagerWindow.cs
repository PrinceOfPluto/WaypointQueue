using GalaSoft.MvvmLight.Messaging;
using System;
using System.Collections.Generic;
using System.Linq;
using UI;
using UI.Builder;
using UI.Common;
using UnityEngine;
using UnityEngine.UI;
using WaypointQueue.State;
using WaypointQueue.State.Events;
using WaypointQueue.UUM;


namespace WaypointQueue.UI
{
    [RequireComponent(typeof(Window))]
    public class RouteManagerWindow : WindowBase
    {
        public override string WindowIdentifier => "RouteManagerPanel";
        public override string Title => "Routes";
        public override Vector2Int DefaultSize => new Vector2Int(800, 560);
        public override Window.Position DefaultPosition => Window.Position.Center;
        public override Window.Sizing Sizing => Window.Sizing.Resizable(DefaultSize, new Vector2Int(1100, Screen.height));

        public static RouteManagerWindow Shared;

        private readonly UIState<string> _selectedRouteId = new UIState<string>(null);
        private readonly List<float> _scrollPositions = new List<float>();

        internal static readonly Dictionary<string, UIPanelBuilder> panelsByWaypointId = [];
        private UIPanelBuilder _scrollViewBuilder;

        private string _searchFilter = string.Empty;
        private SortByField _sortByField = SortByField.Section;
        private SortByDirection _sortByDirection = SortByDirection.Ascending;

        private enum SortByField
        {
            Name,
            Section,
            CreatedAt,
            UpdatedAt
        }

        private enum SortByDirection
        {
            Ascending,
            Descending
        }

        protected void Awake()
        {
            Shared = this;
        }

        protected void OnDestroy()
        {
            Shared = null;
        }

        protected void OnEnable()
        {
            Messenger.Default.Register<WaypointDidUpdate>(this, OnWaypointDidUpdate);
            Messenger.Default.Register<WaypointWasAppended>(this, OnWaypointWasAppended);
            Messenger.Default.Register<RouteDidUpdate>(this, OnRouteDidUpdate);

            if (string.IsNullOrEmpty(_selectedRouteId.Value) && RouteRegistry.Routes.Count > 0)
            {
                _selectedRouteId.Value = RouteRegistry.Routes.Values.First().Id;
            }
        }

        protected void OnDisable()
        {
            Messenger.Default.Unregister(this);
        }

        private void OnRouteDidUpdate(RouteDidUpdate routeDidUpdateEvent)
        {
            if (routeDidUpdateEvent.RouteId == _selectedRouteId.Value && Shared.Window.IsShown)
            {
                RebuildWithScrolls();
            }
        }

        private void OnWaypointDidUpdate(WaypointDidUpdate waypointDidUpdateEvent)
        {
            if (String.IsNullOrEmpty(waypointDidUpdateEvent.RouteId))
            {
                return;
            }

            if (waypointDidUpdateEvent.RouteId == _selectedRouteId.Value && panelsByWaypointId.TryGetValue(waypointDidUpdateEvent.WaypointId, out UIPanelBuilder panelBuilder) && Shared.Window.IsShown)
            {
                Loader.LogDebug($"Rebuilding single waypoint {waypointDidUpdateEvent.WaypointId} for route {_selectedRouteId.Value}");
                panelBuilder.Rebuild();
            }
        }

        private void OnWaypointWasAppended(WaypointWasAppended waypointWasAppendedEvent)
        {
            if (String.IsNullOrEmpty(waypointWasAppendedEvent.RouteId))
            {
                return;
            }

            if (waypointWasAppendedEvent.RouteId == _selectedRouteId.Value && Shared.Window.IsShown)
            {
                RouteDefinition route = RouteRegistry.GetById(_selectedRouteId.Value);
                List<ManagedWaypoint> waypointList = route.Waypoints;
                ManagedWaypoint lastWaypoint = waypointList.Last();

                BuildRouteWaypointSection(route, lastWaypoint, waypointList.Count - 1, waypointList.Count, _scrollViewBuilder);
            }
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
            panelsByWaypointId.Clear();

            builder.Spacing = 0f;
            var filteredRoutes = RouteRegistry.Routes.Values;

            if (!string.IsNullOrWhiteSpace(_searchFilter))
            {
                filteredRoutes = [.. filteredRoutes.Where(r => r.Name.Contains(_searchFilter))];
            }

            var items = filteredRoutes
                .Select(r => new UIPanelBuilder.ListItem<RouteDefinition>(r.Id, r, r.Section, r.Name))
                .ToList();

            if (_sortByField == SortByField.Name)
            {
                if (_sortByDirection == SortByDirection.Ascending)
                {
                    items.Sort((x, y) => x.Value.Name.CompareTo(y.Value.Name));
                }
                else
                {
                    items.Sort((x, y) => y.Value.Name.CompareTo(x.Value.Name));
                }
            }

            if (_sortByField == SortByField.Section)
            {
                if (_sortByDirection == SortByDirection.Ascending)
                {
                    items.Sort((x, y) => x.Value.Section.CompareTo(y.Value.Section));
                }
                else
                {
                    items.Sort((x, y) => y.Value.Section.CompareTo(x.Value.Section));
                }
            }

            if (_sortByField == SortByField.CreatedAt)
            {
                if (_sortByDirection == SortByDirection.Ascending)
                {
                    items.Sort((x, y) => x.Value.CreatedAt.CompareTo(y.Value.CreatedAt));
                }
                else
                {
                    items.Sort((x, y) => y.Value.CreatedAt.CompareTo(x.Value.CreatedAt));
                }
            }

            if (_sortByField == SortByField.UpdatedAt)
            {
                if (_sortByDirection == SortByDirection.Ascending)
                {
                    items.Sort((x, y) => x.Value.UpdatedAt.CompareTo(y.Value.UpdatedAt));
                }
                else
                {
                    items.Sort((x, y) => y.Value.UpdatedAt.CompareTo(x.Value.UpdatedAt));
                }
            }

            builder.VStack(builder =>
            {
                builder.HStack(row =>
                {
                    row.AddLabel("Filter").VerticalTextAlignment(TMPro.VerticalAlignmentOptions.Middle);
                    row.AddInputField(_searchFilter, (value =>
                    {
                        _searchFilter = value;
                        RebuildWithScrolls();
                    }), placeholder: "Type to filter route names").FlexibleWidth();
                    row.AddButtonCompact("Clear filter", () =>
                    {
                        _searchFilter = string.Empty;
                        RebuildWithScrolls();
                    });
                    row.Spacer();

                    row.AddLabel("Sort by").VerticalTextAlignment(TMPro.VerticalAlignmentOptions.Middle);
                    row.AddDropdown(["Name", "Section", "Created at", "Updated at"], (int)_sortByField, (val =>
                    {
                        _sortByField = (SortByField)val;
                        RebuildWithScrolls();
                    })).Width(140f);

                    List<string> sortDirectionLabels = ["A to Z", "Z to A"];
                    if (_sortByField == SortByField.CreatedAt || _sortByField == SortByField.UpdatedAt)
                    {
                        sortDirectionLabels = ["Ascending", "Descending"];
                    }
                    row.AddDropdown(sortDirectionLabels, (int)_sortByDirection, (val =>
                    {
                        _sortByDirection = (SortByDirection)val;
                        RebuildWithScrolls();
                    })).Width(140f);
                });
            });

            builder.Spacer(8f);
            builder.AddHRule();
            builder.Spacer(8f);

            builder.AddListDetail<RouteDefinition>(
                items,
                _selectedRouteId,
                (detail, route) =>
                {
                    detail.Spacing = 12f;

                    if (route == null)
                    {
                        if (items.Count == 0)
                        {
                            detail.AddLabel("No matching results found.");
                            detail.AddExpandingVerticalSpacer();
                            return;
                        }
                        else
                        {
                            detail.AddLabel("Select a route from the list.");
                            detail.AddExpandingVerticalSpacer();
                            return;
                        }
                    }
                    detail.HStack(row =>
                    {
                        row.AddLabel(route.Name, text =>
                        {
                            text.fontSize = 20f;
                        });
                        row.Spacer();

                        row.AddButtonCompact("Delete", () =>
                        {
                            if (string.IsNullOrEmpty(_selectedRouteId.Value)) return;
                            var route = RouteRegistry.GetById(_selectedRouteId.Value);

                            if (route.Waypoints.Count > 0)
                            {
                                ModalAlertController.Present($"Delete route \"{route.Name}\" with {route.Waypoints.Count} waypoints?", "This cannot be undone.",
                                [
                                    (true, "Delete"),
                        (false, "Cancel")
                                ], delegate (bool b)
                                {
                                    if (b)
                                    {
                                        RouteRegistry.Remove(_selectedRouteId.Value);
                                        _selectedRouteId.Value = items.Where(x => x.Value.Id != _selectedRouteId.Value).FirstOrDefault().Value?.Id;
                                        RebuildWithScrolls();
                                    }
                                });
                            }
                            else
                            {
                                RouteRegistry.Remove(_selectedRouteId.Value);
                                _selectedRouteId.Value = items.Where(x => x.Value.Id != _selectedRouteId.Value).FirstOrDefault().Value?.Id;
                                RebuildWithScrolls();
                            }
                        });
                        List<DropdownMenu.RowData> options = [
                            new("Overwrite from Loco", "Overwrites route with waypoints from current loco"),
                            new("Add from Loco", "Appends current loco waypoints to the end of the route"),
                            new("Copy to Loco", "Appends waypoints from this route to the current loco")
                            ];
                        row.AddOptionsDropdown(options, (value =>
                        {
                            switch (value)
                            {
                                case 0:
                                    SetFromSelectedLoco(route);
                                    break;
                                case 1:
                                    AppendFromSelectedLoco(route);
                                    break;
                                case 2:
                                    AssignToSelectedLoco(route, append: true);
                                    break;
                                default:
                                    break;
                            }
                        }));
                    });
                    detail.FieldLabelWidth = 80f;
                    detail.VStack(stack =>
                    {
                        stack.Spacing = 2f;
                        stack.AddField("Name", stack.AddInputField(route.Name, newName =>
                        {
                            RouteRegistry.Rename(route, newName);
                            RebuildWithScrolls();
                        }, placeholder: "Route name"));
                        stack.AddField("Section", stack.AddInputField(route.Section, newSection =>
                        {
                            RouteRegistry.RenameSection(route, newSection);
                            RebuildWithScrolls();
                        }, placeholder: "Assign section to organize route in list"));
                    });

                    detail.ButtonStrip(row =>
                    {
                        row.AddButton("Add waypoint", () =>
                        {
                            WaypointPicker.Shared.StartPickingWaypointForRoute(RouteRegistry.AddWaypointToRoute, route.Id);
                        });

                        row.AddButton("Add from Loco", () =>
                        {
                            AppendFromSelectedLoco(route);
                        });

                        row.Spacer();
                        row.AddButton("Copy to Loco", () => AssignToSelectedLoco(route, append: true));
                    });

                    detail.AddHRule();

                    detail.VScrollView(scroll =>
                    {
                        _scrollViewBuilder = scroll;

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
                                        PresentDeleteAllWaypointsModal(route);
                                        break;
                                }
                            });
                            head.Spacer(8f);
                        });

                        scroll.Spacer(20f);
                        for (int i = 0; i < route.Waypoints.Count; i++)
                        {
                            var mw = route.Waypoints[i];
                            BuildRouteWaypointSection(route, mw, i, route.Waypoints.Count, scroll);
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

            });
        }

        private void PresentDeleteAllWaypointsModal(RouteDefinition route)
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

        private void CachePanelBuilderByWaypointId(string waypointId, UIPanelBuilder builder)
        {
            panelsByWaypointId[waypointId] = builder;
        }

        private void BuildRouteWaypointSection(RouteDefinition route, ManagedWaypoint mw, int index, int totalWaypoints, UIPanelBuilder builder)
        {
            if (!mw.IsValid())
            {
                return;
            }

            WaypointWindow.Shared.BuildWaypointSection(
             mw.Id,
             route.Id,
             index,
             totalWaypoints,
             builder,
             onWaypointChange: (ManagedWaypoint waypoint) =>
             {
                 RouteRegistry.UpdateWaypoint(waypoint, route.Id);
             },
             onWaypointDelete: (ManagedWaypoint waypoint) =>
             {
                 RouteRegistry.RemoveWaypoint(waypoint, route.Id);
             },
             onWaypointReorder: (ManagedWaypoint waypoint, int newIndex) =>
             {
                 RouteRegistry.ReorderWaypointInRoute(route, waypoint, newIndex);
             },
             onWaypointInsert: (ManagedWaypoint waypoint, string beforeWaypointId) =>
             {
                 RouteRegistry.InsertWaypointInRoute(waypoint, beforeWaypointId, route.Id);
             },
             cachePanelByWaypointId: CachePanelBuilderByWaypointId,
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

            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(loco.id);
            List<ManagedWaypoint> list = state.Waypoints;

            route.Waypoints.Clear();

            if (list != null)
            {
                foreach (var mw in list)
                {
                    if (mw.TryCopyForRoute(out ManagedWaypoint copy))
                    {
                        route.Waypoints.Add(copy);
                    }
                    else
                    {
                        Loader.LogDebug($"Failed to copy waypoint {mw.Id} to route {route}");
                    }
                }
            }

            RebuildWithScrolls();
        }

        private void AppendFromSelectedLoco(RouteDefinition route)
        {
            var loco = TrainController.Shared.SelectedLocomotive;
            if (loco == null || route == null) return;

            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(loco.id);
            List<ManagedWaypoint> list = state.Waypoints;
            if (list == null || list.Count == 0) return;

            foreach (var mw in list)
            {
                if (mw.TryCopyForRoute(out ManagedWaypoint copy))
                {
                    route.Waypoints.Add(copy);
                }
                else
                {
                    Loader.LogDebug($"Failed to copy waypoint {mw.Id} to route {route}");
                }
            }

            RebuildWithScrolls();
        }
    }
}
