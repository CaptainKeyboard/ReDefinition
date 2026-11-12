using System;

namespace ReDefinition.Atmospheres.Audio
{
    // The numbers of the thunder (package T07): whether the delay is the real one, the speed of sound, the loudness and the voices.
    internal sealed class ThunderSettings
    {
        // The delay is distance / speed of sound (on); off: the thunder follows the flash at once.
        public bool RealisticDelay = true;
        // The speed of sound in m/s (the standard value at 20 degrees Celsius, tests/data/parity/checks.json N12). A body whose air differs
        // gives its own value.
        public double SpeedOfSoundMs = 343.2;
        // The master volume 0 to 1 of the thunder, before the game's ambience slider.
        public float Volume = 1f;
        // Thunder playing at once, at most.
        public int MaxVoices = 4;
        // Beyond FadeStartM the thunder fades, at MaxAudibleM it is gone (a flash this far is seen and not heard).
        public double FadeStartM = 24000.0, MaxAudibleM = 30000.0;
    }

    // What one thunder does.
    internal struct ThunderPlan
    {
        public bool Play;
        // Seconds from now until it starts, in real time (the game-time delay divided by the warp rate).
        public double DelayS;
        // The loudness of the source 0 to 1 (the clip is rendered at its own peak 0.9 and played at this volume).
        public float Gain;
        // The cutoff in Hz of the low-pass that the distance puts on the rumble, and the share 0 to 1 of the initial crack.
        public float CutoffHz;
        public float CrackGain;
        // The length of the rumble in s.
        public float DurationS;
        // Why it does not play, for the log.
        public string Reason;
    }

    // The policy of the thunder: when a flash is heard, how late, how loud and how dark. Pure; the module calls it per flash and renders the
    // clip with ThunderSynth. Sound sources are procedural only (ThunderSynth); no recorded sound is used.
    // Structure after EVE's lightning (Atmosphere/RaymarchedClouds/Lightning/Lightning.cs, MIT): the delay as distance over the speed of sound
    // times a switch, a near and a far sound split by distance, at most four sounds at once.
    internal static class ThunderPolicy
    {
        internal const double ReferenceSpeedMs = 343.2;
        // Metres at which the loudness has fallen to half of the near value.
        internal const double GainHalfM = 1500.0;
        // The cutoff in Hz at the listener's side of the flash, and the distance in m over which the air takes the top off the thunder.
        internal const double NearCutoffHz = 6000.0, CutoffDistanceM = 1200.0, MinCutoffHz = 90.0;
        // The share of the crack: gone at a few kilometres.
        internal const double CrackDistanceM = 2500.0;

        internal static double DelayGameS(double distanceM, ThunderSettings s)
        {
            if (!s.RealisticDelay) return 0.0;
            double c = s.SpeedOfSoundMs > 1.0 ? s.SpeedOfSoundMs : ReferenceSpeedMs;
            return distanceM <= 0.0 ? 0.0 : distanceM / c;
        }

        // The length of the rumble: a close flash cracks and rolls for two seconds or so, a far one rolls for ten and more.
        internal static float DurationS(double distanceM)
        {
            double km = distanceM * 1e-3;
            double d = 1.5 + 6.0 * (1.0 - Math.Exp(-distanceM / 9000.0)) + 0.15 * km;
            return (float)Math.Min(d, 16.0);
        }

        internal static float CutoffHz(double distanceM)
        {
            double fc = NearCutoffHz / (1.0 + distanceM / CutoffDistanceM);
            return (float)Math.Max(fc, MinCutoffHz);
        }

        internal static float Loudness(double distanceM, ThunderSettings s)
        {
            double g = 1.0 / (1.0 + distanceM / GainHalfM);
            if (distanceM > s.FadeStartM) g *= Math.Max(0.0, (s.MaxAudibleM - distanceM) / Math.Max(s.MaxAudibleM - s.FadeStartM, 1.0));
            return (float)g;
        }

        // The plan for a flash at a distance. air: the listener is in an atmosphere; warpRate: the time-warp rate (below 5 where flashes
        // spawn); ambience: the game's ambience slider 0 to 1; activeVoices: thunder playing now.
        internal static ThunderPlan Plan(double distanceM, ListenerState listener, bool air, float warpRate, float ambience, int activeVoices, ThunderSettings s)
        {
            var plan = new ThunderPlan();
            if (!air) { plan.Reason = "no air"; return plan; }
            if (listener.MapView) { plan.Reason = "map view"; return plan; }
            if (!(distanceM < s.MaxAudibleM)) { plan.Reason = "too far"; return plan; }
            if (activeVoices >= s.MaxVoices) { plan.Reason = "voices"; return plan; }
            float gain = Loudness(distanceM, s) * Clamp01(s.Volume) * Clamp01(ambience);
            float cutoff = CutoffHz(distanceM);
            float crack = (float)Math.Exp(-distanceM / CrackDistanceM);
            if (listener.Cabin)
            {
                // Inside a vehicle the hull takes the top off and 6 dB of the level.
                gain *= 0.5f;
                cutoff = Math.Min(cutoff, 700f);
                crack *= 0.35f;
            }
            else if (listener.Eva)
            {
                gain *= 0.8f;
                cutoff = Math.Min(cutoff, 3500f);
            }
            if (gain < 0.004f) { plan.Reason = "inaudible"; return plan; }
            plan.Play = true;
            double warp = warpRate < 1f ? 1.0 : warpRate;
            plan.DelayS = DelayGameS(distanceM, s) / warp;
            plan.Gain = gain;
            plan.CutoffHz = cutoff;
            plan.CrackGain = crack;
            plan.DurationS = DurationS(distanceM);
            return plan;
        }

        private static float Clamp01(float x) { return x < 0f ? 0f : x > 1f ? 1f : x; }
    }
}
