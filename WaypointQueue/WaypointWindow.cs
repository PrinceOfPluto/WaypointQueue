using Game;
using HarmonyLib;
using Model;
using Model.Ops;
using Model.Ops.Timetable;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using TMPro;
using UI;
using UI.Builder;
using UI.Common;
using UI.CompanyWindow;
using UnityEngine;
using UnityEngine.UI;
using WaypointQueue.UUM;

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

        private static CrewsPanelBuilder _crewsPanelBuilder => new();

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
            if (TrainController.Shared.SelectedLocomotive?.id != _selectedLocomotiveId)
            {
                if (TrainController.Shared.SelectedLocomotive == null)
                {
                    _selectedLocomotiveId = null;
                }
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
                    BuildWaypointSection(waypoint, i, waypointList.Count, builder, onWaypointChange: OnWaypointChange, onWaypointDelete: OnWaypointDelete, onWaypointReorder: OnWaypointReorder);
                    builder.Spacer(20f);
                }
            });
        }

        private void OnWaypointChange(ManagedWaypoint waypoint)
        {
            WaypointQueueController.Shared.UpdateWaypoint(waypoint);
        }

        private void OnWaypointDelete(ManagedWaypoint waypoint)
        {
            WaypointQueueController.Shared.RemoveWaypoint(waypoint);
        }

        private void OnWaypointReorder(ManagedWaypoint waypoint, int newIndex)
        {
            WaypointQueueController.Shared.ReorderWaypoint(waypoint, newIndex);
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

        internal void BuildWaypointSection(ManagedWaypoint waypoint, int index, int totalWaypoints, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange, Action<ManagedWaypoint> onWaypointDelete, Action<ManagedWaypoint, int> onWaypointReorder, bool isRouteWindow = false)
        {
            builder.AddHRule();
            builder.Spacer(16f);
            builder.HStack(delegate (UIPanelBuilder builder)
            {
                builder.AddLabel($"Waypoint {index + 1}");
                builder.Spacer();
                List<DropdownMenu.RowData> options = new List<DropdownMenu.RowData>();
                var jumpToWaypointRow = new DropdownMenu.RowData("Jump to waypoint", "");
                var moveToTopRow = new DropdownMenu.RowData("Move to top", "");
                var moveUpOneRow = new DropdownMenu.RowData("Move up", "");
                var moveDownOneRow = new DropdownMenu.RowData("Move down", "");
                var moveToBottomRow = new DropdownMenu.RowData("Move to bottom", "");
                var removeWaitRow = new DropdownMenu.RowData("Remove wait", "");
                var deleteWaypointRow = new DropdownMenu.RowData("Delete", "");

                options.Add(jumpToWaypointRow);

                if (index > 0)
                {
                    options.Add(moveUpOneRow);
                    options.Add(moveToTopRow);
                }
                if (index < totalWaypoints - 1)
                {
                    options.Add(moveDownOneRow);
                    options.Add(moveToBottomRow);
                }

                if (waypoint.WillWait)
                {
                    options.Add(removeWaitRow);
                }
                options.Add(deleteWaypointRow);

                builder.AddOptionsDropdown(options, (int value) =>
                {
                    if (value == options.IndexOf(jumpToWaypointRow))
                    {
                        JumpCameraToWaypoint(waypoint);
                    }

                    if (value == options.IndexOf(moveToTopRow))
                    {
                        onWaypointReorder(waypoint, 0);
                    }

                    if (value == options.IndexOf(moveUpOneRow))
                    {
                        onWaypointReorder(waypoint, index - 1);
                    }

                    if (value == options.IndexOf(moveDownOneRow))
                    {
                        // incrementing by 2 to account for index shifting after removal
                        onWaypointReorder(waypoint, index + 2);
                    }

                    if (value == options.IndexOf(moveToBottomRow))
                    {
                        onWaypointReorder(waypoint, totalWaypoints);
                    }

                    if (value == options.IndexOf(removeWaitRow))
                    {
                        waypoint.ClearWaiting();
                        onWaypointChange(waypoint);
                    }

                    if (value == options.IndexOf(deleteWaypointRow))
                    {
                        onWaypointDelete(waypoint);
                    }
                });
                builder.Spacer(8f);
            });

            builder.AddField($"Destination", builder.HStack(delegate (UIPanelBuilder field)
            {
                field.AddLabel(waypoint.AreaName?.Length > 0 ? waypoint.AreaName : "Unknown").Width(160f);
            }));

            if (isRouteWindow || waypoint.Locomotive.TryGetTimetableTrainCrewId(out string trainCrewId))
            {
                var (labels, values, selectedIndex) = BuildTimetableSymbolChoices(waypoint.TimetableSymbol);

                var trainSymbolField = builder.AddField($"Train Symbol",
                builder.AddDropdown(labels, selectedIndex, (int idx) =>
                {
                    // Map: 0 = No change (null), 1 = None (""), 2+ = actual symbol names
                    waypoint.TimetableSymbol = values[idx];                 // null or "" or actual symbol
                    onWaypointChange(waypoint);
                }));

                AddLabelOnlyTooltip(trainSymbolField, "Train symbol", "Change to this train symbol once this waypoint becomes active.");
            }

            if (!waypoint.IsCoupling && !waypoint.SeekNearbyCoupling)
            {
                var stopAtWaypointField = builder.AddField($"Stop at waypoint", builder.AddToggle(() => waypoint.StopAtWaypoint, delegate (bool value)
                {
                    waypoint.StopAtWaypoint = value;
                    if (!waypoint.StopAtWaypoint)
                    {
                        waypoint.SetTargetSpeedToOrdersMax();
                    }
                    onWaypointChange(waypoint);
                }));

                AddLabelOnlyTooltip(stopAtWaypointField, "Stop at waypoint", "Controls whether the train will come to a complete stop at the waypoint.\n\nIf you are not stopping, you may still perform uncoupling orders, but you cannot perform coupling, refueling, or waiting orders.");
            }

            if (!waypoint.StopAtWaypoint && !waypoint.IsCoupling && !waypoint.SeekNearbyCoupling)
            {
                var passingSpeedField = builder.AddField($"Passing speed limit", builder.HStack((UIPanelBuilder field) =>
                {
                    field.AddLabel($"{waypoint.WaypointTargetSpeed} mph")
                            .TextWrap(TextOverflowModes.Overflow, TextWrappingModes.NoWrap)
                            .Width(100f);
                    field.AddButtonCompact("-", delegate
                    {
                        int result = Mathf.Max(waypoint.WaypointTargetSpeed - GetOffsetAmount(), 0);
                        waypoint.WaypointTargetSpeed = result;
                        onWaypointChange(waypoint);
                    }).Disable(waypoint.WaypointTargetSpeed <= 0).Width(24f);
                    field.AddButtonCompact("+", delegate
                    {
                        waypoint.WaypointTargetSpeed += GetOffsetAmount();
                        onWaypointChange(waypoint);
                    }).Width(24f);
                }));

                AddLabelOnlyTooltip(passingSpeedField, "Passing speed limit", "When passing this waypoint, the engineer will aim to be traveling at or below this speed.\n\nIf there is a track speed restriction, the engineer will not exceed that speed restriction to ensure safety.");
            }

            if (!waypoint.IsCoupling && !waypoint.IsUncoupling && !waypoint.CurrentlyWaiting)
            {
                var coupleNearbyField = builder.AddField($"Couple nearest", builder.AddToggle(() => waypoint.SeekNearbyCoupling, delegate (bool value)
                {
                    waypoint.SeekNearbyCoupling = value;
                    waypoint.StopAtWaypoint = true;
                    onWaypointChange(waypoint);
                }));

                AddLabelOnlyTooltip(coupleNearbyField, "Couple nearest", "Upon arriving at this waypoint, the engineer will couple to the nearest car within the search radius.\n\nThe nearest car is determine by track distance from the waypoint, not physical distance. You can configure the search radius in the mod settings.");
            }

            if ((waypoint.IsCoupling || waypoint.SeekNearbyCoupling) && !waypoint.CurrentlyWaiting && waypoint.StopAtWaypoint)
            {
                if (waypoint.IsCoupling)
                {
                    TrainController.Shared.TryGetCarForId(waypoint.CoupleToCarId, out Car couplingToCar);
                    builder.AddField($"Couple to ", builder.HStack(delegate (UIPanelBuilder field)
                    {
                        field.AddLabel(couplingToCar.Ident.ToString());
                    }));
                }
                else if (waypoint.SeekNearbyCoupling)
                {
                    builder.AddField($"Couple to ", builder.HStack(delegate (UIPanelBuilder field)
                    {
                        field.AddLabel("Nearest car upon arrival");
                    }));
                }

                if (Loader.Settings.UseCompactLayout)
                {
                    builder.HStack(delegate (UIPanelBuilder builder)
                    {
                        AddConnectAirAndReleaseBrakeToggles(waypoint, builder, onWaypointChange);
                    });
                }
                else
                {
                    AddConnectAirAndReleaseBrakeToggles(waypoint, builder, onWaypointChange);
                }

                if (!waypoint.ShowPostCouplingCut)
                {
                    var thenPerformCutField = builder.AddField($"Then perform cut", builder.AddToggle(() => waypoint.ShowPostCouplingCut, delegate (bool value)
                    {
                        waypoint.ShowPostCouplingCut = value;
                        onWaypointChange(waypoint);
                    }));

                    AddLabelOnlyTooltip(thenPerformCutField, "Pickup or dropoff", "Enabling this advanced option allows you to perform a cut immediately after coupling in order to pickup or dropoff cars.");
                }
                else
                {
                    var postCouplingCutField = builder.AddField($"After coupling", builder.HStack(delegate (UIPanelBuilder field)
                    {
                        string prefix = waypoint.TakeOrLeaveCut == ManagedWaypoint.PostCoupleCutType.Take ? "Pickup " : "Dropoff ";
                        AddCarCutButtons(waypoint, field, onWaypointChange, prefix);
                        field.AddButtonCompact("Swap", () =>
                        {
                            waypoint.TakeOrLeaveCut = waypoint.TakeOrLeaveCut == ManagedWaypoint.PostCoupleCutType.Take ? ManagedWaypoint.PostCoupleCutType.Leave : ManagedWaypoint.PostCoupleCutType.Take;
                            onWaypointChange(waypoint);

                        });
                        field.Spacer(8f);
                    }));

                    AddLabelOnlyTooltip(postCouplingCutField, "Pickup or dropoff", "After coupling, you can \"Pickup\" or \"Dropoff\" a number of cars relative to the car you are coupling to. " +
                    "This is very useful when queueing switching orders." +
                    "\n\nIf you couple to a cut of 3 cars and \"Pickup\" 2 cars, you will leave with the 2 closest cars and the 3rd car will be left behind. " +
                    "You \"Pickup\" cars from the cut you are coupling to." +
                    "\n\nIf you are coupling 2 additional cars to 1 car already spotted, you can \"Dropoff\" 2 cars and continue to the next queued waypoint. " +
                    "You \"Dropoff\" cars from your current consist." +
                    "\n\nIf you Pickup or Dropoff 0 cars, you will NOT perform a post-coupling cut. In other words, you will remain coupled to all cars.");

                    if (waypoint.NumberOfCarsToCut > 0)
                    {
                        if (Loader.Settings.UseCompactLayout)
                        {
                            builder.HStack(delegate (UIPanelBuilder builder)
                        {
                            AddBleedAirAndSetBrakeToggles(waypoint, builder, onWaypointChange);
                        });
                        }
                        else
                        {
                            AddBleedAirAndSetBrakeToggles(waypoint, builder, onWaypointChange);
                        }
                    }

                    if (waypoint.NumberOfCarsToCut == 0)
                    {
                        builder.AddField("", builder.HStack((UIPanelBuilder field) =>
                        {
                            field.AddLabel("Will remain coupled to ALL cars", (TMP_Text text) =>
                            {
                                text.fontStyle = FontStyles.Bold;
                            });
                        }));
                    }
                }
            }
            else if (!waypoint.CurrentlyWaiting)
            {
                builder.HStack(delegate (UIPanelBuilder builder)
                {
                    builder.AddField($"Uncouple", builder.HStack(delegate (UIPanelBuilder field)
                    {
                        AddCarCutButtons(waypoint, field, onWaypointChange, null);
                    }));
                });

                if (waypoint.IsUncoupling)
                {
                    builder.AddField($"Count cars from",
                    builder.AddDropdown(new List<string> { "Closest to waypoint", "Furthest from waypoint" }, waypoint.CountUncoupledFromNearestToWaypoint ? 0 : 1, (int value) =>
                    {
                        waypoint.CountUncoupledFromNearestToWaypoint = !waypoint.CountUncoupledFromNearestToWaypoint;
                        onWaypointChange(waypoint);
                    }));

                    if (Loader.Settings.UseCompactLayout)
                    {
                        builder.HStack(delegate (UIPanelBuilder builder)
                        {
                            AddBleedAirAndSetBrakeToggles(waypoint, builder, onWaypointChange);
                        });
                    }
                    else
                    {
                        AddBleedAirAndSetBrakeToggles(waypoint, builder, onWaypointChange);
                    }

                    var takeActiveCutField = builder.AddField($"Make uncoupled cars active", builder.AddToggle(() => waypoint.TakeUncoupledCarsAsActiveCut, delegate (bool value)
                    {
                        waypoint.TakeUncoupledCarsAsActiveCut = value;
                        onWaypointChange(waypoint);
                    }));

                    AddLabelOnlyTooltip(takeActiveCutField, "Make uncoupled cars active", "If this is active, the number of cars to uncouple will still be part of the active train. " +
                        "The rest of the train will be treated as an uncoupled cut which may bleed air and apply handbrakes. " +
                        "This is particularly useful for local freight switching." +
                        "\n\nA train of 10 cars arrives in Whittier. The 2 cars behind the locomotive need to be delivered. " +
                        "By checking \"Make uncoupled cars active\", you can order the engineer to travel to a waypoint, uncouple 4 cars including the locomotive and tender, and travel to another waypoint to the industry track to deliver the 2 cars, all while knowing that the rest of the local freight consist has handbrakes applied.");
                }
            }

            if (waypoint.CanRefuelNearby && !waypoint.CurrentlyWaiting && waypoint.StopAtWaypoint)
            {
                builder.AddField($"Refuel {waypoint.RefuelLoadName}", builder.AddToggle(() => waypoint.WillRefuel, delegate (bool value)
                {
                    waypoint.WillRefuel = value;
                    onWaypointChange(waypoint);
                }));
            }

            AddWaitingSection(waypoint, builder, onWaypointChange);
        }

        private void AddConnectAirAndReleaseBrakeToggles(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
        {
            builder.AddField("Connect air", builder.AddToggle(() => waypoint.ConnectAirOnCouple, delegate (bool value)
            {
                waypoint.ConnectAirOnCouple = value;
                onWaypointChange(waypoint);
            }));

            builder.AddField("Release handbrakes", builder.AddToggle(() => waypoint.ReleaseHandbrakesOnCouple, delegate (bool value)
            {
                waypoint.ReleaseHandbrakesOnCouple = value;
                onWaypointChange(waypoint);
            }));
        }

        private void AddBleedAirAndSetBrakeToggles(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
        {
            builder.AddField("Bleed air", builder.AddToggle(() => waypoint.BleedAirOnUncouple, delegate (bool value)
            {
                waypoint.BleedAirOnUncouple = value;
                onWaypointChange(waypoint);
            }, interactable: waypoint.NumberOfCarsToCut > 0));
            builder.AddField("Apply handbrakes", builder.AddToggle(() => waypoint.ApplyHandbrakesOnUncouple, delegate (bool value)
            {
                waypoint.ApplyHandbrakesOnUncouple = value;
                onWaypointChange(waypoint);
            }, interactable: waypoint.NumberOfCarsToCut > 0));
        }

        private void AddCarCutButtons(ManagedWaypoint waypoint, UIPanelBuilder field, Action<ManagedWaypoint> onWaypointChange, string prefix = null)
        {
            string pluralCars = waypoint.NumberOfCarsToCut == 1 ? "car" : "cars";
            field.AddLabel($"{prefix}{waypoint.NumberOfCarsToCut}")
                            .TextWrap(TextOverflowModes.Overflow, TextWrappingModes.NoWrap)
                            .Width(100f);
            field.AddButtonCompact("-", delegate
            {
                int result = Mathf.Max(waypoint.NumberOfCarsToCut - GetOffsetAmount(), 0);
                waypoint.NumberOfCarsToCut = result;
                onWaypointChange(waypoint);
            }).Disable(waypoint.NumberOfCarsToCut <= 0).Width(24f);
            field.AddButtonCompact("+", delegate
            {
                waypoint.NumberOfCarsToCut += GetOffsetAmount();
                onWaypointChange(waypoint);
            }).Width(24f);
        }
        private int GetOffsetAmount()
        {
            int offsetAmount = 1;
            if (GameInput.IsShiftDown) offsetAmount = 5;
            if (GameInput.IsControlDown) offsetAmount = 10;
            return offsetAmount;
        }

        private void AddWaitingSection(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
        {
            if (waypoint.CurrentlyWaiting)
            {
                builder.AddField("Waiting until", builder.HStack((UIPanelBuilder field) =>
                {
                    field.AddLabel($"{new GameDateTime(waypoint.WaitUntilGameTotalSeconds)}");
                    field.Spacer();
                    field.AddButtonCompact("Skip wait", delegate
                    {
                        waypoint.ClearWaiting();
                        onWaypointChange(waypoint);
                    });
                    field.Spacer(8f);
                }));
                return;
            }
            if (!waypoint.WillWait)
            {
                builder.AddField("Then wait", builder.AddToggle(() => waypoint.WillWait, (bool _) =>
                {
                    waypoint.WillWait = true;
                    onWaypointChange(waypoint);
                }));
                return;
            }
            builder.AddField("Then wait", builder.HStack((UIPanelBuilder builder) =>
            {
                builder.AddDropdown(["For a duration of time", "Until a specific time"], waypoint.DurationOrSpecificTime == ManagedWaypoint.WaitType.Duration ? 0 : 1, (int value) =>
                {
                    switch (value)
                    {
                        case 0:
                            waypoint.DurationOrSpecificTime = ManagedWaypoint.WaitType.Duration; break;
                        case 1:
                            waypoint.DurationOrSpecificTime = ManagedWaypoint.WaitType.SpecificTime; break;
                        default:
                            break;
                    }
                    onWaypointChange(waypoint);
                }).Width(200f);
            }));

            builder.Spacer(8f);

            if (waypoint.DurationOrSpecificTime == ManagedWaypoint.WaitType.Duration)
            {
                var waitForDurationField = builder.AddField("Wait for", builder.HStack((UIPanelBuilder builder) =>
                {
                    builder.VStack((UIPanelBuilder builder) =>
                    {
                        builder.HStack((UIPanelBuilder field) =>
                        {
                            field.AddInputField(waypoint.WaitUntilTimeString, (string value) =>
                            {
                                if (TryParseDurationInput(value, out int minutes))
                                {
                                    Loader.Log($"Parsed duration as {minutes}");
                                    waypoint.WaitUntilTimeString = value;
                                    waypoint.WaitForDurationMinutes = minutes;
                                    onWaypointChange(waypoint);
                                }
                                else
                                {
                                    Loader.Log($"Error parsing duration: \"{value}\"");
                                    Toast.Present("Duration must be in HH:MM, HH MM, or MM format");
                                }
                            }, placeholder: "HH:MM").Width(80f);
                            string durationLabel = BuildDurationString(waypoint.WaitForDurationMinutes);
                            field.AddLabel(durationLabel).FlexibleWidth();
                            field.AddButtonCompact("Reset", () =>
                            {
                                waypoint.WaitForDurationMinutes = 0;
                                waypoint.WaitUntilTimeString = BuildDurationString(waypoint.WaitForDurationMinutes);
                                onWaypointChange(waypoint);
                            }).Width(70f);
                        });
                        builder.Spacer(8f);
                        builder.HStack((UIPanelBuilder field) =>
                        {
                            field.AddButtonCompact("+5m", delegate
                            {
                                waypoint.WaitForDurationMinutes += 5;
                                waypoint.WaitUntilTimeString = BuildDurationString(waypoint.WaitForDurationMinutes);
                                onWaypointChange(waypoint);
                            }).Width(50f);
                            field.AddButtonCompact("+15m", () =>
                            {
                                waypoint.WaitForDurationMinutes += 15;
                                waypoint.WaitUntilTimeString = BuildDurationString(waypoint.WaitForDurationMinutes);
                                onWaypointChange(waypoint);
                            }).Width(60f);
                            field.AddButtonCompact("+30m", () =>
                            {
                                waypoint.WaitForDurationMinutes += 30;
                                waypoint.WaitUntilTimeString = BuildDurationString(waypoint.WaitForDurationMinutes);
                                onWaypointChange(waypoint);
                            }).Width(60f);
                        });
                    });
                }));

                AddLabelOnlyTooltip(waitForDurationField, "Wait for duration", "Upon arriving at this waypoint, the engineer will begin waiting for this amount of time before proceeding to the next waypoint.");
            }

            if (waypoint.DurationOrSpecificTime == ManagedWaypoint.WaitType.SpecificTime)
            {
                var waitUntilTimeField = builder.AddField("Wait until", builder.HStack((UIPanelBuilder field) =>
                {
                    field.AddInputField(waypoint.WaitUntilTimeString, (string value) =>
                    {
                        if (TimetableReader.TryParseTime(value, out TimetableTime time))
                        {
                            Loader.Log($"Parsed minutes as {time.Minutes}");
                            waypoint.WaitUntilTimeString = value;
                        }
                        else
                        {
                            Loader.Log($"Error parsing time: \"{value}\"");
                            Toast.Present("Time must be in HH:MM 24-hour format.");
                        }
                    }, placeholder: "HH:MM", 5).Width(80f);

                    field.AddDropdown(["Today", "Tomorrow"], waypoint.WaitUntilDay == ManagedWaypoint.TodayOrTomorrow.Today ? 0 : 1, (int value) =>
                    {
                        waypoint.WaitUntilDay = (ManagedWaypoint.TodayOrTomorrow)value;
                        onWaypointChange(waypoint);
                    }).Width(116f);
                }));

                AddLabelOnlyTooltip(waitUntilTimeField, "Wait until", "The engineer will not proceed to the next waypoint until the specified time has passed.\n\nIf the train arrives at the waypoint past this time already, the engineer will still come to a complete stop before proceeding to the next waypoint.");
            }
        }

        private bool TryParseDurationInput(string input, out int outputMinutes)
        {
            input = Regex.Replace(input, @"[^\d\s:]", "").Trim();

            int hours = 0;
            int minutes = 0;

            if (int.TryParse(input, out int totalMinutes))
            {
                outputMinutes = totalMinutes;
                return true;
            }
            else
            {
                Regex regex = new Regex(@"(\d+)(?:[:\s])(\d+)");
                Match match = regex.Match(input);

                if (!match.Success)
                {
                    outputMinutes = -1;
                    return false;
                }
                else
                {
                    if (match.Groups[1].Success)
                    {
                        hours = int.Parse(match.Groups[1].Value);
                    }
                    if (match.Groups[2].Success)
                    {
                        minutes = int.Parse(match.Groups[2].Value);
                    }
                    outputMinutes = hours * 60 + minutes;
                    return true;
                }
            }
        }

        private string BuildDurationString(int waitForMinutes)
        {
            int hours = waitForMinutes / 60;
            int minutes = waitForMinutes % 60;

            if (hours > 0)
            {
                return $"{hours}h {minutes}m";
            }
            return $"{minutes}m";
        }

        private void JumpCameraToWaypoint(ManagedWaypoint waypoint)
        {
            CameraSelector.shared.JumpToPoint(waypoint.Location.GetPosition(), waypoint.Location.GetRotation(), CameraSelector.CameraIdentifier.Strategy);
        }

        private static string DropdownLabelForTimetableTrain(Timetable.Train train)
        {
            MethodInfo dropdownMI = AccessTools.Method(typeof(CrewsPanelBuilder), "DropdownLabelForTimetableTrain", [typeof(Timetable.Train)]);
            string value = (string)dropdownMI.Invoke(_crewsPanelBuilder, [train]);
            return value;
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
                    var sortedTrains = timetable.Trains
                        .Values
                        .Where(t => !string.IsNullOrEmpty(t.Name))
                        .OrderBy(t => t.SortName)
                        .ToList();

                    foreach (var t in sortedTrains)
                    {
                        labels.Add(Loader.Settings.ShowTimeInTrainSymbolDropdown ? DropdownLabelForTimetableTrain(t) : t.DisplayStringLong);
                        values.Add(t.Name);   // value = symbol
                    }

                    Loader.LogDebug($"[TimetableSymbolDropdown] Loaded {sortedTrains.Count} symbols from TimetableController.Current.");
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

        private void AddLabelOnlyTooltip(IConfigurableElement element, string title, string message)
        {
            element.RectTransform.Find("Label").GetComponent<TMP_Text>().rectTransform.Tooltip(title, message);
        }
    }
}
