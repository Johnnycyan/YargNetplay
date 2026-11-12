using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using YARG.Localization;
using YARG.Menu;
using YARG.Menu.Main;
using YARG.Menu.Navigation;
using Object = UnityEngine.Object;

namespace YargNetplay.UI
{
    /// <summary>
    /// Adds an "Online" entry to the main menu by cloning the real Quickplay entry, so it gets the
    /// same prefab, font, hover/selection visuals and controller navigation.
    /// </summary>
    [HarmonyPatch(typeof(MainMenu), "Start")]
    internal static class MainMenuButton
    {
        private const string LABEL = "Online";

        private static readonly AccessTools.FieldRef<NavigatableButton, Button.ButtonClickedEvent> OnClick =
            AccessTools.FieldRefAccess<NavigatableButton, Button.ButtonClickedEvent>("_onClick");

        private static readonly AccessTools.FieldRef<NavigatableBehaviour, GameObject> SelectedVisual =
            AccessTools.FieldRefAccess<NavigatableBehaviour, GameObject>("_selectedVisual");

        private static readonly AccessTools.FieldRef<NavigationGroup, List<NavigatableBehaviour>> Navigatables =
            AccessTools.FieldRefAccess<NavigationGroup, List<NavigatableBehaviour>>("_navigatables");

        private static void Postfix(MainMenu __instance)
        {
            try
            {
                OnlinePage.Create(MenuManager.Instance);
                Inject(__instance);
            }
            catch (Exception e) { Plugin.Log.LogError($"Could not add the Online page: {e}"); }

            if (Environment.GetEnvironmentVariable("YARGNETPLAY_DUMP_MENUS") == "1") HierarchyDump.DumpMenus();
        }

        private static void Inject(MainMenu menu)
        {
            var quickplay = menu.GetComponentsInChildren<NavigatableButton>(true).FirstOrDefault(IsQuickplay);
            if (quickplay == null)
            {
                Plugin.Log.LogWarning("Quickplay entry not found in the main menu; Online button not added.");
                return;
            }

            var clone = Object.Instantiate(quickplay.gameObject, quickplay.transform.parent);
            clone.name = LABEL;
            clone.transform.SetSiblingIndex(quickplay.transform.GetSiblingIndex() + 1);

            // Persistent (serialized) listeners can't be removed, so replace the events outright.
            var nav = clone.GetComponent<NavigatableButton>();
            OnClick(nav) = new Button.ButtonClickedEvent();
            nav.SetOnClickEvent(() => MenuManager.Instance.PushMenu(OnlinePage.MenuId));
            foreach (var unityButton in clone.GetComponentsInChildren<Button>(true))
                unityButton.onClick = new Button.ButtonClickedEvent();

            // The label is localized by key; drop that so our text sticks.
            foreach (var localize in clone.GetComponentsInChildren<LocalizeText>(true))
                Object.DestroyImmediate(localize);
            // Copied while Quickplay may be highlighted: take the unselected text color from the entry below.
            var below = quickplay.transform.parent.GetChild(clone.transform.GetSiblingIndex() + 1)
                .GetComponentInChildren<TMP_Text>(true);
            foreach (var text in clone.GetComponentsInChildren<TMP_Text>(true))
            {
                text.text = LABEL;
                if (below != null) text.color = below.color;
            }

            var badges = clone.transform.Find("Badge Container");
            if (badges != null) badges.gameObject.SetActive(false);

            // Copied while Quickplay may be selected; start unselected.
            var visual = SelectedVisual(nav);
            if (visual != null) visual.SetActive(false);

            // Register for keyboard/controller navigation right after Quickplay.
            var group = quickplay.NavigationGroup;
            if (group != null)
            {
                group.AddNavigatable(nav);
                var list = Navigatables(group);
                list.Remove(nav);
                list.Insert(list.IndexOf(quickplay) + 1, nav);
            }
        }

        private static bool IsQuickplay(NavigatableButton button)
        {
            UnityEventBase ev = OnClick(button);
            for (int i = 0; i < ev.GetPersistentEventCount(); i++)
            {
                if (ev.GetPersistentMethodName(i) == nameof(MainMenu.QuickPlay)) return true;
            }
            return false;
        }
    }
}
