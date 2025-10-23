using Model;
using System.Collections.Generic;
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

        public void Show()
        {
            Loader.LogDebug($"Rebuilding and showing waypoint panel");
            Rebuild();
            var rect = GetComponent<RectTransform>();
            rect.position = new Vector2((float)Screen.width, (float)Screen.height - 40);
            Shared.Window.ShowWindow();
        }

        public static void Toggle()
        {
            Loader.LogDebug($"Toggling waypoint panel");
            if (Shared.Window.IsShown)
            {
                Loader.LogDebug($"Toggling waypoint panel closed");
                Shared.Window.CloseWindow();
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
                if (selectedLocomotive == null)
                {
                    builder.AddLabel("No locomotive is currently selected").HorizontalTextAlignment(TMPro.HorizontalAlignmentOptions.Center);
                    return;
                }

                List<AdvancedWaypoint> waypointList = WaypointQueueController.Shared.GetWaypointList(selectedLocomotive);

                if (waypointList == null || waypointList.Count == 0)
                {
                    builder.AddLabel($"Locomotive {selectedLocomotive.Ident} has no waypoints.").HorizontalTextAlignment(TMPro.HorizontalAlignmentOptions.Center);
                    return;
                }

                builder.HStack(delegate (UIPanelBuilder builder)
                {
                    builder.AddLabel($"Showing current waypoints for locomotive {selectedLocomotive.Ident}");
                    builder.Spacer();
                    builder.AddButtonCompact("Clear all", () => WaypointQueueController.Shared.ClearWaypointState(selectedLocomotive));
                });

                builder.Spacer(8f);
                for (int i = 0; i < waypointList.Count; i++)
                {
                    AdvancedWaypoint waypoint = waypointList[i];
                    BuildWaypointSection(waypoint, i + 1, builder);
                }
            });
        }

        private void BuildWaypointSection(AdvancedWaypoint waypoint, int number, UIPanelBuilder builder)
        {
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
                    builder.AddField($"Uncouple ", builder.HStack(delegate (UIPanelBuilder field)
                    {
                        field.AddLabel($"{waypoint.NumberOfCarsToUncouple} {pluralCars}");
                    }));
                    builder.AddButtonCompact("-", delegate
                    {
                        waypoint.NumberOfCarsToUncouple -= 1;
                        WaypointQueueController.Shared.UpdateWaypoint(waypoint);
                    }).Disable(waypoint.NumberOfCarsToUncouple <= 0);
                    builder.AddButtonCompact("+", delegate
                    {
                        waypoint.NumberOfCarsToUncouple += 1;
                        WaypointQueueController.Shared.UpdateWaypoint(waypoint);
                    });
                });

                if (waypoint.IsUncoupling)
                {
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

        private void JumpCameraToWaypoint(AdvancedWaypoint waypoint)
        {
            CameraSelector.shared.JumpToPoint(waypoint.Location.GetPosition(), waypoint.Location.GetRotation(), CameraSelector.CameraIdentifier.Strategy);
        }
    }
}
