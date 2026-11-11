using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using YARG.Core.Engine;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Gameplay;
using YARG.Gameplay.Player;
using YARG.Player;

namespace YargNetplay.Game
{
    /// <summary>
    /// Every private/protected YARG member the mod touches, in one place.
    /// When a YARG update breaks the mod, this file and the patch targets are where to look.
    /// </summary>
    internal static class Accessors
    {
        // YargPlayer
        private static readonly MethodInfo IsReplaySetter = AccessTools.PropertySetter(typeof(YargPlayer), nameof(YargPlayer.IsReplay));
        private static readonly MethodInfo EnginePresetSetter = AccessTools.PropertySetter(typeof(YargPlayer), nameof(YargPlayer.EnginePreset));

        public static void SetIsReplay(YargPlayer player, bool value) => IsReplaySetter.Invoke(player, new object[] { value });
        public static void SetEnginePreset(YargPlayer player, EnginePreset preset) => EnginePresetSetter.Invoke(player, new object[] { preset });

        // GameManager
        private static readonly MethodInfo YargPlayersSetter = AccessTools.PropertySetter(typeof(GameManager), nameof(GameManager.YargPlayers));

        public static void SetYargPlayers(GameManager gm, IReadOnlyList<YargPlayer> players) => YargPlayersSetter.Invoke(gm, new object[] { players });

        // BasePlayer
        public static readonly AccessTools.FieldRef<BasePlayer, List<GameInput>> ReplayInputs =
            AccessTools.FieldRefAccess<BasePlayer, List<GameInput>>("_replayInputs");

        public static readonly Action<BasePlayer, double> UpdateVisuals =
            AccessTools.MethodDelegate<Action<BasePlayer, double>>(AccessTools.Method(typeof(BasePlayer), "UpdateVisuals"));

        public static readonly Action<BasePlayer, GameInput> OnInputQueued =
            AccessTools.MethodDelegate<Action<BasePlayer, GameInput>>(AccessTools.Method(typeof(BasePlayer), "OnInputQueued"));

        // TrackPlayer: pools whose children are positioned from GameManager.VisualTime
        public static readonly FieldInfo[] TrackPools =
        {
            AccessTools.Field(typeof(TrackPlayer), "NotePool"),
            AccessTools.Field(typeof(TrackPlayer), "LanePool"),
            AccessTools.Field(typeof(TrackPlayer), "BeatlinePool"),
            AccessTools.Field(typeof(TrackPlayer), "EffectPool"),
        };
    }
}
