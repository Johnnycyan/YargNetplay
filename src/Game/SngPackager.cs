using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace YargNetplay.Game
{
    /// <summary>
    /// Packs a loose song folder into a single .sng file
    /// (format: https://github.com/mdsitton/SngFileFormat, matching YARG.Core's SngFile reader).
    /// song.ini becomes the metadata section; every other top-level file becomes a listing.
    /// </summary>
    internal static class SngPackager
    {
        private const uint SNG_VERSION = 1;
        private const int KEY_COUNT = 16;
        private const int MASK_SIZE = 256;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SNGPKG");

        // Background videos can be hundreds of MB and are purely cosmetic, so they are not sent.
        private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".webm", ".avi", ".mkv", ".mov", ".m4v", ".wmv",
        };

        public static void Pack(string folder, string outputPath)
        {
            var files = Directory.GetFiles(folder)
                .Where(f => !Path.GetFileName(f).Equals("song.ini", StringComparison.OrdinalIgnoreCase))
                .Where(f => !SkippedExtensions.Contains(Path.GetExtension(f)))
                .Select(f => (Path: f, Name: Encoding.UTF8.GetBytes(Path.GetFileName(f)), Length: new FileInfo(f).Length))
                .Where(f => f.Name.Length <= byte.MaxValue)
                .ToList();

            var metadata = ReadIniSongSection(Path.Combine(folder, "song.ini"));

            byte[] metaSection = BuildMetadata(metadata);

            // Header (magic + version + keys), metadata block, listing block, data-length field.
            long indexEntriesLength = files.Sum(f => 1 + f.Name.Length + 2 * sizeof(ulong));
            long dataStart = Magic.Length + sizeof(uint) + KEY_COUNT
                + sizeof(ulong) + metaSection.Length
                + sizeof(ulong) + sizeof(ulong) + indexEntriesLength
                + sizeof(ulong);

            var keys = new byte[KEY_COUNT];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(keys);
            var mask = new byte[MASK_SIZE];
            for (int i = 0; i < MASK_SIZE; i++) mask[i] = (byte) (keys[i % KEY_COUNT] ^ i);

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            using var w = new BinaryWriter(fs);

            w.Write(Magic);
            w.Write(SNG_VERSION);
            w.Write(keys);

            // Section lengths include the 8-byte count that follows them (see SngFile.LoadMetadata/LoadListings).
            w.Write((ulong) metaSection.Length);
            w.Write(metaSection);

            w.Write((ulong) (sizeof(ulong) + indexEntriesLength));
            w.Write((ulong) files.Count);
            long position = dataStart;
            foreach (var f in files)
            {
                w.Write((byte) f.Name.Length);
                w.Write(f.Name);
                w.Write((ulong) f.Length);
                w.Write((ulong) position);
                position += f.Length;
            }

            w.Write((ulong) (position - dataStart));

            var buffer = new byte[1 << 16];
            foreach (var f in files)
            {
                using var input = File.OpenRead(f.Path);
                long fileOffset = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    // The mask index restarts at 0 for each file.
                    for (int i = 0; i < read; i++)
                        buffer[i] ^= mask[(fileOffset + i) % MASK_SIZE];
                    w.Write(buffer, 0, read);
                    fileOffset += read;
                }
            }
        }

        /// <summary>Metadata block: [ulong count] then count x ([int len][utf8 key] [int len][utf8 value]), prefixed by its own length by the caller.</summary>
        private static byte[] BuildMetadata(List<(string Key, string Value)> pairs)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((ulong) pairs.Count);
            foreach (var (key, value) in pairs)
            {
                var k = Encoding.UTF8.GetBytes(key);
                var v = Encoding.UTF8.GetBytes(value);
                w.Write(k.Length);
                w.Write(k);
                w.Write(v.Length);
                w.Write(v);
            }
            w.Flush();
            return ms.ToArray();
        }

        private static List<(string Key, string Value)> ReadIniSongSection(string iniPath)
        {
            var pairs = new List<(string, string)>();
            if (!File.Exists(iniPath)) return pairs;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool inSong = false;
            foreach (var raw in File.ReadAllLines(iniPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;

                if (line[0] == '[')
                {
                    inSong = line.Equals("[song]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSong) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                var value = line.Substring(eq + 1).Trim();
                if (key.Length > 0 && value.Length > 0 && seen.Add(key))
                    pairs.Add((key, value));
            }
            return pairs;
        }
    }
}
