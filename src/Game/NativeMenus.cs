using System.Collections.Generic;
using HarmonyLib;
using UnityEngine.SceneManagement;
using YARG;
using YARG.Core.Song;
using YARG.Menu;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Persistent;
using YargNetplay.Net;
using YargNetplay.UI;

namespace YargNetplay.Game
{
    /// <summary>Drives YARG's own menus: the music library and difficulty select.</summary>
    internal static class NativeMenus
    {
        private static readonly AccessTools.FieldRef<MenuManager, Stack<MenuManager.Menu>> OpenMenus =
            AccessTools.FieldRefAccess<MenuManager, Stack<MenuManager.Menu>>("_openMenus");

        /// <summary>True while we push difficulty select because the peer did (so it isn't echoed back).</summary>
        public static bool Following { get; private set; }

        private static bool InMenuScene =>
            MenuManager.Instance != null && SceneManager.GetActiveScene().buildIndex == (int) SceneIndex.Menu;

        private static MenuManager.Menu TopMenu =>
            OpenMenus(MenuManager.Instance).TryPeek(out var top) ? top : MenuManager.Menu.None;

        /// <summary>Same as the main menu's Quickplay button.</summary>
        public static void OpenLibrary()
        {
            if (!InMenuScene || TopMenu == MenuManager.Menu.MusicLibrary) return;

            var menu = MenuManager.Instance.PushMenu(MenuManager.Menu.MusicLibrary, false);
            MusicLibraryMenu.LibraryMode = MusicLibraryMode.QuickPlay;
            menu.gameObject.SetActive(true);
        }

        /// <summary>The peer opened difficulty select for these songs: open it here too, with the same songs.</summary>
        public static void FollowIntoDifficultySelect(List<SongEntry> songs, bool show)
        {
            var session = Session.Current;
            if (!InMenuScene)
            {
                Notify.Info($"{session?.PeerName} is picking a difficulty. Go back to the menu to join in.");
                return;
            }

            if (DialogManager.Instance != null && DialogManager.Instance.IsDialogShowing)
                DialogManager.Instance.ClearDialog();

            GlobalVariables.State.CurrentReplay = null;
            GlobalVariables.State.PlayingWithReplay = false;
            GlobalVariables.State.IsPractice = false;
            GlobalVariables.State.PlayingAShow = show;
            GlobalVariables.State.ShowSongs = songs;
            GlobalVariables.State.ShowIndex = 0;
            GlobalVariables.State.CurrentSong = songs[0];

            Following = true;
            try
            {
                // Re-open so it rebuilds for the new songs.
                if (TopMenu == MenuManager.Menu.DifficultySelect) MenuManager.Instance.PopMenu();
                MenuManager.Instance.PushMenu(MenuManager.Menu.DifficultySelect);
            }
            finally { Following = false; }

            Notify.Info(show
                ? $"{session?.PeerName} started the setlist. Pick your difficulty."
                : $"{session?.PeerName} picked {songs[0].Name.Original}. Pick your difficulty.");
        }

        /// <summary>The peer backed out of difficulty select: go back to the library too. False when not there.</summary>
        public static bool FollowOutOfDifficultySelect()
        {
            if (!InMenuScene || TopMenu != MenuManager.Menu.DifficultySelect) return false;

            Following = true;
            try { MenuManager.Instance.PopMenu(); }
            finally { Following = false; }
            return true;
        }

        /// <summary>Backing out of difficulty select locally takes the peer back to the library too.</summary>
        [HarmonyPatch(typeof(MenuManager), nameof(MenuManager.PopMenu))]
        private static class PopMenuPatch
        {
            private static void Prefix(out bool __state) =>
                __state = !Following && InMenuScene && TopMenu == MenuManager.Menu.DifficultySelect;

            private static void Postfix(bool __state)
            {
                if (__state && TopMenu != MenuManager.Menu.DifficultySelect) Session.Current?.SendLeaveSetlist();
            }
        }

        /// <summary>Opening difficulty select locally (single song or setlist) brings the peer along.</summary>
        [HarmonyPatch(typeof(MenuManager), nameof(MenuManager.PushMenu))]
        private static class PushMenuPatch
        {
            private static void Postfix(MenuManager.Menu menu)
            {
                if (menu != MenuManager.Menu.DifficultySelect || Following) return;

                var session = Session.Current;
                var state = GlobalVariables.State;
                if (session is not { IsConnected: true } || state.IsPractice || state.CurrentSong == null) return;

                var songs = state.PlayingAShow && state.ShowSongs is { Count: > 0 }
                    ? state.ShowSongs
                    : new List<SongEntry> { state.CurrentSong };
                session.SendOpenSetlist(songs, state.PlayingAShow);
            }
        }
    }
}
