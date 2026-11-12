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

        // The depression field of the shader (three octaves of value noise with weights 0.55, 0.30 and 0.15) is close to a normal
        // distribution around 0.5; each octave has a variance of (1/12) times the mean squared weight of the interpolation, about 0.0375.
        // [VERIFY: the player row puddle.share_vs_cover measures the real share against the asked one.]
        internal const float FieldMean = 0.5f;
        internal const float FieldSigma = 0.125f;

        // The threshold of the depression field under which a pixel is in a puddle, so that a share `cover` of level ground is puddle.
        // 0 for no puddles.
        internal static float FieldThreshold(float cover)
        {
            if (!(cover > 0.001f)) return 0f;
            if (cover >= 0.999f) return 1f;
            return FieldMean + FieldSigma * InverseNormal(cover);
        }

        // The inverse of the standard normal distribution (the rational approximation of P. J. Acklam; relative error below 1.2e-9).
        internal static float InverseNormal(float p)
        {
            double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
            double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
            double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
            double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
            double pl = 0.02425, q, r, x;
            if (p < pl)
            {
                q = Math.Sqrt(-2.0 * Math.Log(p));
                x = (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0);
            }
            else if (p <= 1.0 - pl)
            {
                q = p - 0.5;
                r = q * q;
                x = (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1.0);
            }
            else
            {
                q = Math.Sqrt(-2.0 * Math.Log(1.0 - p));
                x = -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0);
            }
            return (float)x;
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
