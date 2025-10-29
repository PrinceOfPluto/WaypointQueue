using Game.Messages;
using Game.State;
using Model;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UI;
using UI.Builder;
using UI.Common;
using UnityEngine;
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

        private string _selectedLocomotiveId;
        private Coroutine _coroutine;

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
            Loader.LogDebug($"WaypointWindow OnWaypointsUpdated");
            Rebuild();
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
                Rebuild();
            }
        }

        public void Show()
        {
            Loader.LogDebug($"Rebuilding and showing waypoint panel");
            Rebuild();
            var rect = GetComponent<RectTransform>();
            rect.position = new Vector2((float)Screen.width, (float)Screen.height - 40);
            Shared.Window.ShowWindow();

            if (_coroutine == null)
            {
                Loader.LogDebug($"Starting waypoint window coroutine");
                _coroutine = StartCoroutine(Ticker());
            }
        }

        public void Hide()
        {
            Shared.Show();

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
                                Rebuild();
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

        private void BuildWaypointSection(ManagedWaypoint waypoint, int number, UIPanelBuilder builder)
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

                builder.AddField($"After coupling", builder.HStack(delegate (UIPanelBuilder field)
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
                }
            }
        }

        private void AddConnectAirAndReleaseBrakeToggles(ManagedWaypoint waypoint, UIPanelBuilder builder)
        {
            builder.AddField("Connect air", builder.AddToggle(() => waypoint.ConnectAirOnCouple, delegate (bool value)
            {
                waypoint.ConnectAirOnCouple = value;
                WaypointQueueController.Shared.UpdateWaypoint(waypoint);
            }));

            builder.AddField("Release handbrakes", builder.AddToggle(() => waypoint.ReleaseHandbrakesOnCouple, delegate (bool value)
            {
                waypoint.ReleaseHandbrakesOnCouple = value;
                WaypointQueueController.Shared.UpdateWaypoint(waypoint);
            }));
        }

        private void AddBleedAirAndSetBrakeToggles(ManagedWaypoint waypoint, UIPanelBuilder builder)
        {
            builder.AddField("Bleed air", builder.AddToggle(() => waypoint.BleedAirOnUncouple, delegate (bool value)
            {
                waypoint.BleedAirOnUncouple = value;
                WaypointQueueController.Shared.UpdateWaypoint(waypoint);
            }, interactable: waypoint.NumberOfCarsToCut > 0));
            builder.AddField("Apply handbrakes", builder.AddToggle(() => waypoint.ApplyHandbrakesOnUncouple, delegate (bool value)
            {
                waypoint.ApplyHandbrakesOnUncouple = value;
                WaypointQueueController.Shared.UpdateWaypoint(waypoint);
            }, interactable: waypoint.NumberOfCarsToCut > 0));
        }

        private void AddCarCutButtons(ManagedWaypoint waypoint, UIPanelBuilder field, string prefix = null)
        {
            string pluralCars = waypoint.NumberOfCarsToCut == 1 ? "car" : "cars";
            field.AddLabel($"{prefix}{waypoint.NumberOfCarsToCut} {pluralCars}")
                            .TextWrap(TextOverflowModes.Overflow, TextWrappingModes.NoWrap)
                            .Width(100f);
            field.AddButtonCompact("-", delegate
            {
                int result = Mathf.Max(waypoint.NumberOfCarsToCut - GetOffsetAmount(), 0);
                waypoint.NumberOfCarsToCut = result;
                WaypointQueueController.Shared.UpdateWaypoint(waypoint);
            }).Disable(waypoint.NumberOfCarsToCut <= 0).Width(24f);
            field.AddButtonCompact("+", delegate
            {
                waypoint.NumberOfCarsToCut += GetOffsetAmount();
                WaypointQueueController.Shared.UpdateWaypoint(waypoint);
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
    }
}
