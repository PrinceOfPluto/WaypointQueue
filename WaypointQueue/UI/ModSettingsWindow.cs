using System.Collections.Generic;
using System.Linq;
using UI;
using UI.Builder;
using UI.Common;
using UI.InputRebind;
using UI.Windows;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;
using WaypointQueue.Settings;

namespace WaypointQueue.UI
{
    public class ModSettingsWindow : MonoBehaviour
    {
        public string Identifier => "WaypointQueueSettings";

        protected string Title => "Waypoint Queue Settings";
        public Vector2Int DefaultSize => new Vector2Int(800, 560);

        public WindowConfig Config => WindowConfig.Resizable(DefaultSize, DefaultSize);

        private HashSet<InputAction> _conflicts = [];

        private UIPanelBuilder _keybindBuilder;

        private UIPanelBuilder _builder;

        private InputAction[] RebindableActions => ModLocator.Settings.Input.GetRebindableActions();

        private WaypointQueueSettings Settings => ModLocator.Settings;

        private CustomWindow _customWindow;

        public void Register()
        {
            var windowDef = new CustomWindowDefinition
            {
                Title = Title,
                Identifier = Identifier,
                Config = Config,
                BuilderAction = BuildContent,
            };
            WindowManager.Shared.RegisterCustomWindow(windowDef);
        }

        protected void BuildContent(UIPanelBuilder builder)
        {
            _builder = builder;
            builder.VScrollView(builder =>
            {
                builder.FieldLabelWidth = 200f;
                builder.AddSection("Keybindings");
                BuildKeybindingSection(builder);

                builder.Spacer(16f);

                builder.AddSection("Brake system");
                BuildBrakeSystemSection(builder);

                builder.Spacer(16f);

                builder.AddSection("Coupling options");
                BuildCouplingBehaviorSection(builder);

                builder.Spacer(16f);

                builder.AddSection("Uncoupling options");
                BuildUncouplingBehaviorSection(builder);

                builder.Spacer(16f);

                builder.AddSection("Post-coupling cut options");
                BuildPostCouplingCutSection(builder);

                builder.Spacer(16f);

                builder.AddSection("Speed options");
                BuildSpeedOptionsSection(builder);

                builder.Spacer(16f);
                builder.AddSection("Reset");
                BuildResetToDefaultSection(builder);
            });
        }

        public void Toggle()
        {
            WindowManager.Shared.ToggleWindow(Identifier);

            if (_customWindow == null)
            {
                _customWindow = WindowManager.Shared.GetCustomWindow(Identifier);
            }
        }

        private void Save()
        {
            Settings.Save();
        }

        private void BuildResetToDefaultSection(UIPanelBuilder builder)
        {
            builder.AddButtonMedium("Reset to defaults", () =>
            {
                ModalAlertController.Present($"Are you sure you want to reset all Waypoint Queue Settings to default?", "This cannot be undone!", [(true, "Yes"), (false, "No")], b =>
                {
                    if (b)
                    {
                        ModLocator.Settings.ResetToDefaults();
                        _customWindow.Rebuild();
                    }
                });
            });
        }

        private void BuildBrakeSystemSection(UIPanelBuilder builder)
        {
            builder.HStack(row =>
            {
                row.AddToggle(() => Settings.ConnectAirByDefault, (bool val) =>
                {
                    Settings.ConnectAirByDefault = val;
                    Save();
                });
                row.Spacer(8f);
                row.AddLabel("Connect air by default when coupling");
            });

            builder.Spacer(8f);
            builder.HStack(row =>
            {
                row.AddToggle(() => Settings.ReleaseHandbrakesByDefault, (bool val) =>
                {
                    Settings.ReleaseHandbrakesByDefault = val;
                    Save();
                });
                row.Spacer(8f);
                row.AddLabel("Release handbrakes by default when coupling");
            });

            builder.Spacer(8f);
            builder.HStack(row =>
            {
                row.AddToggle(() => Settings.BleedAirByDefault, val =>
                {
                    Settings.BleedAirByDefault = val;
                    Save();
                });
                row.Spacer(8f);
                row.AddLabel("Bleed air cylinders by default when uncoupling");
            });

            builder.Spacer(8f);
            builder.HStack(row =>
            {
                row.AddToggle(() => Settings.DoNotBottleAir, val =>
                {
                    Settings.DoNotBottleAir = val;
                    Save();
                });
                row.Spacer(8f);
                row.AddLabel("Leave anglecock open by default after cutting cars (do not bottle air)");
            });

            builder.Spacer(8f);
            builder.HStack(row =>
            {
                row.AddToggle(() => Settings.UncheckAirAndBrakesForKick, val => { Settings.UncheckAirAndBrakesForKick = val; Save(); });
                row.Spacer(8f);
                row.AddLabel("Kick button unchecks bleeding air and applying handbrakes on uncouple");
            });

            builder.Spacer(8f);
            builder.HStack(row =>
            {
                row.AddToggle(() => Settings.ApplyHandbrakesByDefault, val => { Settings.ApplyHandbrakesByDefault = val; Save(); });
                row.Spacer(8f);
                row.AddLabel("Apply handbrakes by default when uncoupling");
            });

            builder.Spacer(16f);
            builder.AddLabel("Percentage of uncoupled cars to set handbrakes on");
            builder.AddSliderQuantized(
                    () => Settings.HandbrakePercentOnUncouple * 100,
                    () => $"{Settings.HandbrakePercentOnUncouple * 100}%",
                    val => { Settings.HandbrakePercentOnUncouple = val / 100; Save(); },
                    increment: 1, minValue: 0, maxValue: 100
                );

            builder.Spacer(16f);
            builder.AddLabel("Mandatory minimum handbrakes to set regardless of cut length");
            builder.AddSliderQuantized(
                    () => Settings.MinimumHandbrakesOnUncouple,
                    () => $"{Settings.MinimumHandbrakesOnUncouple}",
                    val => { Settings.MinimumHandbrakesOnUncouple = (int)val; Save(); },
                    increment: 1, minValue: 0, maxValue: 20
                );
        }

        private void BuildCouplingBehaviorSection(UIPanelBuilder builder)
        {
            builder.AddLabel("Distance to search for nearby coupling, measured in car lengths");
            builder.AddSliderQuantized(
                () => Settings.NearbyCouplingSearchDistanceInCarLengths,
                () => Settings.NearbyCouplingSearchDistanceInCarLengths.ToString(),
                (float val) =>
                {
                    Settings.NearbyCouplingSearchDistanceInCarLengths = val;
                    Save();
                },
                increment: 1, minValue: 1, maxValue: 100);

            builder.Spacer(8f);
            builder.HStack(row =>
            {
                row.AddLabel("Default coupling mode");
                row.Spacer(8f);
                row.AddDropdown(["Nearest car", "Specific car"], (int)Settings.DefaultCouplingSearchMode, (int val) =>
                {
                    // + 1 for correct enum
                    Settings.DefaultCouplingSearchMode = (WaypointQueueSettings.CoupleSearchModeDefaultOptions)val + 1;
                    Save();
                }).FlexibleWidth();
            });
        }

        private void BuildUncouplingBehaviorSection(UIPanelBuilder builder)
        {
            builder.HStack(row =>
            {
                row.AddLabel("Default uncoupling mode");
                row.Spacer(8f);
                row.AddDropdown(["By count", "By destination area", "By destination industry", "By destination track", "By specific car", "All except locomotives"], (int)Settings.DefaultUncouplingMode, (int val) =>
                {
                    // + 1 for correct enum
                    Settings.DefaultUncouplingMode = (WaypointQueueSettings.UncoupleModeDefaultOptions)val + 1;
                    Save();
                }).FlexibleWidth();
            });
            builder.Spacer(16f);

            builder.HStack(row =>
            {
                row.AddToggle(() => Settings.PreventTenderUncoupling, (bool val) =>
                {
                    Settings.PreventTenderUncoupling = val;
                    Save();
                });
                row.Spacer(8f);
                row.AddLabel("Prevent tenders being uncoupled from locomotive");
            });
            builder.Spacer(16f);


            builder.AddLabel("Max seconds to wait for train to fully stop before uncoupling");
            builder.Spacer(8f);
            builder.AddSliderQuantized(
                () => Settings.WaitBeforeCuttingTimeout,
                () => Settings.WaitBeforeCuttingTimeout.ToString(),
                (float val) => { Settings.WaitBeforeCuttingTimeout = val; Save(); },
                increment: 1, minValue: 5, maxValue: 120);

            builder.Spacer(8f);
            builder.AddLabel("Trains will wait to uncouple until the consist is considered at fully rest or until this many seconds have passed, whichever happens sooner. If you are encountering trains unexpectedly re-coupling after being uncoupled, it is recommended to increase this timeout value.");

            builder.Spacer(16f);
        }

        private void BuildPostCouplingCutSection(UIPanelBuilder builder)
        {
            builder.HStack(row =>
            {
                row.AddLabel("Default post-coupling cut mode");
                row.Spacer(8f);
                row.AddDropdown(["Pickup", "Dropoff"], (int)Settings.DefaultPostCouplingCutMode, (int val) =>
                {
                    // enum starts at 0 so no + 1
                    Settings.DefaultPostCouplingCutMode = (WaypointQueueSettings.PostCoupleCutModeDefaultOptions)val;
                    Save();
                }).FlexibleWidth();
            });

            builder.Spacer(8f);
            builder.HStack(row =>
            {
                row.AddLabel("Default uncoupling mode when performing post-coupling cuts");
                row.Spacer(8f);
                row.AddDropdown(["By count", "By destination area", "By destination industry", "By destination track", "By specific car", "All except locomotives"], (int)Settings.DefaultPostCouplingCutUncouplingMode, (int val) =>
                {
                    // + 1 for correct enum
                    Settings.DefaultPostCouplingCutUncouplingMode = (WaypointQueueSettings.UncoupleModeDefaultOptions)val + 1;
                    Save();
                }).FlexibleWidth();
            });
        }

        private void BuildSpeedOptionsSection(UIPanelBuilder builder)
        {
            builder.HStack(row =>
            {
                row.AddToggle(() => Settings.DoNotLimitPassingSpeedDefault, (bool val) =>
                {
                    Settings.DoNotLimitPassingSpeedDefault = val;
                    Save();
                });
                row.Spacer(8f);
                row.AddLabel("Do not automatically set passing speed limit when not stopping at waypoint");
            });

            builder.Spacer(16f);
            builder.AddLabel("Passing speed for kicking cars");
            builder.Spacer(8f);
            builder.AddSliderQuantized(
                () => Settings.PassingSpeedForKickingCars,
                () => $"{Settings.PassingSpeedForKickingCars} mph",
                (float val) => { Settings.PassingSpeedForKickingCars = (int)val; Save(); },
                increment: 1, minValue: 0, maxValue: 100);
        }

        private void BuildKeybindingSection(UIPanelBuilder builder)
        {
            HashSet<InputAction> conflicts = FindConflicts(RebindableActions);
            _conflicts = conflicts;
            _keybindBuilder = builder;

            InputAction[] readonlyArray = RebindableActions;
            foreach (var inputAction in readonlyArray)
            {
                builder.AddInputBindingControl(inputAction, conflicts.Contains(inputAction), DidRebind);
            }

            builder.AddField("Use legacy hotkey options", builder.AddToggle(() => Settings.UseLegacyHotkeyOptions, (bool val) =>
            {
                Settings.UseLegacyHotkeyOptions = val;
                if (val)
                {
                    Settings.Input.EnableLegacyHotkeys();
                }
                else
                {
                    Settings.Input.EnableShortcutHotkeys();
                }
                Save();
                _customWindow.Rebuild();
            }));
        }

        private void DidRebind()
        {
            if (!FindConflicts(RebindableActions).Equals(_conflicts))
            {
                ModLocator.Loader.LogDebug("Conflicts changed, rebuilding");
                _keybindBuilder.Rebuild();
            }
            Save();
        }

        private static HashSet<InputAction> FindConflicts(IEnumerable<InputAction> actions)
        {
            HashSet<InputAction> hashSet = new HashSet<InputAction>();
            GameInput shared = GameInput.Shared;
            foreach (IGrouping<string, InputAction> item in actions.Where((InputAction a) => a.bindings.Count > 0).GroupBy(UniquingKey))
            {
                InputAction[] array = item.ToArray();
                for (int i = 0; i < array.Length; i++)
                {
                    for (int j = i + 1; j < array.Length; j++)
                    {
                        if (shared.CanConflictWith(array[i], array[j]))
                        {
                            hashSet.Add(array[i]);
                            hashSet.Add(array[j]);
                        }
                    }
                }
            }

            return hashSet;
            static string UniquingKey(InputAction action)
            {
                ReadOnlyArray<InputBinding> bindings = action.bindings;
                List<string> list = new List<string>();
                for (int num = 0; num < bindings.Count; num = BindingChord.GroupEnd(bindings, num))
                {
                    list.Add(BindingChord.Signature(bindings, num));
                }

                return string.Join(" ", list);
            }
        }
    }
}
