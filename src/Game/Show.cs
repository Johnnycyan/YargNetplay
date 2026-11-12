using System.Collections.Generic;
using System.Linq;
using YARG.Core.Song;
#if YARG_STATIC_SETLIST
using System.Reflection;
using HarmonyLib;
using YARG.Playlists;
#else
using YARG;
#endif

namespace YargNetplay.Game
{
    /// <summary>
    /// The setlist ("show") YARG is playing, over both of YARG's APIs for it. Up to v0.15.0 and nightly b3912 the show
    /// is a copy of the setlist in PersistentState (PlayingAShow, ShowSongs, ShowIndex). Newer nightlies play the
    /// setlist itself: the static YARG.Playlists.Setlist (IsPlaying, Index, its Playlist). The csproj defines
    /// YARG_STATIC_SETLIST for the YARG builds that have the latter.
    /// </summary>
    internal static class Show
    {
#if YARG_STATIC_SETLIST
        private static readonly MethodInfo IndexSetter = AccessTools.PropertySetter(typeof(Setlist), nameof(Setlist.Index));

        public static bool Playing => Setlist.IsPlaying;

        public static List<SongEntry> Songs => Setlist.ResolveSongs();

        /// <summary>Position in <see cref="Songs"/>. Setting it doesn't change the current song.</summary>
        public static int Index
        {
            get => Setlist.Index;
            set => IndexSetter.Invoke(null, new object[] { value });
        }

        /// <summary>Plays <paramref name="songs"/> as a show from the first song, or as a single song when not a show.</summary>
        public static void Begin(List<SongEntry> songs, bool show)
        {
            if (show)
            {
                // The show plays YARG's setlist, so make it exactly these songs. The hash list is edited directly,
                // which doesn't go through the Playlist methods NativeSetlist shares with the peer.
                var hashes = Setlist.Playlist.SongHashes;
                hashes.Clear();
                hashes.AddRange(songs.Select(s => s.Hash));
                Setlist.TryStart(out _);
            }
            else
            {
                Setlist.EndShow();
            }
        }
#else
        public static bool Playing => GlobalVariables.State.PlayingAShow;

        public static List<SongEntry> Songs => GlobalVariables.State.ShowSongs;

        /// <summary>Position in <see cref="Songs"/>. Setting it doesn't change the current song.</summary>
        public static int Index
        {
            get => GlobalVariables.State.ShowIndex;
            // PersistentState is a struct: write through GlobalVariables.State, not a copy.
            set => GlobalVariables.State.ShowIndex = value;
        }

        /// <summary>Plays <paramref name="songs"/> as a show from the first song, or as a single song when not a show.</summary>
        public static void Begin(List<SongEntry> songs, bool show)
        {
            GlobalVariables.State.PlayingAShow = show;
            GlobalVariables.State.ShowSongs = songs;
            GlobalVariables.State.ShowIndex = 0;
        }
#endif
    }
}
