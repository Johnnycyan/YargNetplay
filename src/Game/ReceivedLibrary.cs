using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using YARG.Core.Song;
using YARG.Core.Song.Cache;
using YARG.Menu.MusicLibrary;
using YARG.Song;

namespace YargNetplay.Game
{
    /// <summary>
    /// Shows received songs in YARG's own music library (metadata, previews, sorting, setlists) while
    /// connected. They are added to the in-memory song cache right before YARG sorts it, so they survive
    /// rescans, are never written to songcache.bin, and disappear again on disconnect.
    /// </summary>
    internal static class ReceivedLibrary
    {
        private static readonly List<SongEntry> Injected = new();
        private static bool _active, _resortPending;

        public static void SetActive(bool active)
        {
            if (_active == active) return;
            _active = active;
            Refresh();
        }

        /// <summary>
        /// Re-sorts the library so newly received songs (or a disconnect) show up. Re-sorting runs YARG's whole sort on
        /// the main thread, which takes seconds with a big library and froze both games on joining and leaving. So it
        /// is skipped when it would change nothing, and otherwise only done right away while the music library is open;
        /// else it waits until the library opens next (see <see cref="LibraryEnablePatch"/>).
        /// </summary>
        public static void Refresh()
        {
            if (!NeedsResort()) return;

            _resortPending = true;
            MusicLibraryMenu.SetReload(MusicLibraryReloadState.Full);

            var library = Object.FindAnyObjectByType<MusicLibraryMenu>(FindObjectsInactive.Exclude);
            if (library == null) return;
            ResortIfPending();
            library.RefreshAndReselect(preserveSelectedIndex: true);
        }

        /// <summary>
        /// Active: some received song isn't in the library yet. Inactive: received songs are still in it. Songs the
        /// player has in their own library are never injected, so they need nothing either way.
        /// </summary>
        private static bool NeedsResort()
        {
            if (!_active) return Injected.Count > 0;
            var byHash = SongContainer.SongsByHash;
            if (byHash == null || byHash.Count == 0) return false; // not scanned yet: the scan's own sort injects them
            foreach (var song in ReceivedSongs.All)
                if (!byHash.ContainsKey(song.Hash)) return true;
            return false;
        }

        private static void ResortIfPending()
        {
            if (!_resortPending) return;
            _resortPending = false;
            SongContainer.RequestContainerRefresh();
        }

        /// <summary>A re-sort put off while the library was closed happens before the library reads the song lists.</summary>
        [HarmonyPatch(typeof(MusicLibraryMenu), "OnEnable")]
        private static class LibraryEnablePatch
        {
            private static void Prefix() => ResortIfPending();
        }

        [HarmonyPatch(typeof(SongSorting), "SortEntries")]
        private static class SortEntriesPatch
        {
            private static void Prefix(SongCache cache)
            {
                foreach (var song in Injected)
                {
                    if (cache.Entries.TryGetValue(song.Hash, out var list))
                    {
                        list.Remove(song);
                        if (list.Count == 0) cache.Entries.Remove(song.Hash);
                    }
                }
                Injected.Clear();

                if (!_active) return;

                foreach (var song in ReceivedSongs.All)
                {
                    // A copy the player keeps in their own library wins.
                    if (cache.Entries.ContainsKey(song.Hash)) continue;
                    cache.Entries[song.Hash] = new List<SongEntry> { song };
                    Injected.Add(song);
                }
            }
        }
    }
}
