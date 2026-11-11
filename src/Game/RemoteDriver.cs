using System.Collections.Generic;
using UnityEngine;
using YARG.Gameplay;
using YARG.Gameplay.Player;
using YargNetplay.Net;

namespace YargNetplay.Game
{
    /// <summary>
    /// Replaces BasePlayer.GameplayUpdate for the remote player. The remote player runs a fixed
    /// delay behind the shared song clock, so its inputs have always arrived by the time the
    /// engine reaches them, and the engine reproduces the peer's exact result.
    /// </summary>
    internal static class RemoteDriver
    {
        private static readonly HashSet<int> Shifted = new();
        private static double _delay;

        public static void BeginSong(double delay)
        {
            _delay = delay;
            Shifted.Clear();
        }

        public static void Update(BasePlayer player, GameManager gm, Session session)
        {
            if (!gm.Started || gm.Paused) return;

            EnsureVisualShift(player);

            if (!gm.Rewinding)
            {
                double time = gm.InputTime - _delay + player.InputCalibration;
                var engine = player.BaseEngine;
                var log = Accessors.ReplayInputs(player);

                while (session.RemoteInputs.TryDequeueUpTo(time, out var input))
                {
                    engine.QueueInput(ref input);
                    Accessors.OnInputQueued(player, input);
                    log.Add(input); // keeps the remote player in the saved replay
                }

                engine.Update(time);
            }

            Accessors.UpdateVisuals(player, gm.VisualTime - _delay);
        }

        /// <summary>
        /// Note/beatline elements place themselves from the undelayed GameManager.VisualTime in LateUpdate.
        /// Pushing their pool containers forward by delay * noteSpeed makes them appear delayed too,
        /// so notes reach the strike line exactly when the delayed engine judges them.
        /// </summary>
        private static void EnsureVisualShift(BasePlayer player)
        {
            if (player is not TrackPlayer || !Shifted.Add(player.GetInstanceID())) return;

            float shift = (float) (_delay * player.NoteSpeed);
            foreach (var field in Accessors.TrackPools)
            {
                if (field?.GetValue(player) is Component pool)
                    pool.transform.localPosition += new Vector3(0f, 0f, shift);
            }
        }
    }
}
