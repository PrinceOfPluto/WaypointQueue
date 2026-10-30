using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine.InputSystem;

namespace WaypointQueue.Settings
{
    [Serializable]
    public class WaypointQueueSettings
    {
        public int Version = 2;

        [NonSerialized]
        private static readonly string BindingsPath = Path.Combine(ModLocator.ModDirectory, "inputbindings.json");
        [NonSerialized]
        private static readonly string SettingsPath = Path.Combine(ModLocator.ModDirectory, "settings.json");

        [Serializable]
        internal struct InputBindingOverrideData
        {
            public string ActionName;
            public int BindingIndex;
            public string OverridePath;
        }

        [NonSerialized]
        public ModInputActions Input;

        public enum CoupleSearchModeDefaultOptions
        {
            Nearest = 1,
            BySpecificCar = 2,
        }

        public enum UncoupleModeDefaultOptions
        {
            ByCount = 1,
            ByDestinationArea = 2,
            ByDestinationIndustry = 3,
            ByDestinationTrack = 4,
            BySpecificCar = 5,
            AllExceptLocomotives = 6,
        }

        public enum PostCoupleCutModeDefaultOptions
        {
            Pickup = 0,
            Dropoff = 1,
        }

        // Show time info in dropdown for timetable train symbol
        public bool ShowTimeInTrainSymbolDropdown = true;

        // Connect air by default when coupling
        public bool ConnectAirByDefault = true;

        // Release handbrakes by default when coupling
        public bool ReleaseHandbrakesByDefault = true;

        // Nearby coupling search distance in car lengths
        // DrawType.Slider, Precision = 0, Min = 1, Max = 100
        // Distance to search for nearby coupling. Measured in car lengths of about 40 ft each
        public float NearbyCouplingSearchDistanceInCarLengths = 10;

        public CoupleSearchModeDefaultOptions DefaultCouplingSearchMode = CoupleSearchModeDefaultOptions.Nearest;

        // Bleed air cylinders by default when uncoupling
        public bool BleedAirByDefault = true;

        // Leave anglecock open by default after cutting cars (do not bottle air)
        public bool DoNotBottleAir = false;

        //Apply handbrakes by default when uncoupling
        public bool ApplyHandbrakesByDefault = true;

        // Max seconds to wait for train to fully stop before uncoupling
        // DrawType.Slider, Precision = 0, Min = 5, Max = 120
        // Trains will wait to uncouple until the consist is considered at fully rest or until this many seconds have passed, whichever happens sooner
        // If you are encountering trains unexpectedly re-coupling after being uncoupled, it is recommended to increase this timeout value
        public float WaitBeforeCuttingTimeout = 15f;

        // Handbrake percentage
        // DrawType.Slider, Precision = 2, Min = 0, Max = 1
        // Handbrakes will be set on this percentage of uncoupled cars
        public float HandbrakePercentOnUncouple = 0.1f;

        // Handbrake minimum
        // At least this amount of handbrakes will always be set on uncoupled cars regardless of cut length
        public int MinimumHandbrakesOnUncouple = 2;

        public UncoupleModeDefaultOptions DefaultUncouplingMode = UncoupleModeDefaultOptions.ByCount;

        public PostCoupleCutModeDefaultOptions DefaultPostCouplingCutMode = PostCoupleCutModeDefaultOptions.Pickup;

        public UncoupleModeDefaultOptions DefaultPostCouplingCutUncouplingMode = UncoupleModeDefaultOptions.ByCount;

        public int PassingSpeedForKickingCars = 7;

        // Kick button unchecks bleeding air and applying handbrakes on uncouple
        public bool UncheckAirAndBrakesForKick = true;

        // Do not automatically set passing speed limit when not stopping at waypoint
        public bool DoNotLimitPassingSpeedDefault = true;

        // Prevent tenders being uncoupled from locomotive
        public bool PreventTenderUncoupling = true;

        public bool UseLegacyHotkeyOptions = false;

        public static WaypointQueueSettings Load()
        {
            WaypointQueueSettings settings = null;

            ModLocator.Loader.Log("Checking for WPQ settings");
            if (File.Exists(SettingsPath))
            {
                try
                {
                    string json = File.ReadAllText(SettingsPath);
                    settings = JsonConvert.DeserializeObject<WaypointQueueSettings>(json);
                    ModLocator.Loader.Log("Successfully loaded WPQ settings");
                }
                catch (System.Exception)
                {
                }
            }

            if (settings == null)
            {
                ModLocator.Loader.Log("Unable to find WPQ settings file, generating new settings");
                settings = new WaypointQueueSettings();
            }

            settings.InitializeInputs();

            return settings;
        }

        public void InitializeInputs()
        {
            if (Input != null)
            {
                Input.Dispose();
            }

            var actions = InputActionAssetFactory.CreateAsset();
            Input = new ModInputActions(actions);

            try
            {
                LoadInputBindings();
            }
            catch (Exception e)
            {
                ModLocator.Loader.LogError(e, "Failed to load input bindings");
                throw;
            }
            Input.Enable();
        }

        private void LoadInputBindings()
        {
            bool loaded = false;
            ModLocator.Loader.Log("Checking for WPQ input bindings file");
            if (File.Exists(BindingsPath))
            {
                string overridesJson = File.ReadAllText(BindingsPath);
                if (!string.IsNullOrWhiteSpace(overridesJson))
                {
                    var overrideData = JsonConvert.DeserializeObject<List<InputBindingOverrideData>>(overridesJson);
                    foreach (var entry in overrideData)
                    {
                        //ModLocator.Loader.Log($"Searching for action {entry.ActionName}");
                        var action = Input.map.FindAction(entry.ActionName, true);
                        if (action != null && action.bindings.Count > entry.BindingIndex)
                        {
                            //ModLocator.Loader.Log($"Found action {entry.ActionName}. Applying binding override path {entry.OverridePath} to {entry.BindingIndex}");
                            action.ApplyBindingOverride(entry.BindingIndex, entry.OverridePath);
                        }
                    }
                    //Input.actions.LoadBindingOverridesFromJson(overridesJson);
                    ModLocator.Loader.Log("Loaded WPQ input bindings from disk");
                    loaded = true;
                }
                else
                {
                    ModLocator.Loader.Log("Found empty WPQ input bindings file");
                }
            }

            if (UseLegacyHotkeyOptions)
            {
                ModLocator.Loader.Log("UseLegacyHotkeyOptions was enabled, attempting to validate bindings");
                ValidateLegacyHotkeyBindings();
            }
            else
            {
                ModLocator.Loader.Log("UseLegacyHotkeyOptions was disabled, attempting to validate bindings");
                ValidateShortcutHotkeyBindings();
            }

            if (!loaded)
            {
                ModLocator.Loader.Log("Cannot find any existing WPQ input bindings file on disk");
            }
        }

        private void ValidateLegacyHotkeyBindings()
        {
            if (!HasBindingPath(Input.appendWaypointMode) || !HasBindingPath(Input.replaceWaypointMode) || !HasBindingPath(Input.insertNextWaypointMode))
            {
                ModLocator.Loader.Log("UseLegacyHotkeyOptions was enabled, but one or more legacy actions are unbounded. Enabling default legacy hotkeys");
                Input.EnableLegacyHotkeys();
            }
            else
            {
                ModLocator.Loader.Log("Validated legacy hotkey bindings");
            }
        }

        private void ValidateShortcutHotkeyBindings()
        {
            if (!HasBindingPath(Input.coupleToNearestShortcut) || !HasBindingPath(Input.uncoupleShortcut) || !HasBindingPath(Input.kickingShortcut))
            {
                ModLocator.Loader.Log("UseLegacyHotkeyOptions was disabled, but one or more shortcut actions are unbounded. Enabling default shortcut hotkeys");
                Input.EnableShortcutHotkeys();
            }
            else
            {
                ModLocator.Loader.Log("Validated shortcut hotkey bindings");
            }
        }

        private bool HasBindingPath(InputAction action)
        {
            if (action == null) return false;
            foreach (var binding in action.bindings)
            {
                if (!string.IsNullOrEmpty(binding.effectivePath) && binding.effectivePath != "disabled")
                {
                    ModLocator.Loader.Log($"Binding path found for action {action.name} - {binding.effectivePath}");
                    return true;
                }
            }
            ModLocator.Loader.Log($"No binding path found for action {action.name}");
            return false;
        }

        private void SaveInputBindings()
        {
            if (Input != null && Input.actions != null)
            {
                ModLocator.Loader.Log("Saving WPQ input bindings to disk");
                var dataList = new List<InputBindingOverrideData>();
                foreach (var action in Input.map.actions)
                {
                    //ModLocator.Loader.Log($"Checking action {action.name} for save with {action.bindings.Count} bindings");
                    for (int i = 0; i < action.bindings.Count; i++)
                    {
                        var path = action.bindings[i].overridePath;
                        if (!string.IsNullOrEmpty(path))
                        {
                            //ModLocator.Loader.Log($"Adding binding data for action {action.name} with binding index {i} and override path {path}");
                            dataList.Add(new InputBindingOverrideData
                            {
                                ActionName = action.name,
                                BindingIndex = i,
                                OverridePath = path
                            });
                        }
                    }
                }
                ModLocator.Loader.Log($"Saving {dataList.Count} bindings to json");
                string overridesJson = JsonConvert.SerializeObject(dataList, Formatting.Indented);
                File.WriteAllText(BindingsPath, overridesJson);
            }
        }

        public void Save()
        {
            ModLocator.Loader.Log("Saving WPQ settings to disk");
            string json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(SettingsPath, json);

            SaveInputBindings();
        }

        public void ResetToDefaults()
        {
            Input.Dispose();
            var newSettings = CleanSettingsReset();
            newSettings.Save();
            ModLocator.Settings = newSettings;
        }

        private static WaypointQueueSettings CleanSettingsReset()
        {
            if (File.Exists(SettingsPath))
                File.Delete(SettingsPath);
            if (File.Exists(BindingsPath))
                File.Delete(BindingsPath);

            var newSettings = new WaypointQueueSettings();
            newSettings.InitializeInputs();
            return newSettings;
        }
    }
}
