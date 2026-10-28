using UnityEngine;

namespace WaypointQueue.Model
{
    public class KeyBinding
    {
        public KeyCode keyCode = KeyCode.None;
        public int modifiers = 0;

        public bool IsPressed()
        {
            if (keyCode == KeyCode.None) return false;

            bool ctrl = (modifiers & 1) != 0 ? (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) : true;
            bool alt = (modifiers & 2) != 0 ? (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) : true;
            bool shift = (modifiers & 4) != 0 ? (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) : true;

            return ctrl && alt && shift && Input.GetKeyDown(keyCode);
        }
    }
}
