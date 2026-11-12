using System;
using YARG.Menu.Persistent;

namespace YargNetplay.UI
{
    /// <summary>YARG's own toasts, safe to call from any scene (and before the toast manager exists).</summary>
    internal static class Notify
    {
        public static void Info(string text, Action onClick = null) => Show(ToastManager.ToastInformation, text, onClick);
        public static void Success(string text, Action onClick = null) => Show(ToastManager.ToastSuccess, text, onClick);
        public static void Warning(string text, Action onClick = null) => Show(ToastManager.ToastWarning, text, onClick);
        public static void Error(string text, Action onClick = null) => Show(ToastManager.ToastError, text, onClick);

        private static void Show(Action<string, Action> toast, string text, Action onClick)
        {
            Plugin.Log.LogInfo("[toast] " + text);
            try { toast(text, onClick); }
            catch (Exception e) { Plugin.Log.LogWarning("Toast failed: " + e.Message); }
        }
    }
}
