using HarmonyLib;
using YARG.Menu.Persistent;
using YargNetplay.Game;

namespace YargNetplay.UI
{
    /// <summary>
    /// Adds the mod's version to YARG's version label at the top left, so both are visible when comparing installs.
    /// Nightlies show the label; stable builds hide it, so there it's shown with just the YARG and mod versions.
    /// </summary>
    [HarmonyPatch(typeof(DevWatermark), "Start")]
    internal static class VersionWatermark
    {
        private static void Postfix(DevWatermark __instance)
        {
            var text = Accessors.WatermarkText(__instance);
            if (text == null) return;

            if (!__instance.gameObject.activeSelf)
            {
                __instance.gameObject.SetActive(true);
                text.text = $"<b>YARG {GameInfo.YargVersion}</b>";
            }
            text.text += $"  ·  YARG Netplay {Plugin.Version}";
        }
    }
}
