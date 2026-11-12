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
        private static bool _active;

        public static void SetActive(bool active)
        {
            if (_active == active) return;
            _active = active;
            Refresh();
        }

        /// <summary>Re-sorts the library so newly received songs (or a disconnect) show up.</summary>
        public static void Refresh()
        {
            if (!_active && Injected.Count == 0) return;

            SongContainer.RequestContainerRefresh();
            MusicLibraryMenu.SetReload(MusicLibraryReloadState.Full);

            var library = Object.FindAnyObjectByType<MusicLibraryMenu>(FindObjectsInactive.Exclude);
            if (library != null) library.RefreshAndReselect(preserveSelectedIndex: true);
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
