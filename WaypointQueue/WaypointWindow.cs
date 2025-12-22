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
using WaypointQueue.UI;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    [RequireComponent(typeof(Window))]
    public class WaypointWindow : WindowBase
    {
        public override string WindowIdentifier => "WaypointPanel";
        public override string Title => "Waypoints";

        public override Vector2Int DefaultSize => new Vector2Int(450, 500);

        public override Window.Position DefaultPosition => Window.Position.LowerRight;

        public override Window.Sizing Sizing => Window.Sizing.Resizable(DefaultSize, new Vector2Int(650, Screen.height));

        public static WaypointWindow Shared => WindowManager.Shared.GetWindow<WaypointWindow>();

        private static Dictionary<string, UIPanelBuilder> panelsByWaypointId = [];

        private static readonly Dictionary<string, List<DropdownOption>> _opsDestinationOptionsByWaypointId = [];

        private static CrewsPanelBuilder _crewsPanelBuilder => new();

        private string _selectedLocomotiveId;
        private Coroutine _coroutine;
        private float _scrollPosition;

        private void OnEnable()
        {
            WaypointQueueController.LocoWaypointStateDidUpdate += OnLocoWaypointStateDidUpdate;
            WaypointQueueController.WaypointDidUpdate += OnWaypointDidUpdate;
        }

        private void OnDisable()
        {
            WaypointQueueController.LocoWaypointStateDidUpdate -= OnLocoWaypointStateDidUpdate;
            WaypointQueueController.WaypointDidUpdate -= OnWaypointDidUpdate;
        }
        private void OnLocoWaypointStateUpdated()
        {
            Loader.LogDebug($"Rebuidling WaypointWindow in OnLocoWaypointStateUpdated");
            RebuildWithScroll();
        }

        private void OnLocoWaypointStateDidUpdate(string id)
        {
            if (id == TrainController.Shared.SelectedLocomotive?.id)
            {
                Loader.LogDebug($"Rebuilding full waypoint window for {TrainController.Shared.SelectedLocomotive?.Ident}");
                RebuildWithScroll();
            }
        }

        private void OnWaypointDidUpdate(ManagedWaypoint waypoint)
        {
            if (waypoint.LocomotiveId == TrainController.Shared.SelectedLocomotive?.id && panelsByWaypointId.TryGetValue(waypoint.Id, out UIPanelBuilder panelBuilder))
            {
                Loader.LogDebug($"Rebuilding single waypoint {waypoint.Id} for {TrainController.Shared.SelectedLocomotive.Ident}");
                panelBuilder.Rebuild();
            }
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

            panelsByWaypointId.Clear();

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
            _opsDestinationOptionsByWaypointId.Remove(waypoint.Id);
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

        internal void BuildWaypointSection(
            ManagedWaypoint waypoint,
            int index,
            int totalWaypoints,
            UIPanelBuilder parentBuilder,
            Action<ManagedWaypoint> onWaypointChange,
            Action<ManagedWaypoint> onWaypointDelete,
            Action<ManagedWaypoint, int> onWaypointReorder,
            bool isRouteWindow = false)
        {
            parentBuilder.VStack(builder =>
            {
                panelsByWaypointId[waypoint.Id] = builder;

                Loader.Log($"Building waypoint section for {waypoint.Id}, at index {index} out of {totalWaypoints}");
                builder.AddHRule();
                builder.Spacer(16f);
                builder.HStack(delegate (UIPanelBuilder builder)
                {
                    BuildWaypointItemHeader(waypoint, index, totalWaypoints, onWaypointChange, onWaypointDelete, onWaypointReorder, builder);
                });

                if (index == 0 && !isRouteWindow)
                {
                    BuildStatusLabelField(waypoint, builder);
                }

                BuildDestinationField(waypoint, builder);

                if (isRouteWindow || waypoint.Locomotive.TryGetTimetableTrainCrewId(out string trainCrewId))
                {
                    BuildTrainSymbolField(waypoint, builder, onWaypointChange);
                }

                builder.HStack(row =>
                {
                    BuildStopAtWaypointField(waypoint, row, onWaypointChange);

                    BuildSendPastWaypointField(waypoint, row, onWaypointChange);
                });

                if (!waypoint.StopAtWaypoint && !waypoint.IsCoupling)
                {
                    BuildPassingSpeedLimitField(waypoint, builder, onWaypointChange);
                }

                if (waypoint.IsCoupling && !waypoint.CurrentlyWaiting)
                {
                    BuildCouplingFieldSection(waypoint, builder, onWaypointChange);
                }

                if (!waypoint.HasAnyCouplingOrders && !waypoint.HasAnyUncouplingOrders && !waypoint.CurrentlyWaiting)
                {
                    builder.HStack(row =>
                    {
                        BuildCoupleToggle(waypoint, row, onWaypointChange);
                        BuildUncoupleToggle(waypoint, row, onWaypointChange);
                    });
                }

                if (!waypoint.HasAnyCouplingOrders && waypoint.UncouplingMode != ManagedWaypoint.UncoupleMode.None && !waypoint.CurrentlyWaiting)
                {
                    BuildUncouplingModeField(waypoint, builder, onWaypointChange);
                }

                if (!waypoint.HasAnyCouplingOrders && waypoint.WillUncoupleByCount)
                {
                    BuildUncoupleByCountField(waypoint, builder, onWaypointChange);
                }

                if (!waypoint.HasAnyCouplingOrders && waypoint.WillUncoupleByDestination)
                {
                    BuildUncoupleByDestinationField(waypoint, builder, onWaypointChange);
                }

                if (!waypoint.HasAnyCouplingOrders && waypoint.WillUncoupleBySpecificCar)
                {
                    BuildUncoupleBySpecificCarField(waypoint, builder, onWaypointChange);
                }

                if (!waypoint.IsCoupling && !waypoint.HasAnyUncouplingOrders && waypoint.CouplingSearchMode != ManagedWaypoint.CoupleSearchMode.None && !waypoint.CurrentlyWaiting)
                {
                    BuildCouplingModeField(waypoint, builder, onWaypointChange);
                }

                if (!waypoint.IsCoupling && waypoint.WillSeekSpecificCarCoupling)
                {
                    BuildCouplingSearchField(waypoint, builder, onWaypointChange);
                }

                if ((waypoint.WillSeekNearestCoupling || waypoint.WillSeekSpecificCarCoupling) && !waypoint.IsCoupling && !waypoint.CurrentlyWaiting)
                {
                    BuildCouplingFieldSection(waypoint, builder, onWaypointChange);
                }

                if (waypoint.HasAnyCouplingOrders && !waypoint.CurrentlyWaiting)
                {
                    BuildPostCouplingCutSection(waypoint, builder, onWaypointChange);
                }

                if (waypoint.CanRefuelNearby && !waypoint.CurrentlyWaiting && waypoint.StopAtWaypoint && !waypoint.HasAnyCouplingOrders && !waypoint.MoveTrainPastWaypoint)
                {
                    BuildRefuelField(waypoint, builder, onWaypointChange);
                }

                BuildWaitingSection(waypoint, builder, onWaypointChange);

                if (!string.IsNullOrEmpty(waypoint.Notes))
                {
                    builder.AddField("Notes", builder.AddLabel(waypoint.Notes));
                }
            });
        }

        private void BuildUncoupleToggle(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
        {
            builder.AddField("Then uncouple", builder.AddToggle(() => waypoint.UncouplingMode != ManagedWaypoint.UncoupleMode.None, (bool value) =>
            {
                if (value)
                {
                    waypoint.UncouplingMode = ManagedWaypoint.UncoupleMode.ByCount;
                    onWaypointChange(waypoint);
                }
            }));
        }

        private void BuildCoupleToggle(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
        {
            builder.AddField("Then couple", builder.AddToggle(() => waypoint.CouplingSearchMode != ManagedWaypoint.CoupleSearchMode.None, (bool value) =>
            {
                if (value)
                {
                    waypoint.CouplingSearchMode = ManagedWaypoint.CoupleSearchMode.SpecificCar;
                    onWaypointChange(waypoint);
                }
            }));
        }

        private void BuildWaypointItemHeader(ManagedWaypoint waypoint, int index, int totalWaypoints, Action<ManagedWaypoint> onWaypointChange, Action<ManagedWaypoint> onWaypointDelete, Action<ManagedWaypoint, int> onWaypointReorder, UIPanelBuilder builder)
        {
            string waypointName = string.IsNullOrEmpty(waypoint.Name) ? string.Empty : $" - {waypoint.Name}";
            builder.AddLabel($"Waypoint {index + 1}{waypointName}");
            builder.Spacer();
            builder.AddButtonCompact("▲", () =>
            {
                if (GameInput.IsControlDown || GameInput.IsShiftDown)
                {
                    // move to top
                    onWaypointReorder(waypoint, 0);
                }
                else
                {
                    // move up one
                    onWaypointReorder(waypoint, index - 1);
                }
            }).Width(30f).Height(30f).Disable(index == 0);

            builder.AddButtonCompact("▼", () =>
            {
                if (GameInput.IsControlDown || GameInput.IsShiftDown)
                {
                    // move to bottom
                    onWaypointReorder(waypoint, totalWaypoints);
                }
                else
                {
                    // move down one
                    // incrementing by 2 to account for index shifting after removal
                    onWaypointReorder(waypoint, index + 2);
                }
            }).Width(30f).Height(30f).Disable(index == totalWaypoints - 1);

            List<DropdownMenu.RowData> options = new List<DropdownMenu.RowData>();
            var jumpToWaypointRow = new DropdownMenu.RowData("Jump to waypoint", "");
            var makeNextWaypointRow = new DropdownMenu.RowData("Make next waypoint", "Moves this waypoint to after current waypoint");
            var editNameAndNotesRow = new DropdownMenu.RowData("Edit name and notes", "");
            var removeWaitRow = new DropdownMenu.RowData("Remove wait", "");
            var deleteWaypointRow = new DropdownMenu.RowData("Delete", "");

            options.Add(jumpToWaypointRow);

            if (index != 0)
            {
                options.Add(makeNextWaypointRow);
            }

            options.Add(editNameAndNotesRow);

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

                if (value == options.IndexOf(makeNextWaypointRow))
                {
                    onWaypointReorder(waypoint, 1);
                }

                if (value == options.IndexOf(editNameAndNotesRow))
                {
                    PresentWaypointNotesModal("Edit waypoint", "Save", waypoint.Name, waypoint.Notes, (string name, string notes) =>
                    {
                        waypoint.Name = name;
                        waypoint.Notes = notes;
                        onWaypointChange(waypoint);
                    });
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
            }).Width(30f).Height(30f);
            builder.Spacer(8f);
        }

        private static void PresentWaypointNotesModal(string title, string submitText, string editName, string editNotes, Action<string, string> onSubmit)
        {
            ModalAlertController.Present(delegate (UIPanelBuilder builder, Action dismiss)
            {
                builder.AddLabel(title, delegate (TMP_Text text)
                {
                    text.fontSize = 22f;
                    text.horizontalAlignment = HorizontalAlignmentOptions.Center;
                });
                builder.AddSection("Name", delegate (UIPanelBuilder builder)
                {
                    builder.AddInputField(editName, delegate (string newName)
                    {
                        editName = newName;
                    }, "Name");
                });
                builder.AddSection("Notes", delegate (UIPanelBuilder builder)
                {
                    builder.AddMultilineTextEditor(editNotes, "Notes", delegate (string newNotes)
                    {
                        editNotes = newNotes;
                    }, delegate
                    {
                    }).Height(200f);
                });
                builder.Spacer(16f);
                builder.AlertButtons(delegate (UIPanelBuilder builder)
                {
                    builder.AddButtonMedium("Cancel", dismiss);
                    builder.AddButtonMedium(submitText, delegate
                    {
                        onSubmit(editName.Trim(), editNotes.Trim());
                        dismiss();
                    });
                });
            }, 500);
        }

        private UIPanelBuilder BuildStatusLabelField(ManagedWaypoint waypoint, UIPanelBuilder builder)
        {
            builder.AddField("Status", builder.HStack(field =>
            {
                field.AddLabel(waypoint.StatusLabel);
            }));
            return builder;
        }

        private UIPanelBuilder BuildDestinationField(ManagedWaypoint waypoint, UIPanelBuilder builder)
        {
            builder.AddField($"Destination", builder.HStack(delegate (UIPanelBuilder field)
            {
                field.AddLabel(waypoint.AreaName?.Length > 0 ? waypoint.AreaName : "Unknown").Width(160f);
            }));
            return builder;
        }

        private UIPanelBuilder BuildTrainSymbolField(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
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
            return builder;
        }

        private UIPanelBuilder BuildStopAtWaypointField(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
        {
            var stopAtWaypointField = builder.AddField($"Stop at waypoint", builder.AddToggle(() => waypoint.StopAtWaypoint || waypoint.IsCoupling, delegate (bool value)
            {
                waypoint.StopAtWaypoint = value;
                if (!waypoint.StopAtWaypoint)
                {
                    waypoint.SetTargetSpeedToOrdersMax();
                }
                onWaypointChange(waypoint);
            }, interactable: !waypoint.IsCoupling));

            AddLabelOnlyTooltip(stopAtWaypointField, "Stop at waypoint", "Controls whether the train will come to a complete stop at the waypoint.\n\nIf you are not stopping, you may still perform uncoupling orders, but you cannot perform coupling, refueling, or waiting orders.");
            return builder;
        }

        private UIPanelBuilder BuildPassingSpeedLimitField(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
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
                field.AddButtonCompact("Kick", delegate
                {
                    waypoint.WaypointTargetSpeed = Loader.Settings.PassingSpeedForKickingCars;
                    onWaypointChange(waypoint);
                });
            }));

            AddLabelOnlyTooltip(passingSpeedField, "Passing speed limit", "When passing this waypoint, the engineer will aim to be traveling at or below this speed.\n\nIf there is a track speed restriction, the engineer will not exceed that speed restriction to ensure safety.");
            return builder;
        }

        private UIPanelBuilder BuildSendPastWaypointField(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
        {
            var sendPastWaypointField = builder.AddField("Send past waypoint", builder.AddToggle(() => waypoint.MoveTrainPastWaypoint, (bool value) =>
            {
                waypoint.MoveTrainPastWaypoint = value;
                onWaypointChange(waypoint);
            }));

            AddLabelOnlyTooltip(sendPastWaypointField, "Send past waypoint", "The engineer will attempt to move the train's length past the waypoint so that the end of the train is at the waypoint.");
            return builder;
        }

        private UIPanelBuilder BuildCouplingFieldSection(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
        {
            builder.AddField($"Couple to ", builder.HStack(delegate (UIPanelBuilder field)
            {
                string labelText = "Failed to find car";
                if (!string.IsNullOrEmpty(waypoint.CoupleToCarId) && waypoint.TryResolveCoupleToCar(out Car coupleToCar))
                {
                    labelText = coupleToCar.Ident.ToString();
                }
                else if (waypoint.CouplingSearchResultCar != null)
                {
                    labelText = waypoint.CouplingSearchResultCar.Ident.ToString();
                }
                else if (waypoint.WillSeekSpecificCarCoupling && string.IsNullOrEmpty(waypoint.CouplingSearchText))
                {
                    labelText = "None";
                }
                else if (waypoint.WillSeekNearestCoupling)
                {
                    labelText = "Nearest car upon arrival";
                }

                field.AddLabel(labelText);
            }));

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


            return builder;
        }

        private UIPanelBuilder BuildUncouplingModeField(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
        {
            var uncouplingModeField = builder.AddField($"Then uncouple", builder.AddDropdown(["None", "By count", "By track destination", "By industry destination", "By area destination", "By specific car"], (int)waypoint.UncouplingMode, (int value) =>
                {
                    waypoint.UncouplingMode = (ManagedWaypoint.UncoupleMode)value;
                    _opsDestinationOptionsByWaypointId.Remove(waypoint.Id);
                    onWaypointChange(waypoint);
                }));

            string tooltipTitle = "Uncoupling mode";
            string noneTooltipBody = "None\n\nNo cars will uncouple.";
            string byCountTooltipBody = "By count\n\nPick a number of cars to uncouple and the direction to count cars from.";

            AddLabelOnlyTooltip(uncouplingModeField, tooltipTitle, $"{noneTooltipBody}\n\n{byCountTooltipBody}");
            return builder;
        }

        private UIPanelBuilder BuildUncoupleByCountField(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
        {
            builder.HStack(delegate (UIPanelBuilder builder)
            {
                builder.AddField($"Uncouple", builder.HStack(delegate (UIPanelBuilder field)
                {
                    AddCarCutButtons(waypoint, field, onWaypointChange, null);
                }));
            });

            if (waypoint.NumberOfCarsToCut > 0)
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

                BuildMakeUncoupledCarsActiveField(waypoint, builder, onWaypointChange);
            }

            return builder;
        }

        private UIPanelBuilder BuildUncoupleByDestinationField(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
        {
            builder.AddField("Search filter", builder.AddInputField(waypoint.DestinationSearchText, placeholder: "Type to filter destination options", onApply: (string value) =>
            {
                waypoint.DestinationSearchText = value;
                _opsDestinationOptionsByWaypointId.Remove(waypoint.Id);
                onWaypointChange(waypoint);
            }));

            if (!_opsDestinationOptionsByWaypointId.TryGetValue(waypoint.Id, out var destinationChoices))
            {
                switch (waypoint.UncouplingMode)
                {
                    case ManagedWaypoint.UncoupleMode.ByDestinationTrack:
                        destinationChoices = BuildTrackDestinationMatchDropdown(waypoint);
                        break;
                    case ManagedWaypoint.UncoupleMode.ByDestinationIndustry:
                        destinationChoices = BuildIndustryDestinationMatchDropdown(waypoint);
                        break;
                    case ManagedWaypoint.UncoupleMode.ByDestinationArea:
                        destinationChoices = BuildAreaDestinationMatchDropdown(waypoint);
                        break;
                    default:
                        break;
                }
            }

            int currentIndex = destinationChoices.FindIndex(x => x.Value == waypoint.UncoupleDestinationId);

            if (currentIndex == -1)
            {
                currentIndex = 0;
            }

            builder.AddField("Matching destination", builder.AddDropdown(destinationChoices.Select(x => x.Label).ToList(), currentIndex, index =>
            {
                waypoint.UncoupleDestinationId = destinationChoices[index].Value;
                bool choiceIsEmpty = string.IsNullOrEmpty(destinationChoices[index].Value);
                waypoint.DestinationSearchText = choiceIsEmpty ? string.Empty : destinationChoices[index].Label;
                onWaypointChange(waypoint);
            }));

            var excludeMatchingCarsFromCutField = builder.AddField("Exclude match from cut", builder.AddToggle(() => waypoint.ExcludeMatchingCarsFromCut, (bool value) =>
            {
                waypoint.ExcludeMatchingCarsFromCut = value;
                onWaypointChange(waypoint);
            }));
            AddLabelOnlyTooltip(excludeMatchingCarsFromCutField, "Exclude matching cars from cut", "If this is enabled, the matching cars won't be included in the uncoupled cut." +
                "\n\nFor example, say you have a consist with a block of Whittier Sawmill SO1/SO2 cars followed by a mix of non-Whittier cars. " +
                "If you select the matching destination as Whittier Sawmill SO1/SO2 and enable this option, then the SO1/SO2 cars will be excluded from the cut. The cut will include all non-matching cars between that SO1/SO2 block and whichever train end you chose.");

            builder.AddField($"Start cut from",
            builder.AddDropdown(["Closest end to waypoint", "Furthest end from waypoint"], waypoint.CountUncoupledFromNearestToWaypoint ? 0 : 1, (int value) =>
            {
                waypoint.CountUncoupledFromNearestToWaypoint = !waypoint.CountUncoupledFromNearestToWaypoint;
                onWaypointChange(waypoint);
            }));

            builder.HStack(delegate (UIPanelBuilder builder)
            {
                AddBleedAirAndSetBrakeToggles(waypoint, builder, onWaypointChange);
            });

            BuildMakeUncoupledCarsActiveField(waypoint, builder, onWaypointChange);

            return builder;
        }

        private UIPanelBuilder BuildUncoupleBySpecificCarField(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
        {
            var searchForCarField = builder.AddField("Search for car", builder.HStack(field =>
            {
                field.AddInputField(waypoint.UncouplingSearchText ?? "", (string value) =>
                {
                    waypoint.UncouplingSearchText = value;
                    waypoint.TryResolveUncouplingSearchText(out Car foundCar);
                    onWaypointChange(waypoint);
                }, "Enter car id").FlexibleWidth();

                field.Spacer();

                field.AddButton("Clear", () =>
                {
                    waypoint.UncouplingSearchText = "";
                    waypoint.UncouplingSearchResultCar = null;
                    onWaypointChange(waypoint);
                });
            }));

            builder.Spacer(8f);

            var buttonsField = builder.AddField("", builder.HStack(field =>
            {
                field.AddButton("Select by click", () =>
                {
                    WaypointCarPicker.Shared.StartPickingCar(waypoint, onWaypointChange, forUncoupling: true);
                });
                field.AddButton("Jump to", () =>
                {
                    if (waypoint.UncouplingSearchResultCar != null)
                    {
                        CameraSelector.shared.JumpToPoint(waypoint.UncouplingSearchResultCar.OpsLocation.GetPosition(), waypoint.UncouplingSearchResultCar.OpsLocation.GetRotation(), CameraSelector.CameraIdentifier.Strategy);
                    }
                }).Disable(waypoint.UncouplingSearchResultCar == null);
            }));

            builder.Spacer(8f);

            string searchResult = "Search or select a car";
            if (waypoint.UncouplingSearchResultCar != null)
            {
                searchResult = $"Found {waypoint.UncouplingSearchResultCar.Ident}";
            }
            else if (!string.IsNullOrEmpty(waypoint.UncouplingSearchText))
            {
                searchResult = $"Cannot find \"{waypoint.UncouplingSearchText}\"";
            }
            var labelField = builder.AddField("Search result", builder.AddLabel(searchResult));

            builder.AddField($"Direction to cut",
            builder.AddDropdown(["Toward waypoint", "Away from waypoint"], waypoint.CountUncoupledFromNearestToWaypoint ? 0 : 1, (int value) =>
            {
                waypoint.CountUncoupledFromNearestToWaypoint = !waypoint.CountUncoupledFromNearestToWaypoint;
                onWaypointChange(waypoint);
            }));

            var excludeMatchingCarsFromCutField = builder.AddField("Exclude car from cut", builder.AddToggle(() => waypoint.ExcludeMatchingCarsFromCut, value =>
            {
                waypoint.ExcludeMatchingCarsFromCut = value;
                onWaypointChange(waypoint);
            }));

            AddLabelOnlyTooltip(excludeMatchingCarsFromCutField, "Exclude car from cut", "If this is enabled, the selected car will NOT be included in the uncoupled cut." +
                "\n\nThis can be useful to uncouple all cars past the locomotive tender by selecting the tender, excluding it from the cut, and choosing the appropriate direction to cut the cars.");

            builder.HStack(delegate (UIPanelBuilder builder)
            {
                AddBleedAirAndSetBrakeToggles(waypoint, builder, onWaypointChange);
            });

            BuildMakeUncoupledCarsActiveField(waypoint, builder, onWaypointChange);

            return builder;
        }

        private UIPanelBuilder BuildMakeUncoupledCarsActiveField(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
        {
            var makeUncoupledCarsActiveField = builder.AddField($"Make uncoupled cars active", builder.AddToggle(() => waypoint.TakeUncoupledCarsAsActiveCut, delegate (bool value)
            {
                waypoint.TakeUncoupledCarsAsActiveCut = value;
                onWaypointChange(waypoint);
            }));

            AddLabelOnlyTooltip(makeUncoupledCarsActiveField, "Make uncoupled cars active", "If this is active, the number of cars to uncouple will still be part of the active train. " +
                "The rest of the train will be treated as an uncoupled cut which may bleed air and apply handbrakes. " +
                "This is particularly useful for local freight switching." +
                "\n\nA train of 10 cars arrives in Whittier. The 2 cars behind the locomotive need to be delivered. " +
                "By checking \"Make uncoupled cars active\", you can order the engineer to travel to a waypoint, uncouple 4 cars including the locomotive and tender, and travel to another waypoint to the industry track to deliver the 2 cars, all while knowing that the rest of the local freight consist has handbrakes applied.");
            return builder;
        }

        private static UIPanelBuilder BuildWaitingBeforeCuttingField(UIPanelBuilder builder)
        {
            builder.AddField("", builder.HStack((UIPanelBuilder field) =>
            {
                field.AddLabel("Waiting until train is at rest before cutting cars", (TMP_Text text) =>
                {
                    text.fontStyle = FontStyles.Bold;
                });
            }));
            return builder;
        }

        private void BuildPostCouplingCutSection(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
        {
            if (!waypoint.ShowPostCouplingCut)
            {
                BuildThenPerformCutField(waypoint, builder, onWaypointChange);
            }
            else
            {
                BuildPostCouplingCutField(waypoint, builder, onWaypointChange);
            }
        }

        private UIPanelBuilder BuildThenPerformCutField(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
        {
            var thenPerformCutField = builder.AddField($"Then perform cut", builder.AddToggle(() => waypoint.ShowPostCouplingCut, delegate (bool value)
            {
                waypoint.ShowPostCouplingCut = value;
                onWaypointChange(waypoint);
            }));

            AddLabelOnlyTooltip(thenPerformCutField, "Pickup or dropoff", "Enabling this advanced option allows you to perform a cut immediately after coupling in order to pickup or dropoff cars.");
            return builder;
        }

        private UIPanelBuilder BuildPostCouplingCutField(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
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

            if (waypoint.CurrentlyWaitingBeforeCutting)
            {
                BuildWaitingBeforeCuttingField(builder);
            }

            return builder;
        }

        private UIPanelBuilder BuildCouplingModeField(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
        {
            var couplingModeField = builder.AddField($"Then couple", builder.AddDropdown(["None", "To nearest car", "To a specific car"], (int)waypoint.CouplingSearchMode, (int value) =>
                {
                    waypoint.CouplingSearchMode = (ManagedWaypoint.CoupleSearchMode)value;
                    onWaypointChange(waypoint);
                }));

            string tooltipTitle = "Coupling mode";
            string coupleNearestTooltipBody = "Nearest\n\nUpon arriving at this waypoint, the engineer will couple to the nearest car within the search radius." +
                "\n\nThe nearest car is determine by track distance from the waypoint, not physical distance. " +
                "You can configure the search radius in the mod settings.";
            string specificCarTooltipBody = "Specific car\n\nAllows you to choose a specific car to couple to after arriving at the waypoint.";

            AddLabelOnlyTooltip(couplingModeField, tooltipTitle, $"{coupleNearestTooltipBody}\n\n{specificCarTooltipBody}");
            return builder;
        }

        private UIPanelBuilder BuildCouplingSearchField(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
        {
            var searchForCarField = builder.AddField("Search for car", builder.HStack(field =>
            {
                field.AddInputField(waypoint.CouplingSearchText ?? "", (string value) =>
                {
                    waypoint.CouplingSearchText = value;
                    waypoint.TryResolveCouplingSearchText(out Car foundCar);
                    onWaypointChange(waypoint);
                }, "Enter car id").FlexibleWidth();

                field.Spacer();

                field.AddButton("Clear", () =>
                {
                    waypoint.CouplingSearchText = "";
                    waypoint.CouplingSearchResultCar = null;
                    onWaypointChange(waypoint);
                });
            }));

            builder.Spacer(8f);

            var buttonsField = builder.AddField("", builder.HStack(field =>
            {
                field.AddButton("Select by click", () =>
                {
                    WaypointCarPicker.Shared.StartPickingCar(waypoint, onWaypointChange);
                });
                field.AddButton("Jump to", () =>
                {
                    if (waypoint.CouplingSearchResultCar != null)
                    {
                        CameraSelector.shared.JumpToPoint(waypoint.CouplingSearchResultCar.OpsLocation.GetPosition(), waypoint.CouplingSearchResultCar.OpsLocation.GetRotation(), CameraSelector.CameraIdentifier.Strategy);
                    }
                }).Disable(waypoint.CouplingSearchResultCar == null);
            }));

            builder.Spacer(8f);

            string searchResult = "Search or select a car";
            if (waypoint.CouplingSearchResultCar != null)
            {
                searchResult = $"Found {waypoint.CouplingSearchResultCar.Ident}";
            }
            else if (!string.IsNullOrEmpty(waypoint.CouplingSearchText))
            {
                searchResult = $"Cannot find \"{waypoint.CouplingSearchText}\"";
            }
            var labelField = builder.AddField("Search result", builder.AddLabel(searchResult));

            if (waypoint.CouplingSearchResultCar != null && waypoint.CouplingSearchResultCar[Car.LogicalEnd.A].IsCoupled && waypoint.CouplingSearchResultCar[Car.LogicalEnd.B].IsCoupled)
            {
                builder.AddField("Warning", builder.AddLabel("Warning! Neither end of the car is currently free to couple"));
            }

            return builder;
        }

        private UIPanelBuilder BuildRefuelField(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
        {
            builder.AddField($"Refuel {waypoint.RefuelLoadName}", builder.AddToggle(() => waypoint.WillRefuel, delegate (bool value)
            {
                waypoint.WillRefuel = value;
                onWaypointChange(waypoint);
            }));

            if (waypoint.WillRefuel)
            {
                var refuelSpeedLimitField = builder.AddField($"Refuel speed limit", builder.HStack((UIPanelBuilder field) =>
                {
                    field.AddLabel($"{waypoint.RefuelingSpeedLimit} mph")
                            .TextWrap(TextOverflowModes.Overflow, TextWrappingModes.NoWrap)
                            .Width(100f);
                    field.AddButtonCompact("-", delegate
                    {
                        int result = Mathf.Max(waypoint.RefuelingSpeedLimit - GetOffsetAmount(), 1);
                        waypoint.RefuelingSpeedLimit = result;
                        onWaypointChange(waypoint);
                    }).Disable(waypoint.RefuelingSpeedLimit <= 1).Width(24f);
                    field.AddButtonCompact("+", delegate
                    {
                        waypoint.RefuelingSpeedLimit += GetOffsetAmount();
                        onWaypointChange(waypoint);
                    }).Width(24f).Disable(waypoint.RefuelingSpeedLimit >= 45);
                }));

                AddLabelOnlyTooltip(refuelSpeedLimitField, "Refuel speed limit", "The engineer will temporarily be restricted to this speed limit while repositioning the locomotive to refuel." +
                    "\n\nThe default 5 mph is generally okay, but some locomotives tend to overshoot the repositioning waypoint when coupled to multiple cars. " +
                    "The lower the speed, the more likely the engineer will accurately align the locomotive when refuel.");
            }

            return builder;
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
            }));
            builder.AddField("Apply handbrakes", builder.AddToggle(() => waypoint.ApplyHandbrakesOnUncouple, delegate (bool value)
            {
                waypoint.ApplyHandbrakesOnUncouple = value;
                onWaypointChange(waypoint);
            }));
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

        private void BuildWaitingSection(ManagedWaypoint waypoint, UIPanelBuilder builder, Action<ManagedWaypoint> onWaypointChange)
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
                builder.AddDropdown(["Remove wait", "For a duration of time", "Until a specific time"], waypoint.DurationOrSpecificTime == ManagedWaypoint.WaitType.Duration ? 1 : 2, (int value) =>
                {
                    switch (value)
                    {
                        case 0:
                            waypoint.WillWait = false;
                            break;
                        case 1:
                            waypoint.DurationOrSpecificTime = ManagedWaypoint.WaitType.Duration; break;
                        case 2:
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

                    //Loader.LogDebug($"[TimetableSymbolDropdown] Loaded {sortedTrains.Count} symbols from TimetableController.Current.");
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

        private List<DropdownOption> BuildTrackDestinationMatchDropdown(ManagedWaypoint waypoint)
        {
            Loader.LogDebug($"Building track destination options for {waypoint.Id}");
            List<DropdownOption> options = [new DropdownOption("Select a destination", ""), new DropdownOption(WaypointResolver.NoDestinationString, WaypointResolver.NoDestinationString)];

            var carPositionLookup = Traverse.Create(OpsController.Shared).Field("_carPositionLookup").GetValue<Dictionary<string, OpsCarPosition>>();

            string filter = waypoint.DestinationSearchText?.Trim().ToLower() ?? "";

            foreach (var item in carPositionLookup.Values)
            {
                if (item.Identifier.ToLower().EndsWith(".formula"))
                {
                    continue;
                }

                if (item.Identifier.ToLower().Contains(filter) || item.DisplayName.ToLower().Contains(filter))
                {
                    options.Add(new DropdownOption(item.DisplayName, item.Identifier));
                }
            }

            options = options.GroupBy(x => x.Label).Select(x => x.First()).ToList();

            _opsDestinationOptionsByWaypointId[waypoint.Id] = options;

            return options;
        }

        private List<DropdownOption> BuildIndustryDestinationMatchDropdown(ManagedWaypoint waypoint)
        {
            Loader.LogDebug($"Building industry destination options for {waypoint.Id}");
            List<DropdownOption> options = [new DropdownOption("Select a destination", ""), new DropdownOption(WaypointResolver.NoDestinationString, WaypointResolver.NoDestinationString)];

            string filter = waypoint.DestinationSearchText?.Trim().ToLower() ?? "";

            foreach (var industry in OpsController.Shared.AllIndustries)
            {
                if (industry.name.ToLower().Contains(filter) || industry.name.ToLower().Contains(filter))
                {
                    options.Add(new DropdownOption(industry.name, industry.identifier));
                }
            }

            options = options.GroupBy(x => x.Label).Select(x => x.First()).ToList();

            _opsDestinationOptionsByWaypointId[waypoint.Id] = options;

            return options;
        }

        private List<DropdownOption> BuildAreaDestinationMatchDropdown(ManagedWaypoint waypoint)
        {
            Loader.LogDebug($"Building area destination options for {waypoint.Id}");
            List<DropdownOption> options = [new DropdownOption("Select a destination", ""), new DropdownOption(WaypointResolver.NoDestinationString, WaypointResolver.NoDestinationString)];

            string filter = waypoint.DestinationSearchText?.Trim().ToLower() ?? "";

            foreach (var area in OpsController.Shared.Areas)
            {
                if (area.name.ToLower().Contains(filter) || area.name.ToLower().Contains(filter))
                {
                    options.Add(new DropdownOption(area.name, area.identifier));
                }
            }

            options = options.GroupBy(x => x.Label).Select(x => x.First()).ToList();

            _opsDestinationOptionsByWaypointId[waypoint.Id] = options;

            return options;
        }

        private void AddLabelOnlyTooltip(IConfigurableElement element, string title, string message)
        {
            element.RectTransform.Find("Label").GetComponent<TMP_Text>().rectTransform.Tooltip(title, message);
        }
    }
}
