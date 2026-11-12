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
        private static double _lastEngineTime;
        private static int _inputs, _lateInputs;
        private static double _worstLateness;
        private static bool _active;

        public static void BeginSong(double delay)
        {
            _delay = delay;
            Shifted.Clear();
            _lastEngineTime = double.NegativeInfinity;
            _inputs = _lateInputs = 0;
            _worstLateness = 0;
            _active = true;
            Plugin.Log.LogInfo($"Remote player delay for this song: {delay * 1000:0} ms");
        }

        /// <summary>
        /// Called when the gameplay scene ends. Inputs that arrive after the delayed engine already passed their
        /// time are judged late, so a non-zero count means the delay was too short for this connection.
        /// </summary>
        public static void EndSong(string peerName)
        {
            if (!_active) return;
            _active = false;

            Plugin.Log.LogInfo($"Remote inputs: {_inputs} received, {_lateInputs} late (worst {_worstLateness * 1000:0} ms) " +
                $"with a {_delay * 1000:0} ms delay");
            if (_lateInputs == 0) return;

            string advice = Plugin.AutoDelay.Value
                ? "The connection had a lag spike bigger than recent pings."
                : "Raise the remote player delay on the Online page or turn on Auto delay.";
            UI.Notify.Warning($"{_lateInputs} of {peerName}'s inputs arrived late (up to {_worstLateness * 1000:0} ms). {advice}");
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
                    _inputs++;
                    if (input.Time < _lastEngineTime)
                    {
                        _lateInputs++;
                        _worstLateness = System.Math.Max(_worstLateness, _lastEngineTime - input.Time);
                    }
                    engine.QueueInput(ref input);
                    Accessors.OnInputQueued(player, input);
                    log.Add(input); // keeps the remote player in the saved replay
                }

                engine.Update(time);
                _lastEngineTime = time;
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
