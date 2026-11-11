using System;
using System.Globalization;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // What the effects do at one precipitation level: the values of the look table of plan 09 (3.3) and plan 05 (3.6).
    internal struct LookValues
    {
        // The visibility along the ground in metres.
        public float VisibilityM;
        // Flakes per second over all layers.
        public float FlakesPerS;
        // The factors on the flake size and the fall speed (1 is the size and speed of light snow).
        public float FlakeSizeFactor;
        public float FallFactor;
        // The mean wind in m/s before the gusts.
        public float WindMs;
        // The alpha of the veil toward the ground (the table value; DrawGate decides only whether our curtain is drawn).
        public float VeilAlpha;
        // How dark the base of the cloud is, 0 (grey deck) to 1 (darkest).
        public float CloudBaseDarkness;
        // The factor on the sun's direct light, 1 in clear air.
        public float SunDirect;
        // The factor on the ambient light, 1 in clear air.
        public float AmbientFactor;
        // The share of the overhead sky that is grey, 0 to 1.
        public float SkyGreyShare;
        // The level of the wind and precipitation sound, 0 to 1.
        public float SoundLevel;
        // The weight of this level in the accumulation of cover, relative to level 1.
        public float AccumulationWeight;
        // The scatter sigma of the medium for a lamp's beam in 1/m (lamp scatter).
        public float LampSigma;
        // The strength of the sun's shadows, 0 to 1.
        public float ShadowStrength;
    }

    // The look table of the precipitation scale: one set of curves over the level that drives the fog, the veil, the flakes, the light and
    // the sound, so every effect reads the same scale. The 1.0 column (60 m, veil 0.85, 2400 flakes per second) holds the values of the
    // accepted storm; every key point is a tuning value (Tuning.Get "look.<curve>.<level>"), not a gate. Pure, no allocation per call.
    internal static class PrecipitationLook
    {
        // The visibility with no precipitation, in metres: where the fog fades out.
        internal const float ClearVisibilityM = 20000f;

        // The curtain is drawn from this level up (our veil and volume), with a hysteresis of CurtainHysteresis below it.
        internal const float CurtainOn = 0.45f, CurtainOff = 0.40f;
        // The curtain gate S = smoothstep(CurtainS0, CurtainS1, level).
        internal const float CurtainS0 = 0.45f, CurtainS1 = 0.9f;
        // The most the level may change per second of game time, outside diagnostics and teleports.
        internal const float MaxLevelPerGameSecond = 1f / 90f;

        // The names of the curves; the key of a key point is "look.<name>.<level>".
        internal static readonly string[] CurveNames =
        {
            "visibility", "flakes", "flakeSize", "fall", "wind", "veil", "cloudDark", "sun", "ambient", "skyGrey", "sound", "accumulation",
            "lampSigma", "shadow"
        };

        // The starting values at level 0 and at the six key points (0.1, 0.25, 0.4, 0.6, 0.8, 1.0) of each curve.
        private static readonly float[][] Start =
        {
            new[] { ClearVisibilityM, 8000f, 3000f, 1200f, 500f, 150f, 60f },
            new[] { 0f, 30f, 150f, 450f, 1000f, 1800f, 2400f },
            new[] { 1f, 1.0f, 1.0f, 0.9f, 0.8f, 0.75f, 0.7f },
            new[] { 1f, 1.0f, 1.0f, 0.95f, 0.9f, 0.87f, 0.85f },
            new[] { 0f, 0.5f, 1.5f, 3f, 6f, 11f, 16f },
            new[] { 0f, 0f, 0f, 0.08f, 0.30f, 0.60f, 0.85f },
            new[] { 0f, 0f, 0.1f, 0.25f, 0.55f, 0.85f, 1f },
            new[] { 1f, 0.95f, 0.8f, 0.65f, 0.4f, 0.2f, 0.1f },
            new[] { 1f, 1f, 0.97f, 0.9f, 0.75f, 0.5f, 0.4f },
            new[] { 0f, 0f, 0.05f, 0.2f, 0.5f, 0.85f, 1f },
            new[] { 0f, 0.05f, 0.15f, 0.3f, 0.55f, 0.8f, 1f },
            new[] { 0f, 0.05f, 0.2f, 0.45f, 0.7f, 0.9f, 1f },
            // Plan 05 (3.6): 0.0004 at a shower, 0.0015 light, 0.008 heavy, 0.03 at the top, put on the six key points.
            new[] { 0f, 0.0002f, 0.001f, 0.003f, 0.008f, 0.018f, 0.03f },
            // The shadow strength follows the direct sun (plan 05, 3.6).
            new[] { 1f, 0.95f, 0.8f, 0.65f, 0.4f, 0.2f, 0.1f }
        };

        private static LookCurve[] curves;
        private static long builtFor = -1;
        private static readonly object Gate = new object();

        private const int Visibility = 0, Flakes = 1, FlakeSize = 2, Fall = 3, Wind = 4, Veil = 5, CloudDark = 6, Sun = 7, Ambient = 8, SkyGrey = 9,
            Sound = 10, Accumulation = 11, LampSigma = 12, Shadow = 13;

        // The look at a level (clamped to 0 to 1). The curves are rebuilt when the tuning table changed.
        internal static LookValues Evaluate(float level01)
        {
            LookCurve[] c = Curves();
            float p = Clamp01(level01);
            LookValues v;
            v.VisibilityM = c[Visibility].Evaluate(p);
            v.FlakesPerS = c[Flakes].Evaluate(p);
            v.FlakeSizeFactor = c[FlakeSize].Evaluate(p);
            v.FallFactor = c[Fall].Evaluate(p);
            v.WindMs = c[Wind].Evaluate(p);
            v.VeilAlpha = c[Veil].Evaluate(p);
            v.CloudBaseDarkness = c[CloudDark].Evaluate(p);
            v.SunDirect = c[Sun].Evaluate(p);
            v.AmbientFactor = c[Ambient].Evaluate(p);
            v.SkyGreyShare = c[SkyGrey].Evaluate(p);
            v.SoundLevel = c[Sound].Evaluate(p);
            v.AccumulationWeight = c[Accumulation].Evaluate(p);
            v.LampSigma = c[LampSigma].Evaluate(p);
            v.ShadowStrength = c[Shadow].Evaluate(p);
            return v;
        }

        // The curve with that name, for tests; null for an unknown name.
        internal static LookCurve Curve(string name)
        {
            int i = Array.IndexOf(CurveNames, name);
            return i < 0 ? null : Curves()[i];
        }

        // The starting value of a curve at the key point index (0 is level 0, 1 to 6 are the key levels).
        internal static float StartValue(string name, int keyIndex)
        {
            return Start[Array.IndexOf(CurveNames, name)][keyIndex];
        }

        // The curtain gate S: 0 below level 0.45, 1 from 0.9, smooth in between. Only the draw gate of our curtain and volume.
        internal static float CurtainShare(float level01)
        {
            float t = Clamp01((level01 - CurtainS0) / (CurtainS1 - CurtainS0));
            return t * t * (3f - 2f * t);
        }

        // The fog density of Unity's ExponentialSquared fog that leaves 5 % of the contrast at that visibility: sqrt(3) / visibility.
        internal static float FogDensity(float visibilityM)
        {
            return 1.7320508f / Math.Max(visibilityM, 1f);
        }

        private static LookCurve[] Curves()
        {
            long version = Tuning.Version;
            LookCurve[] built = curves;
            if (built != null && version == builtFor) return built;
            lock (Gate)
            {
                built = new LookCurve[CurveNames.Length];
                for (int i = 0; i < built.Length; i++)
                {
                    float[] row = Start[i];
                    var values = new float[LookCurve.KeyLevels.Length];
                    for (int k = 0; k < values.Length; k++)
                    {
                        string key = "look." + CurveNames[i] + "." + LookCurve.KeyLevels[k].ToString("0.##", CultureInfo.InvariantCulture);
                        values[k] = Tuning.Get(key, row[k + 1]);
                    }
                    built[i] = new LookCurve(Tuning.Get("look." + CurveNames[i] + ".0", row[0]), values);
                }
                curves = built;
                builtFor = version;
                return built;
            }
        }

        private static float Clamp01(float v)
        {
            return v > 0f ? (v < 1f ? v : 1f) : 0f;
        }
    }

    // The hysteresis of the curtain gate: on from CurtainOn, off below CurtainOff.
    internal struct CurtainGate
    {
        private bool on;

        // Whether our curtain and volume are drawn at that level.
        internal bool Update(float level01)
        {
            if (on) on = level01 >= PrecipitationLook.CurtainOff;
            else on = level01 >= PrecipitationLook.CurtainOn;
            return on;
        }
    }

    // The rate limit of the level: it follows its target by at most MaxLevelPerGameSecond.
    internal static class LevelLimiter
    {
        // The level one step on. A jump (a diagnostic force, a teleport, a body change) sets the target at once.
        internal static float Step(float previous, float target, double dtGameSeconds, bool jump)
        {
            if (jump || float.IsNaN(previous)) return target;
            float step = (float)Math.Min(Math.Max(dtGameSeconds, 0.0) * PrecipitationLook.MaxLevelPerGameSecond, 1.0);
            float d = target - previous;
            return d > step ? previous + step : d < -step ? previous - step : target;
        }
    }
}
