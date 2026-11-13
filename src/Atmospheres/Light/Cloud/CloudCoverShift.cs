using System;

namespace ReDefinition.Atmospheres
{
    // The runtime shift of the layers' coverage threshold (K-T2d: the rain deck's coverage 0.5 closes the whole sky at a map value of 0.47 to 0.64; the owner has not decided
    // between 0.5 and about 0.42, so both are measured as a switchable variant). The one place where the threshold enters: CloudLayerDef.EffectiveCoverage adds the shift, and
    // every reader of the threshold (the layer pass's frame values, the cloud shadows' frame values, the CPU density LayerDensity and HeightDensity, CloudCoverModel) reads that,
    // never CloudLayerDef.Coverage. Default 0 = the configured coverage, unchanged.
    //   cloud.coverageShift      added to the coverage of every layer, -1 to 1
    //   cloud.coverageShiftRain  added to the layers of the group "weather" (the rain deck, or the snow deck in its place) on top of the first
    // The result is clamped to 0 to 1, the range of the coverage.
    internal static class CloudCoverShift
    {
        internal const string TuneShift = "cloud.coverageShift", TuneShiftRain = "cloud.coverageShiftRain";
        // The group the layer-selective key acts on (Clouds-<body>.cfg: the rain and the snow deck share it).
        internal const string RainGroup = "weather";

        private static long seenVersion = -1;
        private static float all, rain;
        private static long stamp;

        // Rises each time the shift of any layer changed, or any other look knob (CloudLookTuning: sigma, softness, noise, per-layer shift, fade): an uploaded copy of the
        // frame values is stale when it differs.
        internal static long Stamp
        {
            get
            {
                Refresh();
                return stamp + CloudLookTuning.Stamp;
            }
        }

        // The shifts as the tuning table holds them (the table is swapped at the frame boundary, so one frame reads one value).
        internal static void Current(out float shiftAll, out float shiftRain)
        {
            Refresh();
            shiftAll = all;
            shiftRain = rain;
        }

        private static void Refresh()
        {
            long version = Tuning.Version;
            if (version == seenVersion) return;
            seenVersion = version;
            float a = Clean(Tuning.Get(TuneShift, 0f)), r = Clean(Tuning.Get(TuneShiftRain, 0f));
            if (a == all && r == rain) return;
            all = a;
            rain = r;
            stamp++;
        }

        private static float Clean(float v)
        {
            return float.IsNaN(v) || float.IsInfinity(v) ? 0f : Math.Max(-1f, Math.Min(1f, v));
        }

        // The coverage threshold of a layer with its shifts applied.
        internal static float Apply(float coverage, string group)
        {
            float a, r;
            Current(out a, out r);
            return Apply(coverage, group, a, r);
        }

        // Pure form: a shift of 0 returns the coverage as it is (no clamp), so that the default changes nothing.
        internal static float Apply(float coverage, string group, float shiftAll, float shiftRain)
        {
            float shift = shiftAll + (string.Equals(group, RainGroup, StringComparison.Ordinal) ? shiftRain : 0f);
            return shift == 0f ? coverage : Math.Max(0f, Math.Min(1f, coverage + shift));
        }
    }
}
