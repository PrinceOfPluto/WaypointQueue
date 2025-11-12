using Model;
using Model.Ops;
using Model.Ops.Timetable;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UI;
using UI.Builder;
using UI.Common;
using UnityEngine;
using UnityEngine.UI;
using WaypointQueue.UUM;
using static UnityEngine.InputSystem.Layouts.InputControlLayout;

namespace WaypointQueue
{
    [RequireComponent(typeof(Window))]
    public class WaypointWindow : WindowBase
    {
        public override string WindowIdentifier => "WaypointPanel";
        public override string Title => "Waypoints";

        public override Vector2Int DefaultSize => new Vector2Int(400, 500);

        public override Window.Position DefaultPosition => Window.Position.LowerRight;

        public override Window.Sizing Sizing => Window.Sizing.Resizable(DefaultSize, new Vector2Int(650, Screen.height));

        public static WaypointWindow Shared => WindowManager.Shared.GetWindow<WaypointWindow>();

        private string _selectedLocomotiveId;
        private Coroutine _coroutine;
        private float _scrollPosition;

        private void OnEnable()
        {
            WaypointQueueController.OnWaypointsUpdated += OnWaypointsUpdated;
        }

        private void OnDisable()
        {
            WaypointQueueController.OnWaypointsUpdated -= OnWaypointsUpdated;
        }

        private void OnWaypointsUpdated()
        {
            //Loader.LogDebug($"WaypointWindow OnWaypointsUpdated");
            RebuildWithScroll();
        }

        private void RebuildWithScroll()
        {
            ScrollRect scrollRect = Shared.Window.contentRectTransform.GetComponentInChildren<ScrollRect>();
            bool setScrollOnNextUpdate = false;
            if (scrollRect != null)
            {
                _scrollPosition = scrollRect.verticalNormalizedPosition;
                setScrollOnNextUpdate = true;
            }

            Rebuild();

            if (setScrollOnNextUpdate)
            {
                Invoke(nameof(HandleScrollUpdate), 0f);
            }
        }

        private void HandleScrollUpdate()
        {
            ScrollRect scrollRect = Shared.Window.contentRectTransform.GetComponentInChildren<ScrollRect>();
            if (scrollRect != null)
            {
                scrollRect.verticalNormalizedPosition = _scrollPosition;
            }
        }

        private IEnumerator Ticker()
        {
            WaitForSeconds t = new WaitForSeconds(0.2f);
            while (true)
            {
                yield return t;
                Tick();
            }
        }

        private void Tick()
        {
            if (TrainController.Shared.SelectedLocomotive.id != _selectedLocomotiveId && Shared.Window.IsShown)
            {
                Loader.LogDebug($"Selected locomotive changed, rebuilding waypoint window");
                RebuildWithScroll();
            }
        }

        public void Show()
        {
            Loader.LogDebug($"Rebuilding and showing waypoint panel");
            RebuildWithScroll();
            WindowPersistence.SetInitialPositionSize(Shared.Window, WindowIdentifier, DefaultSize, DefaultPosition, Sizing);
            Shared.Window.ShowWindow();

            if (_coroutine == null)
            {
                Loader.LogDebug($"Starting waypoint window coroutine");
                _coroutine = StartCoroutine(Ticker());
            }
        }

        public void Hide()
        {
            Shared.Window.CloseWindow();

            if (_coroutine != null)
            {
                Loader.LogDebug($"Stopping waypoint window coroutine");
                StopCoroutine(_coroutine);
                _coroutine = null;
            }
        }

        public static void Toggle()
        {
            Loader.LogDebug($"Toggling waypoint panel");
            if (Shared.Window.IsShown)
            {
                Loader.LogDebug($"Toggling waypoint panel closed");
                Shared.Hide();
            }
            else
            {
                Loader.LogDebug($"Toggling waypoint panel open");
                Shared.Show();
            }
        }

        public override void Populate(UIPanelBuilder builder)
        {
            Loader.LogDebug($"Populating WaypointWindow");

            builder.Spacing = 12f;

            builder.VScrollView(delegate (UIPanelBuilder builder)
            {
                BaseLocomotive selectedLocomotive = TrainController.Shared.SelectedLocomotive;
                _selectedLocomotiveId = selectedLocomotive?.id;
                if (selectedLocomotive == null)
                {
                    builder.AddLabel("No locomotive is currently selected").HorizontalTextAlignment(TMPro.HorizontalAlignmentOptions.Center);
                    return;
                }

                List<ManagedWaypoint> waypointList = WaypointQueueController.Shared.GetWaypointList(selectedLocomotive);

                if (waypointList == null || waypointList.Count == 0)
                {
                    builder.AddLabel($"Locomotive {selectedLocomotive.Ident} has no waypoints.").HorizontalTextAlignment(TMPro.HorizontalAlignmentOptions.Center);
                    return;
                }

                builder.HStack(delegate (UIPanelBuilder builder)
                {
                    builder.AddLabel($"Showing waypoints for {selectedLocomotive.Ident}");
                    builder.Spacer();

                    List<DropdownMenu.RowData> options = new List<DropdownMenu.RowData>();
                    options.Add(new DropdownMenu.RowData("Reroute", "Reroute the current waypoint"));
                    options.Add(new DropdownMenu.RowData("Refresh", "Forces a refresh of the waypoint window"));
                    options.Add(new DropdownMenu.RowData("Delete all", ""));

                    builder.AddOptionsDropdown(options, (int value) =>
                    {
                        switch (value)
                        {
                            case 0:
                                Loader.LogDebug($"Refreshing orders");
                                WaypointQueueController.Shared.RerouteCurrentWaypoint(selectedLocomotive);
                                break;
                            case 1:
                                RebuildWithScroll();
                                break;
                            case 2:
                                PresentDeleteAllModal(selectedLocomotive);
                                break;
                            default:
                                break;
                        }
                    });
                    builder.Spacer(8f);
                });

                builder.Spacer(20f);
                for (int i = 0; i < waypointList.Count; i++)
                {
                    ManagedWaypoint waypoint = waypointList[i];
                    BuildWaypointSection(waypoint, i + 1, builder);
                    builder.Spacer(20f);
                }
            });
        }

        private void PresentDeleteAllModal(BaseLocomotive selectedLocomotive)
        {
            ModalAlertController.Present($"Delete all waypoints for {selectedLocomotive.Ident}?", "This cannot be undone.", new (bool, string)[2]
                {
                    (true, "Delete"),
                    (false, "Cancel")
                }, delegate (bool b)
                {
                    if (b)
                    {
                        WaypointQueueController.Shared.ClearWaypointState(selectedLocomotive);
                    }
                });
        }

        internal void BuildWaypointSection(ManagedWaypoint waypoint, int number, UIPanelBuilder builder)
        {
            builder.AddHRule();
            builder.Spacer(16f);
            builder.HStack(delegate (UIPanelBuilder builder)
            {
                builder.AddLabel($"Waypoint {number}");
                builder.Spacer();
                List<DropdownMenu.RowData> options = new List<DropdownMenu.RowData>();
                options.Add(new DropdownMenu.RowData("Jump to waypoint", ""));
                options.Add(new DropdownMenu.RowData("Delete", ""));
                builder.AddOptionsDropdown(options, (int value) =>
                {
                    switch (value)
                    {
                        case 0:
                            JumpCameraToWaypoint(waypoint);
                            break;
                        case 1:
                            WaypointQueueController.Shared.RemoveWaypoint(waypoint);
                            break;
                        default:
                            break;
                    }
                });
                builder.Spacer(8f);
            });

            builder.AddField($"Destination", builder.HStack(delegate (UIPanelBuilder field)
            {
                field.AddLabel(waypoint.AreaName?.Length > 0 ? waypoint.AreaName : "Unknown").Width(160f);

                field.Spacer(4f);

                // Right: "Set symbol" dropdown (No change / None / symbols…)
                field.AddLabel("Symbol").Width(80f);

                var (labels, values, selectedIndex) = BuildTimetableSymbolChoices(waypoint.TimetableSymbol);
                field.AddDropdown(labels, selectedIndex, (int idx) =>
                {
                    // Map: 0 = No change (null), 1 = None (""), 2+ = actual symbol names
                    waypoint.TimetableSymbol = values[idx];                 // null or "" or actual symbol
                    WaypointQueueController.Shared.UpdateWaypoint(waypoint); // persist/update UI
                })
                .Width(200)
                .Height(10f); 

            }));

            if (waypoint.IsCoupling)
            {
                TrainController.Shared.TryGetCarForId(waypoint.CoupleToCarId, out Car couplingToCar);
                builder.AddField($"Couple to ", builder.HStack(delegate (UIPanelBuilder field)
                {
                    field.AddLabel(couplingToCar.Ident.ToString());
                }));

                if (Loader.Settings.UseCompactLayout)
                {
                    builder.HStack(delegate (UIPanelBuilder builder)
                    {
                        AddConnectAirAndReleaseBrakeToggles(waypoint, builder);
                    });
                }
                else
                {
                    AddConnectAirAndReleaseBrakeToggles(waypoint, builder);
                }

                var postCouplingCutField = builder.AddField($"Post-coupling cut", builder.HStack(delegate (UIPanelBuilder field)
                {
                    string prefix = waypoint.TakeOrLeaveCut == ManagedWaypoint.PostCoupleCutType.Take ? "Take " : "Leave ";
                    AddCarCutButtons(waypoint, field, prefix);
                    field.AddButtonCompact("Swap", () =>
                    {
                        waypoint.TakeOrLeaveCut = waypoint.TakeOrLeaveCut == ManagedWaypoint.PostCoupleCutType.Take ? ManagedWaypoint.PostCoupleCutType.Leave : ManagedWaypoint.PostCoupleCutType.Take;
                        WaypointQueueController.Shared.UpdateWaypoint(waypoint);
                    });
                    field.Spacer(8f);
                }));

                if (Loader.Settings.EnableTooltips)
                {
                    postCouplingCutField.Tooltip("Cutting cars after coupling", "After coupling, you can \"Take\" or \"Leave\" a number of cars. " +
                    "This is very useful when queueing switching orders." +
                    "\n\nIf you couple to a cut of 3 cars and \"Take\" 2 cars, you will leave with the 2 closest cars and the 3rd car will be left behind. " +
                    "You \"Take\" cars from the cut you are coupling to." +
                    "\n\nIf you are coupling 2 additional cars to 1 car already spotted, you can \"Leave\" 2 cars and continue to the next queued waypoint. " +
                    "You \"Leave\" cars from your current consist." +
                    "\n\nIf you Take or Leave 0 cars, you will NOT perform a post-coupling cut. In other words, you will remain coupled to the full cut.");
                }

                if (waypoint.NumberOfCarsToCut > 0)
                {
                    if (Loader.Settings.UseCompactLayout)
                    {
                        builder.HStack(delegate (UIPanelBuilder builder)
                    {
                        AddBleedAirAndSetBrakeToggles(waypoint, builder);
                    });
                    }
                    else
                    {
                        AddBleedAirAndSetBrakeToggles(waypoint, builder);
                    }
                }
            }
            else
            {
                builder.HStack(delegate (UIPanelBuilder builder)
                {
                    builder.AddField($"Uncouple", builder.HStack(delegate (UIPanelBuilder field)
                    {
                        AddCarCutButtons(waypoint, field, null);
                    }));
                });

                if (waypoint.IsUncoupling)
                {
                    builder.AddField($"Count cars from",
                    builder.AddDropdown(new List<string> { "Closest to waypoint", "Furthest from waypoint" }, waypoint.CountUncoupledFromNearestToWaypoint ? 0 : 1, (int value) =>
                    {
                        waypoint.CountUncoupledFromNearestToWaypoint = !waypoint.CountUncoupledFromNearestToWaypoint;
                        WaypointQueueController.Shared.UpdateWaypoint(waypoint);
                    }));

                    if (Loader.Settings.UseCompactLayout)
                    {
                        builder.HStack(delegate (UIPanelBuilder builder)
                        {
                            AddBleedAirAndSetBrakeToggles(waypoint, builder);
                        });
                    }
                    else
                    {
                        AddBleedAirAndSetBrakeToggles(waypoint, builder);
                    }

                    var takeActiveCutField = builder.AddField($"Take active cut", builder.AddToggle(() => waypoint.TakeUncoupledCarsAsActiveCut, delegate (bool value)
                    {
                        waypoint.TakeUncoupledCarsAsActiveCut = value;
                        WaypointQueueController.Shared.UpdateWaypoint(waypoint);
                    }));

                    if (Loader.Settings.EnableTooltips)
                    {
                        takeActiveCutField.Tooltip("Take active cut", "If this is active, the number of cars to uncouple will still be part of the active train. " +
                            "The rest of the train will be treated as an uncoupled cut which may bleed air and apply handbrakes. " +
                            "This is particularly useful for local freight switching." +
                            "\n\nA train of 10 cars arrives in Whittier. The 2 cars behind the locomotive need to be delivered. " +
                            "By checking \"Take active cut\", you can order the engineer to travel to a waypoint, uncouple 4 cars including the locomotive and tender, and travel to another waypoint to the industry track to deliver the 2 cars, all while knowing that the rest of the local freight consist has handbrakes applied.");
                    }
                }
            }

            if (waypoint.CanRefuelNearby)
            {
                builder.AddField($"Refuel {waypoint.RefuelLoadName}", builder.AddToggle(() => waypoint.WillRefuel, delegate (bool value)
                {
                    waypoint.WillRefuel = value;
                    WaypointQueueController.Shared.UpdateWaypoint(waypoint);
                }));
            }
        }

        private void AddConnectAirAndReleaseBrakeToggles(ManagedWaypoint waypoint, UIPanelBuilder builder, Action onUpdate = null)
        {
            builder.AddField("Connect air", builder.AddToggle(() => waypoint.ConnectAirOnCouple, delegate (bool value)
            {
                waypoint.ConnectAirOnCouple = value;
                if (onUpdate != null) onUpdate();
                else WaypointQueueController.Shared.UpdateWaypoint(waypoint);
            }));

            builder.AddField("Release handbrakes", builder.AddToggle(() => waypoint.ReleaseHandbrakesOnCouple, delegate (bool value)
            {
                waypoint.ReleaseHandbrakesOnCouple = value;
                if (onUpdate != null) onUpdate();
                else WaypointQueueController.Shared.UpdateWaypoint(waypoint);
            }));
        }

        private void AddBleedAirAndSetBrakeToggles(ManagedWaypoint waypoint, UIPanelBuilder builder, Action onUpdate = null)
        {
            builder.AddField("Bleed air", builder.AddToggle(() => waypoint.BleedAirOnUncouple, delegate (bool value)
            {
                waypoint.BleedAirOnUncouple = value;
                if (onUpdate != null) onUpdate();
                else WaypointQueueController.Shared.UpdateWaypoint(waypoint);
            }, interactable: waypoint.NumberOfCarsToCut > 0));
            builder.AddField("Apply handbrakes", builder.AddToggle(() => waypoint.ApplyHandbrakesOnUncouple, delegate (bool value)
            {
                waypoint.ApplyHandbrakesOnUncouple = value;
                if (onUpdate != null) onUpdate();
                else WaypointQueueController.Shared.UpdateWaypoint(waypoint);
            }, interactable: waypoint.NumberOfCarsToCut > 0));
        }

        private void AddCarCutButtons(ManagedWaypoint waypoint, UIPanelBuilder field, string prefix = null, Action onUpdate = null)
        {
            string pluralCars = waypoint.NumberOfCarsToCut == 1 ? "car" : "cars";
            field.AddLabel($"{prefix}{waypoint.NumberOfCarsToCut}")
                            .TextWrap(TextOverflowModes.Overflow, TextWrappingModes.NoWrap)
                            .Width(100f);
            field.AddButtonCompact("-", delegate
            {
                int result = Mathf.Max(waypoint.NumberOfCarsToCut - GetOffsetAmount(), 0);
                waypoint.NumberOfCarsToCut = result;
                if (onUpdate != null) onUpdate();
                else WaypointQueueController.Shared.UpdateWaypoint(waypoint);
            }).Disable(waypoint.NumberOfCarsToCut <= 0).Width(24f);
            field.AddButtonCompact("+", delegate
            {
                waypoint.NumberOfCarsToCut += GetOffsetAmount();
                if (onUpdate != null) onUpdate();
                else WaypointQueueController.Shared.UpdateWaypoint(waypoint);
            }).Width(24f);
        }
        private int GetOffsetAmount()
        {
            int offsetAmount = 1;
            if (GameInput.IsShiftDown) offsetAmount = 5;
            if (GameInput.IsControlDown) offsetAmount = 10;
            return offsetAmount;
        }

        private void JumpCameraToWaypoint(ManagedWaypoint waypoint)
        {
            CameraSelector.shared.JumpToPoint(waypoint.Location.GetPosition(), waypoint.Location.GetRotation(), CameraSelector.CameraIdentifier.Strategy);
        }

        private static (List<string> labels, List<string> values, int selected) BuildTimetableSymbolChoices(string current)
        {
            // 0 = "No change" → do nothing
            var labels = new List<string> { "No change" };
            var values = new List<string> { null };

            try
            {
                var timetable = TimetableController.Shared?.Current; // active timetable
                if (timetable?.Trains != null && timetable.Trains.Count > 0)
                {
                    // Sort by SortName; store actual symbol in values = Train.Name
                    var rows = timetable.Trains
                        .Values
                        .Where(t => !string.IsNullOrEmpty(t.Name))
                        .OrderBy(t => t.SortName)
                        .Select(t => t.Name)
                        .ToList();

                    foreach (var sym in rows)
                    {
                        labels.Add(sym);   // display plain symbol
                        values.Add(sym);   // value = symbol
                    }

                    Loader.LogDebug($"[TimetableSymbolDropdown] Loaded {rows.Count} symbols from TimetableController.Current.");
                }
                else
                {
                    Loader.LogDebug("[TimetableSymbolDropdown] TimetableController.Current is null or has no trains.");
                }
            }
            catch (Exception ex)
            {
                Loader.Log($"[TimetableSymbolDropdown] Error building choices: {ex}");
            }

            // Selected index: default to "No change" (0); if current is set, select it if present
            int selected = 0;
            if (!string.IsNullOrEmpty(current))
            {
                int idx = values.IndexOf(current);
                if (idx >= 0) selected = idx;
            }
            return (labels, values, selected);
        }
    }
}
