using System;
using UnityEngine.InputSystem;

namespace WaypointQueue.Settings
{
    [Serializable]
    public class ModInputActions : IDisposable
    {
        public InputActionAsset actions;

        public InputActionMap map;

        // Setting a waypoint with this activated will add it to the end of the locomotive's waypoint queue
        public InputAction appendWaypointMode;

        // Setting a waypoint with this activated will replace the current waypoint but keep the rest of the existing waypoint queue
        public InputAction replaceWaypointMode;

        // Setting a waypoint with this activated will insert it in the queue directly after the current waypoint
        public InputAction insertNextWaypointMode;

        // Setting a waypoint with this activated will create a waypoint with couple to nearest enabled
        public InputAction coupleToNearestShortcut;

        // Setting a waypoint with this activated will create a waypoint with uncoupling enabled
        public InputAction uncoupleShortcut;

        // Setting a waypoint with this activated will create a waypoint with kicking enabled
        public InputAction kickingShortcut;

        public InputAction toggleWaypointPanel;

        public InputAction toggleRoutesPanel;

        private static readonly string LeftCtrl = "<Keyboard>/leftCtrl";
        private static readonly string LeftAlt = "<Keyboard>/leftAlt";
        private static readonly string LeftShift = "<Keyboard>/leftShift";

        public ModInputActions(InputActionAsset asset)
        {
            actions = asset;
            map = asset.FindActionMap("WaypointQueue", true);

            toggleWaypointPanel = map.FindAction("ToggleWaypointPanel", true);
            toggleRoutesPanel = map.FindAction("ToggleRoutesPanel", true);
            appendWaypointMode = map.FindAction("AppendWaypointMode", true);
            replaceWaypointMode = map.FindAction("ReplaceWaypointMode", true);
            insertNextWaypointMode = map.FindAction("InsertNextWaypointMode", true);
            coupleToNearestShortcut = map.FindAction("CoupleToNearestShortcut", true);
            uncoupleShortcut = map.FindAction("UncoupleShortcut", true);
            kickingShortcut = map.FindAction("KickingShortcut", true);
            toggleWaypointPanel = map.FindAction("ToggleWaypointPanel", true);
            toggleRoutesPanel = map.FindAction("ToggleRoutesPanel", true);
        }

        public InputAction[] GetRebindableActions()
        {
            if (ModLocator.Settings.UseLegacyHotkeyOptions)
            {
                return [appendWaypointMode, replaceWaypointMode, insertNextWaypointMode, toggleWaypointPanel, toggleRoutesPanel];
            }
            else
            {
                return [coupleToNearestShortcut, uncoupleShortcut, kickingShortcut, toggleWaypointPanel, toggleRoutesPanel];
            }
        }

        private void SetBinding(InputAction action, string binding)
        {
            if (action == null) return;
            if (action.bindings.Count == 0)
                action.AddBinding("");

            action.ApplyBindingOverride(0, binding);
        }

        private void ClearBindings(InputAction action)
        {
            if (action == null) return;
            action.RemoveAllBindingOverrides();

            for (int i = 0; i < action.bindings.Count; i++)
            {
                action.ApplyBindingOverride(i, "");
            }
        }

        public void EnableLegacyHotkeys()
        {
            ModLocator.Loader.Log("Enabling legacy hotkeys");
            SetBinding(appendWaypointMode, LeftCtrl);
            SetBinding(replaceWaypointMode, LeftAlt);
            SetBinding(insertNextWaypointMode, LeftShift);

            ClearBindings(coupleToNearestShortcut);
            ClearBindings(uncoupleShortcut);
            ClearBindings(kickingShortcut);
        }

        public void EnableShortcutHotkeys()
        {
            ModLocator.Loader.Log("Enabling shortcut hotkeys");
            SetBinding(coupleToNearestShortcut, LeftCtrl);
            SetBinding(uncoupleShortcut, LeftAlt);
            SetBinding(kickingShortcut, LeftShift);

            ClearBindings(appendWaypointMode);
            ClearBindings(replaceWaypointMode);
            ClearBindings(insertNextWaypointMode);
        }

        public void Enable() => actions.Enable();
        public void Disable() => actions.Disable();
        public void Dispose() => UnityEngine.Object.Destroy(actions);
    }
}
