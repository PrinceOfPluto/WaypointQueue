using Game.Notices;
using HarmonyLib;
using WaypointQueue.State;

namespace WaypointQueue.Patches
{
    [HarmonyPatch(typeof(NoticeManager))]
    internal class PatchNoticeManager
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(NoticeManager.PostEphemeralLocal))]
        static bool PostEphemeralLocalPrefix(NoticeManager __instance, EntityReference entity, string contextualKey, ref string content)
        {
            var atWaypointKey = "ai-wpt";

            if (entity.Type == EntityType.Car && contextualKey == atWaypointKey)
            {
                string locoId = entity.Id;

                if (ModStateManager.Shared.LocoWaypointStates.TryGetValue(locoId, out var state) && state.Waypoints.Count > 1)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
