using System;
using System.Collections.Generic;
using UnityEngine;
using YARG.Gameplay;
using YARG.Gameplay.Player;
using YargNetplay.Net;

namespace YargNetplay.Game
{
    /// <summary>
    /// Replaces BasePlayer.GameplayUpdate for the remote player. The remote player runs a delay behind the shared song
    /// clock, so its inputs have normally arrived by the time the engine reaches them, and the engine reproduces the
    /// peer's exact result.
    ///
    /// It also never runs past the peer's watermark (<see cref="RemoteInputBuffer.Watermark"/>): the time up to which
    /// every input of theirs has arrived. When a lag spike holds their packets up, the remote highway pauses instead of
    /// judging notes without the inputs, then catches up. So a delay that's too short only costs a short pause, never
    /// a wrong result.
    ///
    /// With Auto delay on, the delay follows the lag measured during the song (the worst of the last
    /// <see cref="LagWindow"/> seconds, plus a margin), so the highway rarely has to pause. Changes are eased in by
    /// running the remote player slightly slower or faster than the song.
    /// </summary>
    internal static class RemoteDriver
    {
        private const double MinDelay = 0.030, MaxDelay = 1.0;
        private const double DelayMargin = 0.020;
        private const double LagWindow = 15, LagBucket = 0.5;
        // Until lag has been measured this long, the starting delay (from ping, or the fixed one) is kept as a floor.
        private const double SettleTime = 2;
        // How much slower or faster than the song the remote player may run while its delay changes.
        private const double MinRate = 0.75, MaxRate = 1.25;
        // Further ahead than this (the song clock jumped back), it just waits for the song to catch up.
        private const double HoldWhenAhead = 0.25;

        private static bool _active, _adaptive;
        private static double _startDelay, _targetDelay;
        private static double _engineTime, _lastSongTime, _firstLagTime;
        private static readonly List<(double start, double lag)> LagBuckets = new();

        private static int _shiftedPlayer;
        private static float _appliedShift;

        private static int _inputs, _lateInputs, _stalls;
        private static double _worstLateness, _stallTime, _highestDelay;
        private static bool _stalled;

        /// <summary>How far behind the song the remote player is right now (for the debug window).</summary>
        public static double CurrentDelay { get; private set; }

        public static void BeginSong(double delay)
        {
            _startDelay = _targetDelay = CurrentDelay = _highestDelay = delay;
            _adaptive = Plugin.AutoDelay.Value;
            _engineTime = _lastSongTime = _firstLagTime = double.NegativeInfinity;
            LagBuckets.Clear();
            _shiftedPlayer = 0;
            _appliedShift = 0;
            _inputs = _lateInputs = _stalls = 0;
            _worstLateness = _stallTime = 0;
            _stalled = false;
            _active = true;
            Plugin.Log.LogInfo($"Remote player delay for this song: starts at {delay * 1000:0} ms{(_adaptive ? ", adjusts to the measured lag" : "")}");
        }

        /// <summary>
        /// Called when the gameplay scene ends. Late inputs (arriving after the engine passed their time) shouldn't
        /// happen any more, since the engine waits for the watermark; a non-zero count points at a bug.
        /// </summary>
        public static void EndSong(string peerName)
        {
            if (!_active) return;
            _active = false;

            Plugin.Log.LogInfo($"Remote inputs: {_inputs} received, {_lateInputs} late (worst {_worstLateness * 1000:0} ms). " +
                $"Delay started at {_startDelay * 1000:0} ms, ended at {_targetDelay * 1000:0} ms, highest {_highestDelay * 1000:0} ms. " +
                $"Waited for {peerName}'s inputs {_stalls} time(s), {_stallTime:0.00} s in all.");
            if (_lateInputs > 0)
                UI.Notify.Warning($"{_lateInputs} of {peerName}'s inputs arrived late (up to {_worstLateness * 1000:0} ms).");
        }

        public static void Update(BasePlayer player, GameManager gm, Session session)
        {
            if (!gm.Started || gm.Paused) return;

            if (!gm.Rewinding)
            {
                // The time the remote engine would be at with no delay (the peer's calibration is part of their input times).
                double songTime = gm.InputTime + player.InputCalibration;
                double dt = double.IsNegativeInfinity(_lastSongTime) ? 0 : Math.Max(0, songTime - _lastSongTime);
                _lastSongTime = songTime;

                var inputs = session.RemoteInputs;
                double mark = inputs.Watermark;
                if (!double.IsNegativeInfinity(mark)) MeasureLag(songTime, songTime - mark);

                double wanted = songTime - _targetDelay;
                double next;
                if (double.IsNegativeInfinity(_engineTime))
                {
                    next = Math.Min(wanted, mark); // first update: start right there
                }
                else
                {
                    double error = wanted - _engineTime;
                    double step = error < -HoldWhenAhead ? 0
                        : dt + Math.Max(-dt * (1 - MinRate), Math.Min(dt * (MaxRate - 1), error));
                    double paced = _engineTime + step;
                    next = Math.Max(_engineTime, Math.Min(paced, mark));

                    bool stalled = mark < paced;
                    if (stalled)
                    {
                        _stallTime += dt;
                        if (!_stalled) _stalls++;
                    }
                    _stalled = stalled;
                }

                if (next > _engineTime)
                    Advance(player, inputs, next);
            }

            CurrentDelay = double.IsNegativeInfinity(_engineTime) ? _targetDelay : _lastSongTime - _engineTime;
            ShiftVisuals(player, CurrentDelay);
            Accessors.UpdateVisuals(player, gm.VisualTime - CurrentDelay);
        }

        private static void Advance(BasePlayer player, RemoteInputBuffer inputs, double time)
        {
            var engine = player.BaseEngine;
            var log = Accessors.ReplayInputs(player);

            while (inputs.TryDequeueUpTo(time, out var input))
            {
                _inputs++;
                if (input.Time < _engineTime)
                {
                    _lateInputs++;
                    _worstLateness = Math.Max(_worstLateness, _engineTime - input.Time);
                }
                engine.QueueInput(ref input);
                Accessors.OnInputQueued(player, input);
                log.Add(input); // keeps the remote player in the saved replay
            }

            engine.Update(time);
            _engineTime = time;
        }

        /// <summary>
        /// Records how far the peer's watermark is behind the song (the delay that would have been needed right now),
        /// and with Auto delay on sets the delay from the worst of the last <see cref="LagWindow"/> seconds.
        /// </summary>
        private static void MeasureLag(double songTime, double lag)
        {
            if (double.IsNegativeInfinity(_firstLagTime)) _firstLagTime = songTime;

            int last = LagBuckets.Count - 1;
            if (last >= 0 && songTime - LagBuckets[last].start < LagBucket)
                LagBuckets[last] = (LagBuckets[last].start, Math.Max(LagBuckets[last].lag, lag));
            else
                LagBuckets.Add((songTime, lag));
            while (LagBuckets.Count > 1 && LagBuckets[0].start < songTime - LagWindow) LagBuckets.RemoveAt(0);

            if (!_adaptive) return;

            double worst = double.NegativeInfinity;
            foreach (var bucket in LagBuckets) worst = Math.Max(worst, bucket.lag);
            double delay = worst + DelayMargin;
            if (songTime - _firstLagTime < SettleTime) delay = Math.Max(delay, _startDelay);
            _targetDelay = Math.Max(MinDelay, Math.Min(MaxDelay, delay));
            _highestDelay = Math.Max(_highestDelay, _targetDelay);
        }

        /// <summary>
        /// Note/beatline elements place themselves from the undelayed GameManager.VisualTime in LateUpdate.
        /// Pushing their pool containers forward by delay * noteSpeed makes them appear delayed too,
        /// so notes reach the strike line exactly when the delayed engine judges them. The delay changes during the
        /// song, so the shift follows it every frame.
        /// </summary>
        private static void ShiftVisuals(BasePlayer player, double delay)
        {
            if (player is not TrackPlayer) return;
            if (_shiftedPlayer != player.GetInstanceID())
            {
                _shiftedPlayer = player.GetInstanceID();
                _appliedShift = 0;
            }

            float shift = (float) (delay * player.NoteSpeed);
            float change = shift - _appliedShift;
            if (Mathf.Abs(change) < 1e-4f) return;
            _appliedShift = shift;

            foreach (var field in Accessors.TrackPools)
            {
                if (field?.GetValue(player) is Component pool)
                    pool.transform.localPosition += new Vector3(0f, 0f, change);
            }
        }
    }
}
