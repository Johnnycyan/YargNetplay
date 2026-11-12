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
using YARG.Helpers;

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

        private static volatile bool _busy;

        public static string Folder { get; private set; }
        public static string DefaultFolder { get; private set; }
        public static string PartialFolder => Path.Combine(Folder, ".partial");
        public static int Count => Entries.Count;
        public static long TotalBytes { get; private set; }
        public static IEnumerable<SongEntry> All => Entries.Values;

        /// <summary>True while songs are being indexed or moved to a new folder.</summary>
        public static bool Busy => _busy;

        /// <summary>Main thread (needs Application.persistentDataPath). Indexes existing files in the background.</summary>
        public static void Initialize()
        {
            DefaultFolder = Path.GetFullPath(Path.Combine(Application.persistentDataPath, "NetplaySongs"));
            Folder = string.IsNullOrWhiteSpace(Plugin.ReceivedSongsFolder.Value) ? DefaultFolder : Plugin.ReceivedSongsFolder.Value;
            try
            {
                CreateFolders(Folder);
            }
            catch (Exception e)
            {
                // E.g. a removed drive. Keep the setting so the folder is used again once it's back.
                Plugin.Log.LogWarning($"Received songs folder {Folder} is unavailable ({e.Message}). Using {DefaultFolder} for now.");
                Folder = DefaultFolder;
                CreateFolders(Folder);
            }

            _busy = true;
            Task.Run(() =>
            {
                try
                {
                    IndexFolder(Folder);
                    RefreshSize();
                    Plugin.Log.LogInfo($"Indexed {Entries.Count} received songs in {Folder}");
                }
                finally { _busy = false; }
            });
        }

        private static void CreateFolders(string folder)
        {
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(Path.Combine(folder, ".partial"));
        }

        private static void IndexFolder(string folder)
        {
            foreach (var file in Directory.GetFiles(folder))
            {
                if (!IsSongFile(file)) continue;
                if (!TryRegister(file, null, out _, out var error))
                    Plugin.Log.LogWarning($"Skipping received song {Path.GetFileName(file)}: {error}");
            }
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Main thread. Switches to <paramref name="requested"/> (empty means the default folder) and moves the
        /// received songs there in the background. <paramref name="onDone"/> gets a summary and whether every song
        /// moved, on the main thread. It isn't called when the folder doesn't change.
        /// Returns false with an error when the folder can't be used or a move is already running.
        /// </summary>
        public static bool TryChangeFolder(string requested, Action<string, bool> onDone, out string error)
        {
            if (_busy)
            {
                error = "Received songs are still being indexed or moved. Try again in a moment.";
                return false;
            }

            string target;
            try
            {
                target = string.IsNullOrWhiteSpace(requested) ? DefaultFolder : Path.GetFullPath(requested.Trim());
                CreateFolders(target);
                string probe = Path.Combine(target, ".yargnetplay-write-test");
                File.WriteAllBytes(probe, new byte[0]);
                File.Delete(probe);
            }
            catch (Exception e)
            {
                error = $"That folder can't be used: {e.Message}";
                return false;
            }

            error = null;
            Plugin.ReceivedSongsFolder.Value = SamePath(target, DefaultFolder) ? "" : target;
            if (SamePath(target, Folder)) return true;

            string previous = Folder;
            Folder = target;
            _busy = true;
            MoveSongs(previous, target, onDone);
            return true;
        }

        private static async void MoveSongs(string from, string to, Action<string, bool> onDone)
        {
            string summary;
            bool ok = false;
            try
            {
                (summary, ok) = await Task.Run(() =>
                {
                    int moved = 0, kept = 0;
                    foreach (var file in Directory.Exists(from) ? Directory.GetFiles(from) : new string[0])
                    {
                        if (!IsSongFile(file)) continue;
                        string dest = Path.Combine(to, Path.GetFileName(file));
                        try
                        {
                            if (File.Exists(dest)) { kept++; continue; } // never overwrite or delete on a name clash
                            MoveFile(file, dest);
                            moved++;
                        }
                        catch (Exception e)
                        {
                            Plugin.Log.LogWarning($"Couldn't move {file} to {to}: {e.Message}");
                            kept++;
                        }
                    }

                    // Re-point moved songs at their new files and pick up songs that were already in the new folder.
                    // Songs that couldn't be moved keep working from their old location until the game restarts.
                    IndexFolder(to);
                    TryDeleteEmpty(Path.Combine(from, ".partial"));
                    TryDeleteEmpty(from);

                    return kept == 0
                        ? ($"Moved {moved} received song{(moved == 1 ? "" : "s")} to {to}", true)
                        : ($"Moved {moved} song{(moved == 1 ? "" : "s")} to {to}. {kept} couldn't be moved and stay in {from}.", false);
                });
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Moving received songs failed: {e}");
                summary = $"Moving received songs failed: {e.Message}";
            }

            _busy = false;
            RefreshSize();
            ReceivedLibrary.Refresh();
            onDone?.Invoke(summary, ok);
        }

        /// <summary>File.Move, falling back to copy + delete where a move across drives isn't supported.</summary>
        private static void MoveFile(string source, string dest)
        {
            try
            {
                File.Move(source, dest);
            }
            catch (IOException)
            {
                if (File.Exists(dest)) throw;
                string temp = dest + ".moving";
                File.Copy(source, temp, true);
                File.Move(temp, dest);
                File.Delete(source);
            }
        }

        private static void TryDeleteEmpty(string folder)
        {
            try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
            catch { /* best effort */ }
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

        public static void OpenFolder() => FileExplorerHelper.OpenFolder(Folder);
    }
}
