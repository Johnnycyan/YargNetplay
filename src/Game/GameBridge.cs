using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using YARG;
using YARG.Core;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Core.IO;
using YARG.Core.Replays;
using YARG.Core.Song;
using YARG.Player;
using YARG.Settings.Customization;
using YARG.Song;

namespace YargNetplay.Game
{
    internal static class GameInfo
    {
        public static string YargVersion => GlobalVariables.Instance != null ? GlobalVariables.Instance.CurrentVersion : "unknown";
    }

    /// <summary>Lookups into the player's own song library, by the chart hash YARG already computes.</summary>
    internal static class SongLibrary
    {
        /// <summary>
        /// Dev aid for testing transfers with two instances on one PC (which share a library):
        /// with YARGNETPLAY_IGNORE_LIBRARY=1 this instance only "has" songs it received.
        /// </summary>
        private static readonly bool IgnoreLibrary = Environment.GetEnvironmentVariable("YARGNETPLAY_IGNORE_LIBRARY") == "1";

        public static bool TryGet(byte[] hash, out SongEntry song)
        {
            song = null;
            var key = HashWrapper.Create(hash);
            var byHash = SongContainer.SongsByHash;
            if (IgnoreLibrary || byHash == null) return ReceivedSongs.TryGet(key, out song);

            if (byHash.TryGetValue(key, out var list) && list.Count > 0)
            {
                song = list[0];
                return true;
            }
            return ReceivedSongs.TryGet(key, out song);
        }

        public static int LibraryCount => SongContainer.Songs?.Length ?? 0;

        public static bool IsReceived(SongEntry song) => ReceivedSongs.TryGet(song.Hash, out var r) && ReferenceEquals(r, song);

        /// <summary>The player's library plus songs received from peers (which are not in the library).</summary>
        public static List<SongEntry> All()
        {
            var library = SongContainer.Songs ?? Array.Empty<SongEntry>();
            var result = new List<SongEntry>(library.Length + ReceivedSongs.Count);
            result.AddRange(library);
            var byHash = SongContainer.SongsByHash;
            foreach (var received in ReceivedSongs.All)
            {
                if (byHash == null || !byHash.ContainsKey(received.Hash))
                    result.Add(received);
            }
            return result;
        }
    }

    internal sealed class LocalSetup
    {
        public byte[] ProfileBytes;
        public string EnginePresetJson;
    }

    /// <summary>Picks the local player, serializes it for the peer, and loads gameplay the same way the song menu does.</summary>
    internal static class SongLauncher
    {
        private static readonly JsonSerializerSettings PresetJsonSettings =
            (JsonSerializerSettings) AccessTools.Field(typeof(CustomContent), "JsonSettings").GetValue(null);

        public static YargPlayer GetLocalPlayer(out string error)
        {
            var active = PlayerContainer.Players.Where(p => !p.SittingOut).ToList();
            error = active.Count switch
            {
                0 => "No active local player. Add a profile in the main menu first.",
                > 1 => "Netplay currently supports one local player per machine.",
                _ => null,
            };
            if (error != null) return null;

            var player = active[0];
            if (player.Profile.GameMode == GameMode.Vocals)
            {
                error = "Vocals are not supported by netplay yet.";
                return null;
            }
            return player;
        }

        // ToNativeGameMode throws for non-playable values such as Instrument.Band, so map once and skip those.
        private static readonly (Instrument Instrument, GameMode Mode)[] PlayableInstruments =
            Enum.GetValues(typeof(Instrument)).Cast<Instrument>()
                .Select(i => { try { return ((Instrument, GameMode)?) (i, i.ToNativeGameMode()); } catch { return null; } })
                .Where(x => x != null)
                .Select(x => x.Value)
                .ToArray();

        public static IEnumerable<Instrument> InstrumentsFor(YargProfile profile) =>
            PlayableInstruments.Where(x => x.Mode == profile.GameMode).Select(x => x.Instrument);

        public static bool TryPrepareLocalPlayer(SongEntry song, out LocalSetup setup, out string error)
        {
            setup = null;
            var player = GetLocalPlayer(out error);
            if (player == null) return false;

            var profile = player.Profile;
            if (!song.HasInstrument(profile.CurrentInstrument))
            {
                var fallback = InstrumentsFor(profile).Where(song.HasInstrument).ToList();
                if (fallback.Count == 0)
                {
                    error = $"{song.Name.Original} has no part for {profile.GameMode}.";
                    return false;
                }
                profile.CurrentInstrument = fallback[0];
            }

            // TODO: validate that the chart has profile.CurrentDifficulty for this instrument.

            player.RefreshPresets();

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                profile.Serialize(bw);
                bw.Flush();
                setup = new LocalSetup
                {
                    ProfileBytes = ms.ToArray(),
                    EnginePresetJson = JsonConvert.SerializeObject(player.EnginePreset, PresetJsonSettings),
                };
            }
            return true;
        }

        /// <summary>Builds the remote player as a replay-style player: no bindings, inputs fed by the mod.</summary>
        public static YargPlayer CreateRemotePlayer(Net.PeerSetup setup)
        {
            YargProfile profile;
            using (var data = FixedArray.Read(new MemoryStream(setup.ProfileBytes), setup.ProfileBytes.Length))
            {
                var stream = data.ToValueStream();
                profile = new YargProfile(ref stream);
            }

            // Engine parameters are filled in when the gameplay player creates its engine (see CreateEnginePatch).
            var frame = new ReplayFrame(profile, null, null, Array.Empty<GameInput>());
            var player = new YargPlayer(frame, NewReplayData(frame));

            try
            {
                var preset = JsonConvert.DeserializeObject<EnginePreset>(setup.EnginePresetJson, PresetJsonSettings);
                if (preset != null) Accessors.SetEnginePreset(player, preset);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not read the peer's engine preset, using theirs by id or default: {e.Message}");
            }

            return player;
        }

        /// <summary>
        /// Replay data holding just this frame. ReplayData's constructor differs between YARG builds (newer ones added
        /// rock meter presets and a no-fail flag), so fill whichever one this build has: empty preset dictionaries,
        /// defaults for flags, no frame times.
        /// </summary>
        private static ReplayData NewReplayData(ReplayFrame frame)
        {
            var ctor = typeof(ReplayData).GetConstructors()
                .First(c => c.GetParameters().Any(p => p.ParameterType == typeof(ReplayFrame[])));
            var args = ctor.GetParameters().Select(p =>
                p.ParameterType == typeof(ReplayFrame[]) ? new[] { frame } :
                p.ParameterType == typeof(double[]) ? Array.Empty<double>() :
                Activator.CreateInstance(p.ParameterType)).ToArray();
            return (ReplayData) ctor.Invoke(args);
        }
    }

    /// <summary>
    /// Main-thread FIFO of the peer's inputs, in their song time. Inputs arrive numbered, over both TCP and UDP, so
    /// duplicates are dropped and ones that arrive early (a lost UDP packet) wait until the gap is filled.
    /// </summary>
    internal sealed class RemoteInputBuffer
    {
        private readonly Queue<GameInput> _queue = new();
        private readonly Dictionary<int, GameInput> _early = new();
        private readonly List<(int lastSeq, double time)> _pendingMarks = new();
        private int _nextSeq;

        /// <summary>
        /// The peer's engine time up to which every input they will ever send has arrived (-infinity before the first
        /// one). The remote player must not run past it.
        /// </summary>
        public double Watermark { get; private set; } = double.NegativeInfinity;

        public void Clear()
        {
            _queue.Clear();
            _early.Clear();
            _pendingMarks.Clear();
            _nextSeq = 0;
            Watermark = double.NegativeInfinity;
        }

        public void Add(int seq, GameInput input)
        {
            if (seq < _nextSeq || _early.ContainsKey(seq)) return;
            _early[seq] = input;
            while (_early.TryGetValue(_nextSeq, out var next))
            {
                _early.Remove(_nextSeq);
                _queue.Enqueue(next);
                _nextSeq++;
            }
            ApplyMarks();
        }

        /// <summary>The peer reached <paramref name="time"/> after sending input <paramref name="lastSeq"/>. Counts once that input is here.</summary>
        public void SetWatermark(int lastSeq, double time)
        {
            if (time <= Watermark) return;
            _pendingMarks.Add((lastSeq, time));
            ApplyMarks();
        }

        private void ApplyMarks()
        {
            for (int i = _pendingMarks.Count - 1; i >= 0; i--)
            {
                var (lastSeq, time) = _pendingMarks[i];
                if (lastSeq < _nextSeq) Watermark = Math.Max(Watermark, time);
                if (lastSeq < _nextSeq || time <= Watermark) _pendingMarks.RemoveAt(i);
            }
        }

        public bool TryDequeueUpTo(double time, out GameInput input)
        {
            if (_queue.Count > 0 && _queue.Peek().Time <= time)
            {
                input = _queue.Dequeue();
                return true;
            }
            input = default;
            return false;
        }
    }
}
