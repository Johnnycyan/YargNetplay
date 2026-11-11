using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using YARG.Core;
using YARG.Core.IO;
using YARG.Core.Song;

namespace YargNetplay.Game
{
    /// <summary>
    /// Songs received from peers. They live in their own folder (not a YARG song directory), so they never
    /// enter the player's library unless the player moves them there. Entries are built in memory with the
    /// same code YARG's scanner uses, so no library rescan is needed.
    /// </summary>
    internal static class ReceivedSongs
    {
        private static readonly string[] ChartListings = { "notes.mid", "notes.midi", "notes.chart" };
        private static readonly YARG.Core.Song.ChartFormat[] ChartFormats =
        {
            YARG.Core.Song.ChartFormat.Mid, YARG.Core.Song.ChartFormat.Midi, YARG.Core.Song.ChartFormat.Chart,
        };

        // SngEntry is internal to YARG.Core; its scanner entry point is public static.
        private static readonly MethodInfo ProcessSngEntry =
            AccessTools.Method("YARG.Core.Song.SngEntry:ProcessNewEntry");

        private static readonly ConcurrentDictionary<HashWrapper, SongEntry> Entries = new();

        public static string Folder { get; private set; }
        public static string PartialFolder => Path.Combine(Folder, ".partial");
        public static int Count => Entries.Count;
        public static long TotalBytes { get; private set; }
        public static IEnumerable<SongEntry> All => Entries.Values;

        /// <summary>Main thread (needs Application.persistentDataPath). Indexes existing files in the background.</summary>
        public static void Initialize()
        {
            Folder = Path.Combine(Application.persistentDataPath, "NetplaySongs");
            Directory.CreateDirectory(Folder);
            Directory.CreateDirectory(PartialFolder);

            Task.Run(() =>
            {
                foreach (var file in Directory.GetFiles(Folder))
                {
                    if (!IsSongFile(file)) continue;
                    if (!TryRegister(file, null, out _, out var error))
                        Plugin.Log.LogWarning($"Skipping received song {Path.GetFileName(file)}: {error}");
                }
                RefreshSize();
                Plugin.Log.LogInfo($"Indexed {Entries.Count} received songs in {Folder}");
            });
        }

        public static bool IsSongFile(string path)
        {
            var ext = Path.GetExtension(path);
            return ext.Equals(".sng", StringComparison.OrdinalIgnoreCase) || ext.Equals(".yargsong", StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryGet(HashWrapper hash, out SongEntry song) => Entries.TryGetValue(hash, out song);

        public static string FinalPathFor(string name, string artist, byte[] hash, string extension)
        {
            var invalid = Path.GetInvalidFileNameChars();
            string clean(string s) => new string((s ?? "").Where(c => !invalid.Contains(c)).ToArray()).Trim();

            string title = $"{clean(artist)} - {clean(name)}";
            if (title.Length > 120) title = title.Substring(0, 120);
            string shortHash = BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
            return Path.Combine(Folder, $"{title} [{shortHash}]{extension}");
        }

        /// <summary>
        /// Builds a SongEntry from a .sng file and registers it.
        /// When <paramref name="expectedHash"/> is given, the chart hash must match it.
        /// </summary>
        public static bool TryRegister(string path, byte[] expectedHash, out SongEntry song, out string error)
        {
            song = null;
            try
            {
                var sng = SngFile.TryLoadFromFile(path, true);
                if (!sng.IsLoaded)
                {
                    error = "not a valid .sng file";
                    return false;
                }

                using (sng)
                {
                    // Same rule as CacheHandler.ScanSngFile: .mid charts need song.ini metadata.
                    int start = sng.Modifiers.IsEmpty() ? 2 : 0;
                    for (int i = start; i < ChartListings.Length; i++)
                    {
                        if (!sng.TryGetListing(ChartListings[i], out var listing)) continue;

                        var args = new object[] { sng, listing, new FileInfo(path), ChartFormats[i], "Netplay" };
                        var result = ProcessSngEntry.Invoke(null, args);
                        var resultType = result.GetType();

                        if (!(bool) resultType.GetProperty("HasValue").GetValue(result))
                        {
                            error = "chart rejected: " + resultType.GetProperty("Error").GetValue(result);
                            return false;
                        }

                        song = (SongEntry) resultType.GetProperty("Value").GetValue(result);
                        break;
                    }
                }

                if (song == null)
                {
                    error = "no notes.mid / notes.chart inside";
                    return false;
                }

                if (expectedHash != null && !song.Hash.HashBytes.AsSpan().SequenceEqual(expectedHash))
                {
                    error = "chart hash doesn't match the requested song";
                    song = null;
                    return false;
                }

                Entries[song.Hash] = song;
                error = null;
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        public static void RefreshSize()
        {
            try
            {
                TotalBytes = Directory.GetFiles(Folder).Where(IsSongFile).Sum(f => new FileInfo(f).Length);
            }
            catch { /* folder removed by the user */ }
        }

        public static void OpenFolder() => Application.OpenURL(new Uri(Folder).AbsoluteUri);
    }
}
