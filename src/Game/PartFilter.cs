using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using YARG.Core;
using YARG.Core.Song;
using YARG.Localization;
using YARG.Menu.Filters;
using YARG.Menu.MusicLibrary;
using YargNetplay.Net;
using Object = UnityEngine.Object;

namespace YargNetplay.Game
{
    /// <summary>A player's preferred part (instrument + difficulty) for online play, or "any part".</summary>
    internal readonly struct PartChoice : IEquatable<PartChoice>
    {
        public static readonly Difficulty[] Difficulties = { Difficulty.Easy, Difficulty.Medium, Difficulty.Hard, Difficulty.Expert };

        public readonly Instrument? Instrument;
        public readonly Difficulty Difficulty;

        public PartChoice(Instrument? instrument, Difficulty difficulty)
        {
            Instrument = instrument;
            Difficulty = difficulty;
        }

        public bool IsAny => Instrument == null;

        /// <summary>Whether the song has a chart for this instrument at this difficulty (from YARG's own scan data).</summary>
        public bool Allows(SongEntry song)
        {
            if (Instrument is not Instrument instrument) return true;
            try { return song[instrument][Difficulty]; }
            catch { return false; } // instrument this YARG build doesn't track
        }

        public string Label => Instrument is Instrument i ? $"{Difficulty.ToLocalizedName()} {i.ToLocalizedName()}" : "Any part";

        public void Write(BinaryWriter w)
        {
            w.Write(Instrument.HasValue);
            w.Write((int) (Instrument ?? 0));
            w.Write((int) Difficulty);
        }

        public static PartChoice Read(BinaryReader r)
        {
            bool has = r.ReadBoolean();
            var instrument = (Instrument) r.ReadInt32();
            var difficulty = (Difficulty) r.ReadInt32();
            return new PartChoice(has ? instrument : null, difficulty);
        }

        public static PartChoice FromConfig()
        {
            Instrument? instrument = Enum.TryParse(Plugin.PreferredInstrument.Value, out Instrument i) ? i : null;
            var difficulty = Enum.TryParse(Plugin.PreferredDifficulty.Value, out Difficulty d) ? d : Difficulty.Expert;
            return new PartChoice(instrument, difficulty);
        }

        public void SaveToConfig()
        {
            Plugin.PreferredInstrument.Value = Instrument?.ToString() ?? "Any";
            Plugin.PreferredDifficulty.Value = Difficulty.ToString();
        }

        public bool Equals(PartChoice other) => Instrument == other.Instrument && (IsAny || Difficulty == other.Difficulty);
        public override bool Equals(object obj) => obj is PartChoice other && Equals(other);
        public override int GetHashCode() => IsAny ? 0 : ((int) Instrument.Value * 16) + (int) Difficulty;
    }

    /// <summary>
    /// While connected, the music library only lists songs that have a chart for both players' preferred parts.
    /// Hooks YARG's own filter predicate (FiltersMenu), so it combines with YARG's filters and gets the
    /// filtered song counts and selection handling for free.
    /// </summary>
    internal static class PartFilter
    {
        private static PartChoice? _localPart;

        /// <summary>The local player's preferred part (saved in the config, shared with the peer while connected).</summary>
        public static PartChoice LocalPart => _localPart ??= PartChoice.FromConfig();

        public static void SetLocalPart(PartChoice part)
        {
            if (part.Equals(LocalPart)) return;
            _localPart = part;
            part.SaveToConfig();
            ApplyToProfile(part);
            Session.Current?.SendPartChoice();
            Refresh();
        }

        private static bool Active(out PartChoice local, out PartChoice peer)
        {
            local = peer = default;
            if (Session.Current is not { IsConnected: true } session) return false;
            local = LocalPart;
            peer = session.PeerPart;
            return !(local.IsAny && peer.IsAny);
        }

        public static bool Allows(SongEntry song) =>
            !Active(out var local, out var peer) || (local.Allows(song) && peer.Allows(song));

        /// <summary>Re-applies the filter after either player's part (or the connection) changed.</summary>
        public static void Refresh()
        {
            try { FiltersMenu.RefreshActiveFilterPredicate(); }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't refresh the library filter: " + e.Message); }

            var library = Object.FindAnyObjectByType<MusicLibraryMenu>(FindObjectsInactive.Exclude);
            if (library != null) library.RefreshAndReselect();
        }

        [HarmonyPatch(typeof(FiltersMenu), "BuildFilterPredicate")]
        private static class BuildPredicatePatch
        {
            private static void Postfix(ref Func<SongEntry, bool> __result)
            {
                if (!Active(out _, out _)) return;
                var yarg = __result;
                __result = yarg == null ? Allows : song => yarg(song) && Allows(song);
            }
        }

        /// <summary>
        /// Instruments the local player can pick: what their profile's game mode plays, minus vocals (not supported
        /// online yet) and the rarely charted co-op guitar parts.
        /// </summary>
        public static Instrument[] ChoosableInstruments()
        {
            var player = SongLauncher.GetLocalPlayer(out _);
            var modes = player != null
                ? new[] { player.Profile.GameMode }
                : new[] { GameMode.FiveFretGuitar, GameMode.FourLaneDrums, GameMode.FiveLaneDrums, GameMode.SixFretGuitar, GameMode.ProKeys };
            return modes
                .SelectMany(m => { try { return m.PossibleInstruments(); } catch { return Array.Empty<Instrument>(); } })
                .Where(i => i is not (Instrument.Vocals or Instrument.Harmony or Instrument.Band
                    or Instrument.FiveFretCoopGuitar or Instrument.SixFretCoopGuitar))
                .Distinct()
                .ToArray();
        }

        /// <summary>Makes the chosen part the default in difficulty select, when the local profile can play it.</summary>
        public static void ApplyToProfile(PartChoice choice)
        {
            if (choice.Instrument is not Instrument instrument) return;
            var player = SongLauncher.GetLocalPlayer(out _);
            if (player == null || !player.Profile.GameMode.PossibleInstruments().Contains(instrument)) return;

            player.Profile.CurrentInstrument = instrument;
            player.Profile.PreferredInstrument = instrument;
            player.Profile.CurrentDifficulty = choice.Difficulty;
            player.Profile.DifficultyFallback = choice.Difficulty; // what difficulty select starts from
        }
    }
}
