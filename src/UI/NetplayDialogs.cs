using System;
using HarmonyLib;
using TMPro;
using YARG.Menu.Data;
using YARG.Menu.Dialogs;
using YARG.Menu.Persistent;
using YARG.Player;
using YargNetplay.Net;

namespace YargNetplay.UI
{
    /// <summary>YARG's own dialogs (text input, message), plus the host/join actions the Online page uses.</summary>
    internal static class NetplayDialogs
    {
        private static readonly AccessTools.FieldRef<RenameDialog, TMP_InputField> RenameInput =
            AccessTools.FieldRefAccess<RenameDialog, TMP_InputField>("_inputField");

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
