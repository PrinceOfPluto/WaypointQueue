using UnityEngine;
using UnityEngine.InputSystem;

namespace WaypointQueue.Settings
{
    public static class InputActionAssetFactory
    {
        public static InputActionAsset CreateAsset()
        {
            var asset = ScriptableObject.CreateInstance<InputActionAsset>();
            var map = new InputActionMap("WaypointQueue");

            map.AddAction("AppendWaypointMode", InputActionType.Button, "");
            map.AddAction("ReplaceWaypointMode", InputActionType.Button, "");
            map.AddAction("InsertNextWaypointMode", InputActionType.Button, "");
            map.AddAction("CoupleToNearestShortcut", InputActionType.Button, "<Keyboard>/leftCtrl");
            map.AddAction("UncoupleShortcut", InputActionType.Button, "<Keyboard>/leftAlt");
            map.AddAction("KickingShortcut", InputActionType.Button, "<Keyboard>/leftShift");

            var toggleWaypointPanel = map.AddAction("ToggleWaypointPanel", InputActionType.Button);
            toggleWaypointPanel.AddCompositeBinding("ButtonWithOneModifier")
                .With("modifier", "<Keyboard>/shift")
                .With("button", "<Keyboard>/g");

            var toggleRoutesPanel = map.AddAction("ToggleRoutesPanel", InputActionType.Button);
            toggleRoutesPanel.AddCompositeBinding("ButtonWithOneModifier")
                .With("modifier", "<Keyboard>/ctrl")
                .With("button", "<Keyboard>/z");

            asset.AddActionMap(map);
            return asset;
        }
    }
}
