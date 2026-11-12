using System.Linq;
using HarmonyLib;
using UnityEngine;
using YARG.Core.Song;
using YARG.Menu.MusicLibrary;
using YARG.Playlists;
using YargNetplay.Net;

namespace YargNetplay.Game
{
    /// <summary>
    /// Keeps YARG's own "Current Setlist" (MusicLibraryMenu.ShowPlaylist, the only ephemeral playlist)
    /// and the session's shared setlist in sync, in both directions.
    /// </summary>
    internal static class NativeSetlist
    {
        private static bool _applying;

        private static MusicLibraryMenu FindLibrary() =>
            Object.FindAnyObjectByType<MusicLibraryMenu>(FindObjectsInactive.Include);

        private static Session ConnectedSession => Session.Current is { IsConnected: true } s ? s : null;

        // ------------------------------------------------------------ session -> native

        public static void Add(SongEntry song)
        {
            var library = FindLibrary();
            if (library == null || library.ShowPlaylist.ContainsSong(song)) return;
            Apply(library, () => library.ShowPlaylist.AddSong(song));
        }

        public static void Remove(byte[] hash)
        {
            var library = FindLibrary();
            if (library == null || !SongLibrary.TryGet(hash, out var song) || !library.ShowPlaylist.ContainsSong(song)) return;
            Apply(library, () => library.ShowPlaylist.RemoveSong(song));
        }

        public static void Clear()
        {
            var library = FindLibrary();
            if (library == null || library.ShowPlaylist.Count == 0) return;
            Apply(library, () => library.ShowPlaylist.Clear());
        }

        private static void Apply(MusicLibraryMenu library, System.Action change)
        {
            _applying = true;
            try { change(); }
            finally { _applying = false; }

            if (library.isActiveAndEnabled) library.RefreshAndReselect(preserveSelectedIndex: true);
        }

        /// <summary>
        /// The library menu is recreated with an empty setlist each time the menu scene loads: fill it from
        /// the shared setlist, and share anything that was added before connecting.
        /// </summary>
        private static void Resync(MusicLibraryMenu library)
        {
            var session = ConnectedSession;
            if (session == null) return;

            bool changed = false;
            _applying = true;
            try
            {
                foreach (var item in session.Queue.Where(q => q.LocalHas))
                {
                    if (SongLibrary.TryGet(item.Hash, out var song) && !library.ShowPlaylist.ContainsSong(song))
                    {
                        library.ShowPlaylist.AddSong(song);
                        changed = true;
                    }
                }
            }
            finally { _applying = false; }

            foreach (var song in library.ShowPlaylist.ToList())
                session.AddToQueue(song);

            if (changed && library.isActiveAndEnabled) library.RefreshAndReselect(preserveSelectedIndex: true);
        }

        public static void ResyncIfOpen()
        {
            var library = FindLibrary();
            if (library != null) Resync(library);
        }

        // ------------------------------------------------------------ native -> session

        [HarmonyPatch(typeof(Playlist), nameof(Playlist.AddSong))]
        private static class AddSongPatch
        {
            private static void Postfix(Playlist __instance, SongEntry song)
            {
                if (__instance.Ephemeral && !_applying) ConnectedSession?.AddToQueue(song);
            }
        }

        [HarmonyPatch(typeof(Playlist), nameof(Playlist.RemoveSong))]
        private static class RemoveSongPatch
        {
            private static void Postfix(Playlist __instance, SongEntry song)
            {
                if (__instance.Ephemeral && !_applying) ConnectedSession?.RemoveFromQueue(song.Hash.HashBytes);
            }
        }

        [HarmonyPatch(typeof(Playlist), nameof(Playlist.Clear))]
        private static class ClearPatch
        {
            private static void Postfix(Playlist __instance)
            {
                if (__instance.Ephemeral && !_applying) ConnectedSession?.ClearQueue();
            }
        }

        [HarmonyPatch(typeof(MusicLibraryMenu), "OnEnable")]
        private static class LibraryEnablePatch
        {
            private static void Postfix(MusicLibraryMenu __instance) => Resync(__instance);
        }
    }
}
