using System;

namespace ReDefinition.Atmospheres.Audio
{
    // Why the weather sound is silent or on its way.
    internal enum GateReason
    {
        // Everything holds: the gain is 1.
        Open,
        // The master switch is off, the scene has no weather sound or there is no air.
        Off,
        // The game is paused.
        Paused,
        // The vessel is on rails at a warp rate above 1.
        Warp
    }

    // The gain of the whole weather sound over time: one second in on the arrival in a scene, 0.5 s out when an on-rails warp starts, silent
    // until the rate is 1 again, then 1.5 s in; a pause cuts in 0.1 s and returns in 0.3 s. It is stepped on the main thread with the real
    // (unscaled) frame time and handed to the mixer as part of the master volume, which smooths it per sample. Never a pitch change.
    internal sealed class AudioGate
    {
        internal const float ArrivalFadeInS = 1f;
        internal const float WarpFadeOutS = 0.5f;
        internal const float WarpFadeInS = 1.5f;
        internal const float PauseFadeOutS = 0.1f;
        internal const float PauseFadeInS = 0.3f;
        // The most one frame may advance the gate: a hitch (loading) must not skip a fade.
        internal const float MaxStepS = 0.1f;

        private float gain;
        private float upRate = 1f / ArrivalFadeInS;
        private GateReason reason = GateReason.Off;

        internal float Gain { get { return gain; } }
        internal GateReason Reason { get { return reason; } }

        // Back to silence, with the arrival fade ready (a scene came up, or the sound was switched on).
        internal void Arrive()
        {
            gain = 0f;
            upRate = 1f / ArrivalFadeInS;
            reason = GateReason.Off;
        }

        // Steps the gate. enabled: the weather has air and a view. paused: the game's pause. onRailsWarp: the time warp is on rails above
        // rate 1. Returns the gain.
        internal float Step(float dtRealSeconds, bool enabled, bool paused, bool onRailsWarp)
        {
            float dt = dtRealSeconds < 0f ? 0f : dtRealSeconds > MaxStepS ? MaxStepS : dtRealSeconds;
            // The pause and the warp come first: while the game is paused or on rails the view may go stale, and that must not turn into a
            // slow fade to "off".
            GateReason now = paused ? GateReason.Paused : onRailsWarp ? GateReason.Warp : !enabled ? GateReason.Off : GateReason.Open;
            if (now != reason)
            {
                // The way back up depends on what held it down.
                if (now == GateReason.Open)
                {
                    upRate = reason == GateReason.Warp ? 1f / WarpFadeInS : reason == GateReason.Paused ? 1f / PauseFadeInS : 1f / ArrivalFadeInS;
                }
                reason = now;
            }
            float target = now == GateReason.Open ? 1f : 0f;
            if (target > gain)
            {
                gain = Math.Min(target, gain + upRate * dt);
            }
            else if (target < gain)
            {
                float down = now == GateReason.Paused ? 1f / PauseFadeOutS : now == GateReason.Warp ? 1f / WarpFadeOutS : 1f / ArrivalFadeInS;
                gain = Math.Max(target, gain - down * dt);
            }
            return gain;
        }
    }
}
