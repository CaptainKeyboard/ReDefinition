using System;

namespace ReDefinition.Atmospheres
{
    // What the wetness does to the picture, as the numbers the G-buffer overlay applies (package T08, item 1).
    internal struct WetLook
    {
        // The factor on the diffuse colour of ground that is not in a puddle (1 dry).
        public float DiffuseFactor;
        // Smoothness added to the G-buffer's smoothness (0 dry).
        public float SmoothAdd;
        // The share of the ground the puddle field covers (0 dry); a pixel is in a puddle where the depression noise is under it.
        public float PuddleCover;
        // The diffuse factor and the smoothness inside a puddle.
        public float PuddleDiffuse, PuddleSmooth;
        // The rain rings on the puddles, 0 to 1.
        public float Ripples;
        // The share of the water that is frozen (glaze): no ripples, a sheet instead of a mirror.
        public float Frozen;
        // The wetness 0 to 1 the look was made from (the pass skips below the tuning's SkipBelow).
        public float Wetness;
    }

    // Maps the water on the ground to its look. Pure. Real-world behaviour: wet bare ground reflects 0.5 to 0.8 of what it reflects dry
    // and is glossier; standing water is a mirror with a flat surface; frozen water darkens little and stays glossy; snow cover hides
    // the wet ground (wet snow darkens a little and is not a puddle field).
    internal static class WetnessLookMath
    {
        // saturation: the surface layer's wetness 0 to 1; standingMm: water standing; rainLevel: the precipitation level now (rings
        // only while it rains); temperatureK and freezeK decide the frozen share (a ramp of 3 K below freezing); snow01: the share of the
        // ground under snow cover 0 to 1 (the cover system's depth over its full depth).
        internal static WetLook Eval(float saturation, float standingMm, float rainLevel, float temperatureK, float freezeK, float snow01, ref WetnessTuning t)
        {
            float wet = (float)Math.Pow(Clamp01(saturation), t.WetPow);
            float frozen = float.IsNaN(temperatureK) ? 0f : 1f - Smooth(freezeK - 3f, freezeK, temperatureK);
            float snowKeep = 1f - t.SnowDamp * Clamp01(snow01);
            float darkKeep = (1f - frozen) + frozen * t.FrozenDarken;
            var look = new WetLook();
            look.Wetness = wet;
            look.Frozen = frozen;
            look.DiffuseFactor = 1f - (1f - t.WetDiffuse) * wet * darkKeep * snowKeep;
            look.SmoothAdd = t.WetSmooth * wet * (1f - 0.6f * Clamp01(snow01));
            float standing = Clamp01(standingMm / Math.Max(t.PuddleFullMm, 1e-3f));
            look.PuddleCover = t.PuddleMax * (float)Math.Pow(standing, 0.8) * snowKeep;
            // A puddle is darker than wet ground (deeper water, less of the ground's colour) and glassy; frozen it is a sheet of ice,
            // still glossy but less dark.
            look.PuddleDiffuse = 1f - (1f - t.PuddleDarken) * (1f - 0.5f * frozen);
            look.PuddleSmooth = t.PuddleSmooth * (1f - 0.1f * frozen);
            look.Ripples = t.RippleScale * Clamp01(rainLevel * 3f) * (1f - frozen) * (look.PuddleCover > 0.001f ? 1f : 0f);
            return look;
        }

        private static float Smooth(float a, float b, float x)
        {
            float v = Clamp01((x - a) / (b - a));
            return v * v * (3f - 2f * v);
        }

        internal static float Clamp01(float v)
        {
            return float.IsNaN(v) ? 0f : Math.Min(Math.Max(v, 0f), 1f);
        }
    }

    // Advances the wetness of a site through an interval of game time by reading the weather of the interval from a source, so that a
    // warp jump, a scene change or a first frame arrives at the state the same hours in frames would have (item 1; WSR's catch-up in EVE
    // has the same purpose).
    internal static class WetnessCatchUp
    {
        // The weather at a UT. The source is asked once per sub-interval, at its middle.
        internal delegate WetInput Source(double ut);

        // Advances kind's state from fromUt to toUt; sub-intervals of at most stepS seconds (each sampled at its middle).
        internal static void Advance(Source source, ref WetState s, WetClass kind, ref WetnessTuning t, double fromUt, double toUt, double stepS)
        {
            double span = toUt - fromUt;
            if (!(span > 0.0) || source == null) return;
            int n = Math.Max(1, (int)Math.Ceiling(span / Math.Max(stepS, 1.0)));
            double h = span / n;
            for (int i = 0; i < n; i++)
            {
                WetInput w = source(fromUt + (i + 0.5) * h);
                WetnessModel.Step(ref s, ref w, kind, ref t, (float)h);
            }
        }

        // Replays the lookback before toUt on a dry state, or continues from a known state (lastUt finite and within the lookback).
        // Returns the state at toUt.
        internal static WetState Resume(Source source, WetState known, double lastUt, WetClass kind, ref WetnessTuning t, double toUt)
        {
            double lookbackS = t.LookbackH * 3600.0;
            WetState s = known;
            double from = lastUt;
            if (double.IsNaN(lastUt) || toUt - lastUt > lookbackS || toUt < lastUt)
            {
                s = default(WetState);
                from = toUt - lookbackS;
            }
            Advance(source, ref s, kind, ref t, from, toUt, t.HistoryStepH * 3600.0);
            return s;
        }
    }
}
