using System;
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
            UnityEngine.Object.FindAnyObjectByType<MusicLibraryMenu>(FindObjectsInactive.Include);

        private static Session ConnectedSession => Session.Current is { IsConnected: true } s ? s : null;

        // ------------------------------------------------------------ session -> native

        public static void Add(SongEntry song)
        {
            var library = FindLibrary();
            if (library == null || library.ShowPlaylist.ContainsSong(song)) return;
            // A song that finished downloading goes where it is in the shared setlist, not at the end.
            Apply(library, () =>
            {
                library.ShowPlaylist.AddSong(song);
                SortToSharedOrder(library);
            });
        }

        /// <summary>The shared setlist was reordered: put YARG's setlist (the songs this player has) in the same order.</summary>
        public static void ApplyOrder()
        {
            var library = FindLibrary();
            if (library == null || library.ShowPlaylist.Count < 2) return;
            Apply(library, () => SortToSharedOrder(library));
        }

        private static void SortToSharedOrder(MusicLibraryMenu library)
        {
            var queue = ConnectedSession?.Queue;
            if (queue == null) return;

            int Position(HashWrapper hash)
            {
                int i = queue.FindIndex(q => hash.HashBytes.AsSpan().SequenceEqual(q.Hash));
                return i < 0 ? int.MaxValue : i;
            }

            var hashes = library.ShowPlaylist.SongHashes;
            var sorted = hashes.Select((hash, index) => (hash, index))
                .OrderBy(e => Position(e.hash)).ThenBy(e => e.index) // stable: unknown songs keep their order at the end
                .Select(e => e.hash).ToList();
            hashes.Clear();
            hashes.AddRange(sorted);
        }

        // ------------------------------------------------------------ setlist overlay edits

        /// <summary>Removes a song from the shared setlist for both players, whether or not this player has it.</summary>
        public static void RemoveShared(byte[] hash)
        {
            ConnectedSession?.RemoveFromQueue(hash);
            Remove(hash);
        }

        /// <summary>Moves a song up (-1) or down (+1) in the shared setlist for both players.</summary>
        public static bool MoveShared(int index, int delta)
        {
            if (ConnectedSession?.MoveInQueue(index, delta) != true) return false;
            ApplyOrder();
            return true;
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

            Refreshed(library);
        }

        /// <summary>
        /// Redraws the list and asks YARG to rebuild the library's controls, which depend on whether the setlist is
        /// empty (Yellow: Play a Show vs Start Set). YARG applies that once no popup or dialog blocks it.
        /// </summary>
        private static void Refreshed(MusicLibraryMenu library)
        {
            NeedsSchemeRefresh(library) = true;
            if (library.isActiveAndEnabled) library.RefreshAndReselect(preserveSelectedIndex: true);
        }

        private static readonly AccessTools.FieldRef<MusicLibraryMenu, bool> NeedsSchemeRefresh =
            AccessTools.FieldRefAccess<MusicLibraryMenu, bool>("_needsNavigationSchemeRefresh");

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

            if (changed) Refreshed(library);
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

        /// <summary>YARG's own setlist view moves songs with Left/Right; share the new order.</summary>
        [HarmonyPatch]
        private static class MoveSongPatch
        {
            private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods() => new[]
            {
                AccessTools.Method(typeof(Playlist), nameof(Playlist.MoveSongUp)),
                AccessTools.Method(typeof(Playlist), nameof(Playlist.MoveSongDown)),
            };

            private static void Postfix(Playlist __instance)
            {
                if (__instance.Ephemeral && !_applying)
                    ConnectedSession?.MatchQueueOrder(__instance.SongHashes.Select(h => h.HashBytes).ToList());
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
