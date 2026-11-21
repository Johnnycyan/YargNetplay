using System;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;
#if YARG_PROFILES_MENU
using YARG.Menu.Tooltips;
using ProfilesMenuType = YARG.Menu.ProfileList.ProfilesMenu;
using ProfilePaneType = YARG.Menu.ProfileList.ProfileCenterPane;
#else
using ProfilesMenuType = YARG.Menu.ProfileList.ProfileListMenu;
using ProfilePaneType = YARG.Menu.ProfileList.ProfileSidebar;
#endif

namespace YargNetplay.UI
{
    /// <summary>
    /// The parts of YARG's Profiles page that <see cref="OnlinePage"/> clones, over both layouts of that page. Up to
    /// v0.15.0 and older nightlies it is a profile list beside a sidebar (ProfileListMenu, ProfileSidebar) with buttons
    /// along the sidebar's bottom. Newer nightlies (b4378 has it; ProfilesMenu, ProfileCenterPane) add header tabs, a
    /// binding set filter and pane, and a tooltip pane on the right, and the buttons only live in the binding set pane.
    /// The csproj defines YARG_PROFILES_MENU for the YARG builds that have the latter.
    /// </summary>
    internal static class ProfilesTemplate
    {
        public static readonly Type MenuType = typeof(ProfilesMenuType);

#if YARG_PROFILES_MENU
        public const string Header = "HeaderWithTabs";
        public const string List = "LeftPane/List";
        public const string Sidebar = "Center Pane/Profile Center Pane/Contents";
#else
        public const string Header = "GenericHeader";
        public const string List = "Background/Profiles";
        public const string Sidebar = "Sidebar/Contents";
#endif
        public const string ListContent = List + "/Viewport/Content";

        /// <summary>
        /// Takes the page's row and list header prefabs, then removes the page's own logic and the parts the Online
        /// page has no use for, keeping the visuals laid out like the old two-pane page.
        /// </summary>
        public static (GameObject Row, GameObject Header) Strip(GameObject page)
        {
            var menu = page.GetComponent<ProfilesMenuType>();
            var row = (GameObject) AccessTools.Field(MenuType, "_profileViewPrefab").GetValue(menu);
            var header = (GameObject) AccessTools.Field(MenuType, "_profileListHeaderPrefab").GetValue(menu);

            Object.DestroyImmediate(menu);
            foreach (var pane in page.GetComponentsInChildren<ProfilePaneType>(true))
                Object.DestroyImmediate(pane);
            var root = page.transform;
            Remove(root, "HighwayConfigurationMenu");

#if YARG_PROFILES_MENU
            foreach (var path in new[] { Header + "/Tabs", Header + "/Add Profile Buttons", Header + "/Import Binding Set", "LeftPane/Filter Pane", Sidebar + "/Header/Overview" })
                Hide(root, path);

            // Bring the buttons back to the bottom of the profile pane, across its width, with the options scrolling above them.
            var contents = root.Find(Sidebar);
            var buttons = (RectTransform) root.Find("Center Pane/Binding Center Pane/Background/Contents/Buttons");
            buttons.SetParent(contents, false);
            buttons.gameObject.SetActive(true);
            buttons.anchorMin = Vector2.zero;
            buttons.anchorMax = Vector2.right;
            buttons.pivot = new Vector2(0.5f, 0f);
            buttons.anchoredPosition = Vector2.zero;
            buttons.sizeDelta = new Vector2(0f, buttons.sizeDelta.y);
            var scroll = (RectTransform) contents.Find("ScrollView");
            scroll.offsetMin = new Vector2(scroll.offsetMin.x, buttons.sizeDelta.y);
            Remove(root, "Center Pane/Binding Center Pane");
            Remove(root, "DummyControllerRecordDialog");

            // The tooltips would describe profile settings, not ours. Without them the right pane is empty, so give
            // its width to the list and the sidebar.
            var right = (RectTransform) root.Find("RightPane");
            float width = right.sizeDelta.x;
            Object.DestroyImmediate(right.gameObject);
            var center = (RectTransform) root.Find("Center Pane");
            center.anchoredPosition += new Vector2(width, 0f);
            var left = (RectTransform) root.Find("LeftPane");
            left.sizeDelta += new Vector2(width, 0f);
            StripTooltips(page);
            foreach (var coordinator in page.GetComponentsInChildren<TooltipCoordinator>(true))
                Object.DestroyImmediate(coordinator);
#else
            Hide(root, Header + "/Add Profile");
            Hide(root, Header + "/Add Bot");
#endif
            return (row, header);
        }

        /// <summary>Removes tooltip triggers, which would look for the tooltip pane the Online page doesn't have.</summary>
        public static void StripTooltips(GameObject root)
        {
#if YARG_PROFILES_MENU
            foreach (var trigger in root.GetComponentsInChildren<TooltipTrigger>(true))
                Object.DestroyImmediate(trigger);
#endif
        }

        private static void Hide(Transform root, string path)
        {
            var child = root.Find(path);
            if (child != null) child.gameObject.SetActive(false);
        }

        private static void Remove(Transform root, string path)
        {
            var child = root.Find(path);
            if (child != null) Object.DestroyImmediate(child.gameObject);
        }
    }
}
