using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using YARG;
using YARG.Core.Engine;
using YARG.Gameplay;
using YARG.Gameplay.HUD;
using YARG.Gameplay.Player;
using YARG.Player;
using YARG.Scores;
using YargNetplay.Net;

namespace YargNetplay.Game
{
    /// <summary>Gameplay-scene state for the current netplay song, plus the Harmony patches that use it.</summary>
    internal static class GameHooks
    {
        public static GameManager GameManager { get; private set; }
        public static YargPlayer RemotePlayer { get; private set; }
        public static YargPlayer LocalPlayer { get; private set; }

        private static bool _applyingRemotePause;
        private static bool _applyingRemoteLeave;

        /// <summary>The session while a netplay song's scene is up, including while a restart or skip waits for the peer.</summary>
        public static Session ActiveSession =>
            Session.Current is { ActivePeerSetup: not null } s && (s.InSongFlow || s.ChangingSong) ? s : null;

        public static bool IsRemote(YargPlayer player) => player != null && player == RemotePlayer;

        /// <summary>The peer's player from the last song. Unlike RemotePlayer it outlives the gameplay scene, for the score screen.</summary>
        private static YargPlayer _lastRemotePlayer;

        public static void Clear()
        {
            GameManager = null;
            RemotePlayer = null;
            LocalPlayer = null;
            _startAligned = false;
        }

        public static void ApplyRemotePause(bool paused)
        {
            if (GameManager == null || !GameManager.Started || GameManager.Paused == paused) return;
            _applyingRemotePause = true;
            try { GameManager.SetPaused(paused); }
            finally { _applyingRemotePause = false; }
        }

        // ------------------------------------------------------------ ready gate

        /// <summary>
        /// Every way into gameplay (difficulty select, setlist continue, score-screen restart, pause menu)
        /// goes through GlobalVariables.LoadScene, so this is the single place both players are synced up.
        /// </summary>
        [HarmonyPatch(typeof(GlobalVariables), nameof(GlobalVariables.LoadScene))]
        private static class LoadScenePatch
        {
            private static bool Prefix(SceneIndex scene)
            {
                if (scene != SceneIndex.Gameplay) return true;

                var session = Session.Current;
                var state = GlobalVariables.State;
                if (session is not { IsConnected: true } || state.CurrentReplay != null || state.IsPractice) return true;

                return session.TryPassGameplayGate();
            }
        }

        /// <summary>
        /// Restart and Skip (pause and fail menus) change the song for both players: tell the peer, then let YARG
        /// reload the scene as usual. The reload waits at the ready gate until the peer has followed.
        /// </summary>
        [HarmonyPatch(typeof(PauseMenuManager))]
        private static class PauseMenuPatch
        {
            private static IEnumerable<MethodBase> TargetMethods() => new[]
            {
                AccessTools.Method(typeof(PauseMenuManager), nameof(PauseMenuManager.Restart)),
                AccessTools.Method(typeof(PauseMenuManager), nameof(PauseMenuManager.Skip)),
            };

            private static bool Prefix(MethodBase __originalMethod)
            {
                var session = ActiveSession;
                if (session == null || GameManager == null) return true;
                if (session.ChangingSong) return false; // already restarting or skipping

                bool skip = __originalMethod.Name == nameof(PauseMenuManager.Skip);
                if (skip)
                {
                    // YARG only offers Skip with a next song (and would index past the end otherwise).
                    var songs = Show.Songs;
                    if (!Show.Playing || Show.Index >= songs.Count - 1) return false;
                    session.BeginSongChange(songs[Show.Index + 1], Show.Index + 1, skip: true);
                }
                else
                {
                    session.BeginSongChange(GlobalVariables.State.CurrentSong, Show.Index, skip: false);
                }
                return true;
            }
        }

        // ------------------------------------------------------------ player injection

        /// <summary>Add the remote player to the player list right after GameManager reads PlayerContainer.</summary>
        [HarmonyPatch(typeof(GameManager), "Awake")]
        private static class GameManagerAwakePatch
        {
            private static void Postfix(GameManager __instance)
            {
                Clear();
                var session = ActiveSession;
                if (session == null || __instance.Song == null) return;

                try
                {
                    GameManager = __instance;
                    LocalPlayer = __instance.YargPlayers.FirstOrDefault(p => !p.SittingOut && !p.IsReplay);
                    RemotePlayer = _lastRemotePlayer = SongLauncher.CreateRemotePlayer(session.ActivePeerSetup);
                    RemoteDriver.BeginSong(session.RemoteDelaySeconds);

                    var players = new List<YargPlayer>(__instance.YargPlayers) { RemotePlayer };
                    Accessors.SetYargPlayers(__instance, players.ToArray());

                    Plugin.Log.LogInfo($"Injected remote player {RemotePlayer.Profile.Name} ({RemotePlayer.Profile.CurrentInstrument})");
                }
                catch (Exception e)
                {
                    // Never leave YARG with a half-built gameplay scene: log it and play this song without the peer.
                    Plugin.Log.LogError($"Couldn't add {session.PeerName}'s player to this song: {e}");
                    UI.Notify.Error($"Couldn't add {session.PeerName}'s player to this song. See the BepInEx log.");
                    Clear();
                }
            }
        }

        /// <summary>
        /// Build the remote engine through the game's normal (non-replay) path so it gets real
        /// parameters from the peer's engine preset, then store them as the replay override that
        /// the rest of the player code expects for IsReplay players.
        /// </summary>
        [HarmonyPatch]
        private static class CreateEnginePatch
        {
            private static IEnumerable<MethodBase> TargetMethods() =>
                AccessTools.GetTypesFromAssembly(typeof(BasePlayer).Assembly)
                    .Where(t => typeof(BasePlayer).IsAssignableFrom(t) && !t.IsAbstract && !t.ContainsGenericParameters)
                    .Select(t => AccessTools.DeclaredMethod(t, "CreateEngine"))
                    .Where(m => m != null);

            private static void Prefix(BasePlayer __instance, out bool __state)
            {
                __state = IsRemote(__instance.Player);
                if (__state) Accessors.SetIsReplay(__instance.Player, false);
            }

            private static void Postfix(BasePlayer __instance, object __result, bool __state)
            {
                if (!__state) return;
                Accessors.SetIsReplay(__instance.Player, true);
                if (__result is BaseEngine engine)
                {
                    // Replay players take their speed from the override (TrackPlayer.Initialize calls
                    // SetSpeed(override.SongSpeed)); fresh params have 0, which zeroes hit windows and
                    // makes NoteSpeed infinite. Netplay always runs at 1x (see SongLauncher.LoadGameplay).
                    engine.BaseParameters.SongSpeed = 1.0;
                    __instance.Player.EngineParameterOverride = engine.BaseParameters;
                }
            }
        }

        // ------------------------------------------------------------ start gate

        /// <summary>Hold the song (the runner lazy-starts on its first Update) until both sides are loaded and the agreed time passes.</summary>
        [HarmonyPatch(typeof(GameManager), "Update")]
        private static class GameManagerUpdatePatch
        {
            private static bool Prefix(GameManager __instance)
            {
                var session = ActiveSession;
                if (session == null || __instance != GameManager) return true;
                // The next song is agreed and this scene is unloading: freeze it, and don't report it as loaded.
                if (session.ReplacingSong) return false;

                session.MarkLocalLoaded();
                return session.IsStartGateOpen();
            }

            /// <summary>
            /// Both games open the gate at the same host-clock time, but YARG's song clock can still start late on
            /// one of them: it waits out lagging frames (up to a second) before starting, and the first frames of a
            /// song are often heavy (venue loading). Every song clock starts from the same song time, so a clock
            /// that started late is behind by exactly that lateness. Measure it on the first running frame and catch up.
            /// </summary>
            private static void Postfix(GameManager __instance)
            {
                var session = ActiveSession;
                if (session == null || __instance != GameManager || !__instance.Started) return;
                if (session.StartAtHostTime is not double startAt) return;
                double sinceStart = session.HostNow - startAt;

                if (!_startAligned)
                {
                    _startAligned = true;
                    if (sinceStart >= 0.005)
                        __instance.SetSongTime(__instance.SongTime + sinceStart * __instance.SongSpeed, 0);
                    _timelineOrigin = __instance.SongTime - sinceStart * __instance.SongSpeed;
                    _clockChecked = false;
                    // The origin (song time at the agreed start) should be the same in both games' logs.
                    Plugin.Log.LogInfo($"Song clock started {sinceStart * 1000:0} ms after the agreed start" +
                        $"{(sinceStart >= 0.005 ? " and caught up" : "")}; timeline origin {_timelineOrigin:0.0000} s");
                    return;
                }

                // One check per song: how far this game's song time is from the shared timeline (compare both logs).
                if (!_clockChecked && sinceStart >= 10 && !__instance.Paused)
                {
                    _clockChecked = true;
                    double off = __instance.SongTime - (_timelineOrigin + sinceStart * __instance.SongSpeed);
                    Plugin.Log.LogInfo($"Clock check 10 s in: {off * 1000:+0;-0} ms from the shared timeline");
                }
            }
        }

        private static bool _startAligned, _clockChecked;
        private static double _timelineOrigin;

        // ------------------------------------------------------------ inputs

        [HarmonyPatch(typeof(BasePlayer), nameof(BasePlayer.GameplayUpdate))]
        private static class GameplayUpdatePatch
        {
            private static bool Prefix(BasePlayer __instance)
            {
                var session = ActiveSession;
                if (session == null || !IsRemote(__instance.Player)) return true;

                RemoteDriver.Update(__instance, GameManager, session);
                return false;
            }
        }

        /// <summary>Forward every input the local engine accepted. _replayInputs only grows when the input was really queued.</summary>
        [HarmonyPatch(typeof(BasePlayer), "OnGameInput")]
        private static class OnGameInputPatch
        {
            private static void Prefix(BasePlayer __instance, out int __state)
            {
                __state = ActiveSession != null && __instance.Player == LocalPlayer
                    ? Accessors.ReplayInputs(__instance).Count
                    : -1;
            }

            private static void Postfix(BasePlayer __instance, int __state)
            {
                if (__state < 0) return;
                var inputs = Accessors.ReplayInputs(__instance);
                for (int i = __state; i < inputs.Count; i++)
                    ActiveSession?.SendInput(inputs[i]);
            }
        }

        // ------------------------------------------------------------ pause / quit / scores

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.Pause))]
        private static class PausePatch
        {
            private static void Postfix(GameManager __instance)
            {
                if (!_applyingRemotePause && __instance == GameManager) ActiveSession?.SendPause(true);
            }
        }

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.Resume))]
        private static class ResumePatch
        {
            private static void Prefix(GameManager __instance)
            {
                if (!_applyingRemotePause && __instance == GameManager) ActiveSession?.SendPause(false);
            }
        }

        /// <summary>
        /// Back to Library (pause and fail menus) asks first online. Leaving ends the song for both players,
        /// so they stay on the same screen.
        /// </summary>
        [HarmonyPatch(typeof(PauseMenuManager), nameof(PauseMenuManager.Quit))]
        private static class QuitPatch
        {
            private static bool Prefix()
            {
                var session = ActiveSession;
                if (session == null || GameManager == null) return true;

                UI.NetplayDialogs.ConfirmLeaveSong(session.PeerName, () =>
                {
                    if (GameManager != null) GameManager.ForceQuitSong();
                });
                return false;
            }
        }

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.ForceQuitSong))]
        private static class ForceQuitPatch
        {
            private static void Prefix()
            {
                if (!_applyingRemoteLeave) ActiveSession?.SendLeaveSong();
            }
        }

        /// <summary>The other player left the song: leave it too, without telling them back. False when not in a song.</summary>
        public static bool ApplyRemoteLeave()
        {
            UI.NetplayDialogs.CloseLeaveConfirm();
            if (GameManager == null) return false;

            _applyingRemoteLeave = true;
            try { GameManager.ForceQuitSong(); }
            finally { _applyingRemoteLeave = false; }
            return true;
        }

        /// <summary>
        /// GameManager.Awake writes the current song to currentSong.txt (for stream overlays). Two games on one PC
        /// share that file, and netplay starts both loads at the same moment, so one write can hit a sharing
        /// violation, which throws out of Awake and leaves that game's scene half-built. Drop the failed write instead.
        /// </summary>
        [HarmonyPatch(typeof(YARG.Integration.CurrentSongController), "OnGameStateChange")]
        private static class CurrentSongFilePatch
        {
            private static Exception Finalizer(Exception __exception)
            {
                if (__exception is not System.IO.IOException io) return __exception;
                Plugin.Log.LogWarning("Skipped updating currentSong.txt (in use by another YARG): " + io.Message);
                return null;
            }
        }

        /// <summary>
        /// The peer is a replay-style player internally, so its score card would be tagged "Replay". Show it like a
        /// live player instead (Full Combo or Cleared). Its "high score" would be checked against this machine's
        /// database, which never holds the peer's scores, so it's never tagged High Score.
        /// </summary>
        [HarmonyPatch]
        private static class ScoreCardTagPatch
        {
            private static IEnumerable<MethodBase> TargetMethods() =>
                AccessTools.GetTypesFromAssembly(typeof(GameManager).Assembly)
                    .Where(t => !t.IsAbstract && !t.ContainsGenericParameters && IsScoreCard(t))
                    .Select(t => AccessTools.DeclaredMethod(t, "SetCardContents"))
                    .Where(m => m != null);

            private static bool IsScoreCard(Type t)
            {
                for (var b = t.BaseType; b != null; b = b.BaseType)
                    if (b.IsGenericType && b.GetGenericTypeDefinition() == typeof(YARG.Menu.ScoreScreen.ScoreCard<>)) return true;
                return false;
            }

            private static void Prefix(UnityEngine.MonoBehaviour __instance, out YargPlayer __state)
            {
                var player = Traverse.Create(__instance).Property("Player").GetValue<YargPlayer>();
                __state = player != null && player == _lastRemotePlayer ? player : null;
                if (__state == null) return;

                // Older builds read the player's IsReplay; newer ones copy it into the card's own IsReplay field.
                Accessors.SetIsReplay(player, false);
                var card = Traverse.Create(__instance);
                if (card.Field("IsReplay").FieldExists()) card.Field("IsReplay").SetValue(false);
                card.Field("IsHighScore").SetValue(false);
            }

            private static void Postfix(YargPlayer __state)
            {
                if (__state != null) Accessors.SetIsReplay(__state, true);
            }
        }

        /// <summary>Never write the peer's score into this machine's score database.</summary>
        [HarmonyPatch(typeof(ScoreContainer), nameof(ScoreContainer.IsSoloScoreValid))]
        private static class ScoreValidPatch
        {
            private static bool Prefix(YargPlayer player, ref bool __result)
            {
                if (!IsRemote(player)) return true;
                __result = false;
                return false;
            }
        }
    }
}
