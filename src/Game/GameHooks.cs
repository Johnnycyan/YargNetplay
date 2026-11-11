using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using YARG.Core.Engine;
using YARG.Gameplay;
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

        public static Session ActiveSession =>
            Session.Current is { InSongFlow: true, PeerSetup: not null } s ? s : null;

        public static bool IsRemote(YargPlayer player) => player != null && player == RemotePlayer;

        public static void Clear()
        {
            GameManager = null;
            RemotePlayer = null;
            LocalPlayer = null;
        }

        public static void ApplyRemotePause(bool paused)
        {
            if (GameManager == null || !GameManager.Started || GameManager.Paused == paused) return;
            _applyingRemotePause = true;
            try { GameManager.SetPaused(paused); }
            finally { _applyingRemotePause = false; }
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

                GameManager = __instance;
                LocalPlayer = __instance.YargPlayers.FirstOrDefault(p => !p.SittingOut && !p.IsReplay);
                RemotePlayer = SongLauncher.CreateRemotePlayer(session.PeerSetup);
                RemoteDriver.BeginSong(session.RemoteDelaySeconds);

                var players = new List<YargPlayer>(__instance.YargPlayers) { RemotePlayer };
                Accessors.SetYargPlayers(__instance, players.ToArray());

                Plugin.Log.LogInfo($"Injected remote player {RemotePlayer.Profile.Name} ({RemotePlayer.Profile.CurrentInstrument})");
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

                session.MarkLocalLoaded();
                return session.IsStartGateOpen();
            }
        }

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

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.ForceQuitSong))]
        private static class ForceQuitPatch
        {
            private static void Prefix() => ActiveSession?.SendLeaveSong();
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
