using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Core.Input;
using YARG.Menu.Data;
using YARG.Menu.Dialogs;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using static UnityEngine.Object;
using YARG.Player;
using YargNetplay.Net;

namespace YargNetplay.UI
{
    /// <summary>YARG's own dialogs (text input, message), plus the host/join actions the Online page uses.</summary>
    internal static class NetplayDialogs
    {
        private static readonly AccessTools.FieldRef<RenameDialog, TMP_InputField> RenameInput =
            AccessTools.FieldRefAccess<RenameDialog, TMP_InputField>("_inputField");
        private static readonly AccessTools.FieldRef<Dialog, Transform> DialogButtons =
            AccessTools.FieldRefAccess<Dialog, Transform>("_dialogButtonContainer");
        private static readonly AccessTools.FieldRef<Dialog, NavigationGroup> DialogNavGroup =
            AccessTools.FieldRefAccess<Dialog, NavigationGroup>("_navigationGroup");

        private static Dialog _waiting;

        private static DialogManager Dialogs => DialogManager.Instance;

        private static void ClearCurrent()
        {
            if (Dialogs != null && Dialogs.IsDialogShowing) Dialogs.ClearDialog();
        }

        public static void ShowTextInput(string title, string prefill, Action<string> onSubmit)
        {
            if (Dialogs == null) return;
            ClearCurrent();
            var dialog = Dialogs.ShowRenameDialog(title, text => onSubmit(text.Trim()));
            RenameInput(dialog).text = prefill ?? "";
        }

        public static void ShowWaiting(string peer, string song, Action onCancel)
        {
            if (Dialogs == null) return;
            ClearCurrent();

            var dialog = Dialogs.ShowMessage("Online", $"Waiting for {peer} to be ready for {song}...");
            dialog.ClearButtons();
            dialog.AddDialogButton("Menu.Common.Cancel", MenuData.Colors.CancelButton, () => onCancel());
            _waiting = dialog;
        }

        public static void CloseWaiting()
        {
            // Unity null check: the dialog is destroyed when closed.
            if (_waiting != null && Dialogs != null) Dialogs.ClearDialog();
            _waiting = null;
        }

        private static Dialog _leaveConfirm;

        /// <summary>Asks before leaving a song online, since leaving sends the other player back to the library too.</summary>
        public static void ConfirmLeaveSong(string peer, Action onLeave)
        {
            if (Dialogs == null)
            {
                onLeave();
                return;
            }
            ClearCurrent();

            _nextDialogIsLeaveConfirm = true;
            var dialog = Dialogs.ShowMessage("Leave Song?", $"Going back to the library ends the song for {peer} too.");
            _nextDialogIsLeaveConfirm = false;
            dialog.ClearButtons();
            StackButtonsVertically(dialog);
            AddListButton(dialog, "Stay", MenuData.Colors.BrightButton, CloseLeaveConfirm);
            AddListButton(dialog, "Leave Song", MenuData.Colors.CancelButton, () =>
            {
                CloseLeaveConfirm();
                onLeave();
            });
            _leaveConfirm = dialog;

            // Controller friendly: Stay starts selected, and Back means Stay (see LeaveConfirmSchemePatch).
            DialogNavGroup(dialog).SelectFirst();

            if (Environment.GetEnvironmentVariable("YARGNETPLAY_DUMP_MENUS") == "1")
                HierarchyDump.DumpObject(dialog.transform, "dialog-dump.txt");
        }

        /// <summary>Two-choice dialogs navigated with Up/Down read better as a vertical list than YARG's button row.</summary>
        private static void StackButtonsVertically(Dialog dialog)
        {
            var container = DialogButtons(dialog);
            if (container.GetComponent<HorizontalLayoutGroup>() is not { } row) return;

            DestroyImmediate(row);

            // The container sits on the dialog's bottom edge; grow it upwards from there as rows are added.
            var rect = (RectTransform) container;
            float bottom = rect.anchoredPosition.y - rect.rect.height * rect.pivot.y;
            rect.pivot = new Vector2(rect.pivot.x, 0f);
            rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, bottom);

            // A centered column about as wide as two of YARG's side-by-side dialog buttons would be together.
            int side = Mathf.Max(0, Mathf.RoundToInt((rect.rect.width - LIST_BUTTON_WIDTH) / 2));
            var column = container.gameObject.AddComponent<VerticalLayoutGroup>();
            column.spacing = 15;
            column.padding = new RectOffset(side, side, 0, 0);
            column.childAlignment = TextAnchor.LowerCenter;
            column.childControlWidth = column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;

            var fitter = container.GetComponent<ContentSizeFitter>();
            if (fitter == null) fitter = container.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private const float LIST_BUTTON_WIDTH = 600, LIST_BUTTON_HEIGHT = 50;

        /// <summary>A dialog button for a vertical list. YARG's button root has no layout size of its own, so give it one.</summary>
        private static void AddListButton(Dialog dialog, string label, Color color, UnityEngine.Events.UnityAction action)
        {
            var button = dialog.AddDialogButton("Menu.Common.Confirm", color, action);
            button.Text.text = label;
            var element = button.gameObject.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = LIST_BUTTON_HEIGHT;
        }

        private static bool _nextDialogIsLeaveConfirm;

        /// <summary>
        /// YARG dialogs only map Up/Down/Select. The dialog pushes this scheme itself when it opens and pops it when
        /// it closes, so the Leave Song dialog gets Back = Stay without touching the scheme stack from outside.
        /// </summary>
        [HarmonyPatch(typeof(Dialog), "GetNavigationScheme")]
        private static class LeaveConfirmSchemePatch
        {
            private static void Postfix(ref NavigationScheme __result)
            {
                if (!_nextDialogIsLeaveConfirm) return;
                __result = new NavigationScheme(new()
                {
                    NavigationScheme.Entry.NavigateSelect,
                    NavigationScheme.Entry.NavigateUp,
                    NavigationScheme.Entry.NavigateDown,
                    new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", CloseLeaveConfirm),
                }, null);
            }
        }

        public static void CloseLeaveConfirm()
        {
            // Unity null check: the dialog is destroyed when closed.
            if (_leaveConfirm != null && Dialogs != null) Dialogs.ClearDialog();
            _leaveConfirm = null;
        }

        public static void ShowError(string message)
        {
            if (Dialogs == null)
            {
                Notify.Error(message);
                return;
            }
            ClearCurrent();
            Dialogs.ShowMessage("Online", message);
        }

        // ------------------------------------------------------------ actions

        /// <summary>The name the peer sees: the active profile's name.</summary>
        private static string LocalPlayerName()
        {
            foreach (var player in PlayerContainer.Players)
                if (!player.SittingOut) return player.Profile.Name;
            return Environment.UserName;
        }

        public static void Host()
        {
            int port = Plugin.Port.Value;
            var session = Session.Host(port);
            session.LocalName = LocalPlayerName();
        }

        public static void Join(string address)
        {
            if (!SavedServers.TryParse(address, Plugin.Port.Value, out var host, out var port))
            {
                ShowError($"\"{address}\" isn't a valid address. Use an IP like 192.168.1.20 or 192.168.1.20:47820.");
                return;
            }

            Plugin.LastJoinAddress.Value = address;
            var session = Session.Join(host, port, address);
            session.LocalName = LocalPlayerName();
        }

        public static void PromptJoinAddress() =>
            ShowTextInput("Join: IP or IP:port", Plugin.LastJoinAddress.Value, Join);
    }
}
