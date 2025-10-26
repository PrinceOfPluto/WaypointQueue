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

        public override Vector2Int DefaultSize => new Vector2Int(560, 150);

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

            builder.Spacing = 8f;

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
                    builder.AddLabel($"Showing current waypoints for locomotive {selectedLocomotive.Ident}");
                    builder.Spacer();
                    builder.AddButtonCompact("Delete all", () =>
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
                    });
                });

                builder.Spacer(20f);
                for (int i = 0; i < waypointList.Count; i++)
                {
                    ManagedWaypoint waypoint = waypointList[i];
                    BuildWaypointSection(waypoint, i + 1, builder);
                }
            });
        }

        private void BuildWaypointSection(ManagedWaypoint waypoint, int number, UIPanelBuilder builder)
        {
            builder.Spacer(10f);
            builder.HStack(delegate (UIPanelBuilder builder)
            {
                builder.AddLabel($"Waypoint {number}");
                builder.Spacer();
                builder.AddButtonCompact("Remove", () => WaypointQueueController.Shared.RemoveWaypoint(waypoint));
            });

            builder.AddField($"Jump to waypoint", builder.HStack(delegate (UIPanelBuilder field)
            {
                field.AddButtonCompact($"Waypoint {number}", () => JumpCameraToWaypoint(waypoint));
            }));

            if (waypoint.IsCoupling)
            {
                TrainController.Shared.TryGetCarForId(waypoint.CoupleToCarId, out Car couplingToCar);
                builder.AddField($"Couple to ", builder.HStack(delegate (UIPanelBuilder field)
                {
                    field.AddLabel(couplingToCar.Ident.ToString());
                }));

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
            else
            {
                builder.HStack(delegate (UIPanelBuilder builder)
                {
                    string pluralCars = waypoint.NumberOfCarsToUncouple == 1 ? "car" : "cars";
                    builder.AddField($"Uncouple", builder.HStack(delegate (UIPanelBuilder field)
                    {
                        field.AddLabel($"{waypoint.NumberOfCarsToUncouple} {pluralCars}")
                            .TextWrap(TextOverflowModes.Overflow, TextWrappingModes.NoWrap)
                            .Width(120f)
                            .Height(30f);
                        field.AddButton("-", delegate
                        {
                            int result = Mathf.Max(waypoint.NumberOfCarsToUncouple - GetOffsetAmount(), 0);
                            waypoint.NumberOfCarsToUncouple = result;
                            WaypointQueueController.Shared.UpdateWaypoint(waypoint);
                        }).Disable(waypoint.NumberOfCarsToUncouple <= 0);
                        field.AddButton("+", delegate
                        {
                            waypoint.NumberOfCarsToUncouple += GetOffsetAmount();
                            WaypointQueueController.Shared.UpdateWaypoint(waypoint);
                        });

                    }));
                });

                if (waypoint.IsUncoupling)
                {
                    builder.AddField($"Start cut from", builder.HStack(delegate (UIPanelBuilder field)
                    {
                        field.AddLabel(waypoint.UncoupleNearestToWaypoint ? "Closest car to waypoint" : "Furthest car from waypoint");
                        field.AddButtonCompact("Swap", () =>
                        {
                            waypoint.UncoupleNearestToWaypoint = !waypoint.UncoupleNearestToWaypoint;
                            WaypointQueueController.Shared.UpdateWaypoint(waypoint);
                        });
                    }));
                    builder.AddField("Bleed air", builder.AddToggle(() => waypoint.BleedAirOnUncouple, delegate (bool value)
                    {
                        waypoint.BleedAirOnUncouple = value;
                        WaypointQueueController.Shared.UpdateWaypoint(waypoint);
                    }, interactable: waypoint.IsUncoupling));
                    builder.AddField("Apply handbrakes", builder.AddToggle(() => waypoint.ApplyHandbrakesOnUncouple, delegate (bool value)
                    {
                        waypoint.ApplyHandbrakesOnUncouple = value;
                        WaypointQueueController.Shared.UpdateWaypoint(waypoint);
                    }, interactable: waypoint.IsUncoupling));
                }
            }
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
