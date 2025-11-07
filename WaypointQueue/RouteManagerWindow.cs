using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Model;
using Model.Ops;
using TMPro;
using Track;
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
        public override Vector2Int DefaultSize => new Vector2Int(700, 560);
        public override Window.Position DefaultPosition => Window.Position.Center;
        public override Window.Sizing Sizing => Window.Sizing.Resizable(DefaultSize, new Vector2Int(1100, Screen.height));

        public static RouteManagerWindow Shared => WindowManager.Shared.GetWindow<RouteManagerWindow>();

        private readonly UIState<string> _selectedRouteId = new UIState<string>(null);

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
                Shared.Window, WindowIdentifier, DefaultSize, DefaultPosition, Sizing
            );
            Shared.Window.ShowWindow();
        }

        public void Hide()
        {
            Shared.Window.CloseWindow();
        }

        public void Toggle()
        {
            if (Shared.Window.IsShown) Hide();
            else Show();
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
                        RouteRegistry.Rename(route, newName, moveFile: true);
                        detail.Rebuild();
                    }, placeholder: "Route name"));

                    detail.Spacer(8f);
                    detail.ButtonStrip(row =>
                    {
                        row.AddButton("Capture from Selected Loco", () =>
                        {
                            var loco = TrainController.Shared.SelectedLocomotive;
                            if (loco == null) return;
                            var list = WaypointQueueController.Shared.GetWaypointList(loco);
                            route.Waypoints.Clear();
                            if (list != null)
                            {
                                foreach (var mw in list)
                                    route.Waypoints.Add(FromManagedWaypoint(mw));
                            }
                            RouteRegistry.Save(route);
                            detail.Rebuild();
                        });
                        row.AddButton("Assign → Append", () => AssignToSelectedLoco(route, append: true));
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
                                    case 0: // refresh
                                        detail.Rebuild();
                                        break;
                                    case 1: // delete all
                                        PresentDeleteAllModal(route);
                                        break;
                                }
                            });
                            head.Spacer(8f);
                        });

                        scroll.Spacer(20f);
                        for (int i = 0; i < route.Waypoints.Count; i++)
                        {
                            var rwp = route.Waypoints[i];
                            BuildRouteWaypointSection(route, rwp, i + 1, scroll);
                            scroll.Spacer(20f);
                        }
                    });

                }
            );

            builder.Spacer(12f);

            // Bottom bar: New / Save / Delete / Reload
            builder.ButtonStrip(row =>
            {
                row.AddButton("New Route", () =>
                {
                    var r = new RouteDefinition { Name = $"Route {RouteRegistry.Routes.Count + 1}" };
                    RouteRegistry.Add(r, save: true);
                    _selectedRouteId.Value = r.Id;
                    builder.Rebuild();
                });

                row.AddButton("Save", () =>
                {
                    var r = RouteRegistry.GetById(_selectedRouteId.Value);
                    if (r != null) RouteRegistry.Save(r);
                });

                row.AddButton("Delete", () =>
                {
                    if (string.IsNullOrEmpty(_selectedRouteId.Value)) return;
                    RouteRegistry.Remove(_selectedRouteId.Value, deleteFile: true);
                    _selectedRouteId.Value = RouteRegistry.Routes.FirstOrDefault()?.Id;
                    builder.Rebuild();
                });

                row.Spacer();

                row.AddButton("Reload", () =>
                {
                    var keep = _selectedRouteId.Value;
                    RouteRegistry.ReloadFromDisk();
                    if (RouteRegistry.GetById(keep) == null)
                        _selectedRouteId.Value = RouteRegistry.Routes.FirstOrDefault()?.Id;
                    builder.Rebuild();
                });
            });
        }

        private static bool TryResolveLocation(RouteWaypoint rwp, out Location loc)
        {
            try
            {
                loc = Graph.Shared.ResolveLocationString(rwp.LocationString);
                return true;
            }
            catch
            {
                loc = default; // valid zero-initialized Location struct
                return false;
            }
        }

        private static string GetAreaName(RouteWaypoint rwp)
        {
            if (TryResolveLocation(rwp, out var loc))
            {
                var area = OpsController.Shared.ClosestAreaForGamePosition(loc.GetPosition());
                return area?.name ?? "Unknown";
            }
            return "Unknown";
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
                        RouteRegistry.Save(route);
                        Rebuild();
                    }
                });
        }

        private void BuildRouteWaypointSection(RouteDefinition route, RouteWaypoint rwp, int number, UIPanelBuilder builder)
        {
            builder.AddHRule();
            builder.Spacer(16f);
            builder.HStack(row =>
            {
                row.AddLabel($"Waypoint {number}");
                row.Spacer();
                var options = new List<DropdownMenu.RowData>
                {
                    new DropdownMenu.RowData("Jump to waypoint", ""),
                    new DropdownMenu.RowData("Delete", "")
                };
                row.AddOptionsDropdown(options, (int value) =>
                {
                    switch (value)
                    {
                        case 0:
                            JumpCameraToWaypoint(rwp);
                            break;
                        case 1:
                            route.Waypoints.Remove(rwp);
                            RouteRegistry.Save(route);
                            Rebuild();
                            break;
                    }
                });
                row.Spacer(8f);
            });

            builder.AddField("Destination", builder.HStack(field =>
            {
                field.AddLabel(GetAreaName(rwp));
            }));

            bool isCoupling = !string.IsNullOrEmpty(rwp.CoupleToCarId);

            if (isCoupling)
            {
                if (TrainController.Shared.TryGetCarForId(rwp.CoupleToCarId, out Car couplingToCar))
                {
                    builder.AddField("Couple to ", builder.HStack(field =>
                    {
                        field.AddLabel(couplingToCar.Ident.ToString());
                    }));
                }
                else
                {
                    builder.AddField("Couple to ", builder.HStack(field =>
                    {
                        field.AddLabel($"Unknown ({rwp.CoupleToCarId})");
                    }));
                }

                if (Loader.Settings.UseCompactLayout)
                {
                    builder.HStack(inner => { AddConnectAirAndReleaseBrakeToggles(rwp, inner, route); });
                }
                else
                {
                    AddConnectAirAndReleaseBrakeToggles(rwp, builder, route);
                }

                builder.AddField("Post-coupling cut", builder.HStack(field =>
                {
                    string prefix = rwp.TakeOrLeaveCut == ManagedWaypoint.PostCoupleCutType.Take ? "Take " : "Leave ";
                    AddCarCutButtons(rwp, field, route, prefix);
                    field.AddButtonCompact("Swap", () =>
                    {
                        rwp.TakeOrLeaveCut = rwp.TakeOrLeaveCut == ManagedWaypoint.PostCoupleCutType.Take
                            ? ManagedWaypoint.PostCoupleCutType.Leave
                            : ManagedWaypoint.PostCoupleCutType.Take;
                        RouteRegistry.Save(route);
                    });
                    field.Spacer(8f);
                })).Tooltip("Cutting cars after coupling",
                    "After coupling, you can \"Take\" or \"Leave\" a number of cars...");

                if (rwp.NumberOfCarsToCut > 0)
                {
                    if (Loader.Settings.UseCompactLayout)
                    {
                        builder.HStack(inner => { AddBleedAirAndSetBrakeToggles(rwp, inner, route); });
                    }
                    else
                    {
                        AddBleedAirAndSetBrakeToggles(rwp, builder, route);
                    }
                }
            }
            else
            {
                builder.HStack(row =>
                {
                    row.AddField("Uncouple", row.HStack(row2 =>
                    {
                        AddCarCutButtons(rwp, row2, route, null);
                    }));
                });

                bool isUncoupling = rwp.NumberOfCarsToCut > 0;
                if (isUncoupling)
                {
                    builder.AddField("Count cars from",
                        builder.AddDropdown(new List<string> { "Closest to waypoint", "Furthest from waypoint" },
                        rwp.CountUncoupledFromNearestToWaypoint ? 0 : 1, (int value) =>
                        {
                            rwp.CountUncoupledFromNearestToWaypoint = !rwp.CountUncoupledFromNearestToWaypoint;
                            RouteRegistry.Save(route);
                        }));

                    if (Loader.Settings.UseCompactLayout)
                    {
                        builder.HStack(inner => { AddBleedAirAndSetBrakeToggles(rwp, inner, route); });
                    }
                    else
                    {
                        AddBleedAirAndSetBrakeToggles(rwp, builder, route);
                    }

                    builder.AddField("Take active cut", builder.AddToggle(() => rwp.TakeUncoupledCarsAsActiveCut, (bool value) =>
                    {
                        rwp.TakeUncoupledCarsAsActiveCut = value;
                        rwp.TakeUncoupledCarsAsActiveCut = value; RouteRegistry.Save(route);
                    })).Tooltip("Take active cut", "If this is active, the number of cars to uncouple...");
                }
            }

            // Refuel
            if (!string.IsNullOrEmpty(rwp.RefuelLoadName))
            {
                builder.AddField($"Refuel {rwp.RefuelLoadName}", builder.AddToggle(() => rwp.WillRefuel, (bool value) =>
                {
                    rwp.WillRefuel = value;
                    rwp.WillRefuel = value; RouteRegistry.Save(route);
                }));
            }
        }

        // ---------- UI helpers for toggles/buttons (RouteWaypoint variants) ----------
        private void AddConnectAirAndReleaseBrakeToggles(RouteWaypoint rwp, UIPanelBuilder builder, RouteDefinition route)
        {
            builder.AddField("Connect air", builder.AddToggle(() => rwp.ConnectAirOnCouple, (bool value) =>
            {
                rwp.ConnectAirOnCouple = value;
                RouteRegistry.Save(route);
            }));

            builder.AddField("Release handbrakes", builder.AddToggle(() => rwp.ReleaseHandbrakesOnCouple, (bool value) =>
            {
                rwp.ReleaseHandbrakesOnCouple = value;
                RouteRegistry.Save(route);
            }));
        }

        private void AddBleedAirAndSetBrakeToggles(RouteWaypoint rwp, UIPanelBuilder builder, RouteDefinition route)
        {
            builder.AddField("Bleed air", builder.AddToggle(() => rwp.BleedAirOnUncouple, (bool value) =>
            {
                rwp.BleedAirOnUncouple = value;
                RouteRegistry.Save(route);
            }, interactable: rwp.NumberOfCarsToCut > 0));

            builder.AddField("Apply handbrakes", builder.AddToggle(() => rwp.ApplyHandbrakesOnUncouple, (bool value) =>
            {
                rwp.ApplyHandbrakesOnUncouple = value;
                RouteRegistry.Save(route);
            }, interactable: rwp.NumberOfCarsToCut > 0));
        }

        private void AddCarCutButtons(RouteWaypoint rwp, UIPanelBuilder field, RouteDefinition route, string prefix = null)
        {
            field.AddLabel($"{prefix}{rwp.NumberOfCarsToCut}")
                .TextWrap(TextOverflowModes.Overflow, TextWrappingModes.NoWrap)
                .Width(100f);
            field.AddButtonCompact("-", () =>
            {
                int result = Mathf.Max(rwp.NumberOfCarsToCut - GetOffsetAmount(), 0);
                rwp.NumberOfCarsToCut = result;
                RouteRegistry.Save(route);
            }).Disable(rwp.NumberOfCarsToCut <= 0).Width(24f);
            field.AddButtonCompact("+", () =>
            {
                rwp.NumberOfCarsToCut += GetOffsetAmount();
                RouteRegistry.Save(route);
            }).Width(24f);
        }

        private int GetOffsetAmount()
        {
            int offsetAmount = 1;
            if (GameInput.IsShiftDown) offsetAmount = 5;
            if (GameInput.IsControlDown) offsetAmount = 10;
            return offsetAmount;
        }

        // ---------- Conversions & actions ----------
        private RouteWaypoint FromManagedWaypoint(ManagedWaypoint mw)
        {
            return new RouteWaypoint
            {
                LocationString = mw.LocationString,
                CoupleToCarId = mw.CoupleToCarId,
                ConnectAirOnCouple = mw.ConnectAirOnCouple,
                ReleaseHandbrakesOnCouple = mw.ReleaseHandbrakesOnCouple,
                ApplyHandbrakesOnUncouple = mw.ApplyHandbrakesOnUncouple,
                BleedAirOnUncouple = mw.BleedAirOnUncouple,
                NumberOfCarsToCut = mw.NumberOfCarsToCut,
                CountUncoupledFromNearestToWaypoint = mw.CountUncoupledFromNearestToWaypoint,
                TakeOrLeaveCut = mw.TakeOrLeaveCut,
                TakeUncoupledCarsAsActiveCut = mw.TakeUncoupledCarsAsActiveCut,
                SerializableRefuelPoint = mw.SerializableRefuelPoint,
                RefuelIndustryId = mw.RefuelIndustryId,
                RefuelLoadName = mw.RefuelLoadName,
                RefuelMaxCapacity = mw.RefuelMaxCapacity,
                WillRefuel = mw.WillRefuel
            };
        }

        private void AssignToSelectedLoco(RouteDefinition route, bool append)
        {
            var loco = TrainController.Shared.SelectedLocomotive;
            if (loco == null || route == null) return;

            if (!append)
            {
                WaypointQueueController.Shared.ClearWaypointState(loco);
            }

            if (route.Waypoints == null || route.Waypoints.Count == 0) return;

            foreach (var rw in route.Waypoints)
            {
                if (TryResolveLocation(rw, out var location))
                {
                    WaypointQueueController.Shared.AddWaypoint(loco, location, rw.CoupleToCarId, isReplacing: false);
                }

                // copy flags onto the newly-added queue item (last)
                var list = WaypointQueueController.Shared.GetWaypointList(loco);
                if (list != null && list.Count > 0)
                {
                    var last = list[list.Count - 1];

                    last.ConnectAirOnCouple = rw.ConnectAirOnCouple;
                    last.ReleaseHandbrakesOnCouple = rw.ReleaseHandbrakesOnCouple;
                    last.ApplyHandbrakesOnUncouple = rw.ApplyHandbrakesOnUncouple;
                    last.BleedAirOnUncouple = rw.BleedAirOnUncouple;
                    last.NumberOfCarsToCut = rw.NumberOfCarsToCut;
                    last.CountUncoupledFromNearestToWaypoint = rw.CountUncoupledFromNearestToWaypoint;
                    last.TakeOrLeaveCut = rw.TakeOrLeaveCut;
                    last.TakeUncoupledCarsAsActiveCut = rw.TakeUncoupledCarsAsActiveCut;

                    last.SerializableRefuelPoint = rw.SerializableRefuelPoint;
                    last.RefuelIndustryId = rw.RefuelIndustryId;
                    last.RefuelLoadName = rw.RefuelLoadName;
                    last.RefuelMaxCapacity = rw.RefuelMaxCapacity;
                    last.WillRefuel = rw.WillRefuel;

                    WaypointQueueController.Shared.UpdateWaypoint(last);
                }
            }
        }

        private void JumpCameraToWaypoint(RouteWaypoint rwp)
        {
            try
            {
                if (TryResolveLocation(rwp, out var loc))
                {
                    CameraSelector.shared.JumpToPoint(loc.GetPosition(), loc.GetRotation(), CameraSelector.CameraIdentifier.Strategy);
                }
            }
            catch
            {
                // ignore bad location strings
            }
        }
    }
}
