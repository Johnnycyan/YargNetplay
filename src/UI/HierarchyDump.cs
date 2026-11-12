using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using UnityEngine;
using YARG.Menu;

namespace YargNetplay.UI
{
    /// <summary>Dev aid: writes the menu hierarchy (names, components, rects) to BepInEx/menu-dump.txt.</summary>
    internal static class HierarchyDump
    {
        public static void DumpMenus()
        {
            var sb = new StringBuilder();
            foreach (var menu in Object.FindObjectsByType<MenuObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                sb.AppendLine($"===== MenuObject {menu.Menu} ({menu.name}) parent={menu.transform.parent?.name}");
                Dump(menu.transform, 0, sb, 6);
            }
            var profileList = Object.FindAnyObjectByType<YARG.Menu.ProfileList.ProfileListMenu>(FindObjectsInactive.Include);
            if (profileList != null)
            {
                foreach (var name in new[] { "_profileViewPrefab", "_profileListHeaderPrefab" })
                {
                    var prefab = (GameObject) HarmonyLib.AccessTools.Field(profileList.GetType(), name).GetValue(profileList);
                    sb.AppendLine($"===== prefab {name}");
                    if (prefab != null) Dump(prefab.transform, 0, sb, 12);
                }
                var row = profileList.transform.Find("Sidebar/Contents/ScrollView/Viewport/Content/Input Calibration");
                sb.AppendLine("===== Input Calibration row");
                if (row != null) Dump(row, 0, sb, 12);
                var lefty = profileList.transform.Find("Sidebar/Contents/ScrollView/Viewport/Content/Lefty Flip");
                sb.AppendLine("===== Lefty Flip row");
                if (lefty != null) Dump(lefty, 0, sb, 12);
            }

            var settings = YARG.Menu.Settings.SettingsMenu.Instance;
            if (settings != null)
            {
                sb.AppendLine("===== SettingsMenu");
                Dump(settings.transform, 0, sb, 9);
            }
            var path = Path.Combine(Paths.BepInExRootPath, "menu-dump.txt");
            File.WriteAllText(path, sb.ToString());
            Plugin.Log.LogInfo("Menu hierarchy written to " + path);
        }

        private static void Dump(Transform t, int depth, StringBuilder sb, int maxDepth)
        {
            var rt = t as RectTransform;
            string rect = rt != null
                ? $" rect={rt.rect.width:0}x{rt.rect.height:0} anchors=({rt.anchorMin.x:0.##},{rt.anchorMin.y:0.##})-({rt.anchorMax.x:0.##},{rt.anchorMax.y:0.##}) pos={rt.anchoredPosition}"
                : "";
            var comps = string.Join(",", t.GetComponents<Component>()
                .Where(c => c != null && c is not Transform)
                .Select(c => c.GetType().Name));
            var text = t.GetComponent<TMPro.TMP_Text>();
            string label = text != null ? $" text=\"{text.text?.Replace("\n", " ")}\"" : "";
            sb.AppendLine($"{new string(' ', depth * 2)}{t.name}{(t.gameObject.activeSelf ? "" : " [inactive]")} [{comps}]{rect}{label}");
            if (depth >= maxDepth) return;
            foreach (Transform child in t) Dump(child, depth + 1, sb, maxDepth);
        }
    }
}
