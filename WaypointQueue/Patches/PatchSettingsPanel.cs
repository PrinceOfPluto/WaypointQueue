using HarmonyLib;
using UI.Builder;
using UI.CompanyWindow;

namespace WaypointQueue.Patches
{
    [HarmonyPatch(typeof(SettingsPanelBuilder), "BuildFeatureMap")]
    internal class PatchSettingsPanel
    {
        static void Postfix(UIPanelBuilder builder)
        {
            builder.AddSection("Waypoint Queue");
            builder.AddField("Mod settings", builder.AddButtonCompact("Open settings", () =>
            {
                ModLocator.Loader.ModSettingsWindow.Toggle();
            }));
            builder.Spacer(8f);
        }

    }
}
