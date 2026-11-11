using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The three fractions of a mixed fall: snow, sleet and rain add up to 1.
    internal struct TypeMix
    {
        public float Snow, Sleet, Rain;

        internal static TypeMix Of(float snow, float sleet, float rain)
        {
            float sum = snow + sleet + rain;
            if (!(sum > 0f)) return new TypeMix();
            return new TypeMix { Snow = snow / sum, Sleet = sleet / sum, Rain = rain / sum };
        }

        // The type with the largest fraction; a tie goes to sleet, the type in between.
        internal PrecipType Dominant
        {
            get
            {
                if (Sleet >= Snow && Sleet >= Rain) return Sleet > 0f ? PrecipType.Sleet : PrecipType.None;
                return Snow > Rain ? PrecipType.Snow : PrecipType.Rain;
            }
        }
    }

    // The type of the precipitation from the temperature of the weather field (there is no second cold): a wet-bulb estimate, a logistic
    // share of snow around the body's centre temperature, rain as the rest and sleet where the two overlap. Pure.
    //   Tw = T - 0.7 K in light and T - 0.3 K in heavy precipitation (saturated column; the offset runs between them over the levels
    //   0.2 to 0.6), fs = 1 / (1 + exp((Tw - centre) / width)), rain = 1 - fs, sleet = 4 fs (1 - fs); the three are normalised and the
    //   type is the largest. The centre is 1.0 C and the width 0.9 C unless the body's PrecipTypeRule says otherwise. Freezing rain is a
    //   rare flag in the transition zone, off by default. The other types of a body (dust, sand, ash, methane) come from its rule.
    internal static class PrecipTypeModel
    {
        internal const float DefaultCentreC = 1.0f, DefaultWidthC = 0.9f;
        internal const float WetBulbOffsetLightK = 0.7f, WetBulbOffsetHeavyK = 0.3f;
        // The chance of freezing rain per ten minutes of game time inside the transition zone, when the flag is on.
        internal const float FreezingRainChance = 0.08f;
        // The most a fraction may change per second of game time: 0.1 per game minute.
        internal const float MaxFractionPerGameSecond = 0.1f / 60f;

        // The wet-bulb offset in kelvin at a level.
        internal static float WetBulbOffsetK(float level01)
        {
            float t = (level01 - 0.2f) / 0.4f;
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            t = t * t * (3f - 2f * t);
            return WetBulbOffsetLightK + (WetBulbOffsetHeavyK - WetBulbOffsetLightK) * t;
        }

        // The share of snow in the fall.
        internal static float SnowShare(float temperatureK, float level01, float centreC, float widthC)
        {
            float twC = temperatureK - 273.15f - WetBulbOffsetK(level01);
            double x = (twC - centreC) / Math.Max(widthC, 0.05f);
            return (float)(1.0 / (1.0 + Math.Exp(x)));
        }

        // The mix at a temperature (kelvin, from the field) and a level.
        internal static TypeMix Mix(float temperatureK, float level01, float centreC, float widthC)
        {
            if (float.IsNaN(temperatureK)) return new TypeMix();
            float fs = SnowShare(temperatureK, level01, centreC, widthC);
            float fr = 1f - fs;
            return TypeMix.Of(fs, 4f * fs * fr, fr);
        }

        // The centre and the width of the snow-to-rain change of a body: its snow rule if it has one, else its rain rule, else the
        // defaults.
        internal static void Parameters(PrecipRules rules, out float centreC, out float widthC)
        {
            centreC = DefaultCentreC;
            widthC = DefaultWidthC;
            if (rules == null) return;
            for (int i = 0; i < rules.Types.Count; i++)
            {
                PrecipTypeRule r = rules.Types[i];
                if (r.Kind == PrecipType.Snow || r.Kind == PrecipType.Rain)
                {
                    centreC = (float)r.CentreC;
                    widthC = (float)r.WidthC;
                    return;
                }
            }
        }

        // The kind a body falls instead of the temperature-driven types (dust, sand, ash, methane), or None where the body uses snow and
        // rain.
        internal static PrecipType BodyKind(PrecipRules rules)
        {
            if (rules == null) return PrecipType.None;
            for (int i = 0; i < rules.Types.Count; i++)
            {
                PrecipType k = rules.Types[i].Kind;
                if (k == PrecipType.Dust || k == PrecipType.Sand || k == PrecipType.Ash || k == PrecipType.Methane) return k;
            }
            return PrecipType.None;
        }

        // Whether the freezing-rain flag is set for that time and seed: the same answer for the same ten minutes of game time.
        internal static bool FreezingRainFlag(double ut, int seed)
        {
            long slot = (long)Math.Floor(ut / 600.0);
            uint h = unchecked((uint)(slot * 2654435761L) ^ (uint)seed * 2246822519u);
            h ^= h >> 15;
            h = unchecked(h * 2246822519u);
            h ^= h >> 13;
            return (h & 0xFFFFFF) / (float)0x1000000 < FreezingRainChance;
        }

        // The mix one step on: snow and rain move towards the target by at most half of MaxFractionPerGameSecond each, and sleet is the
        // rest, so no fraction changes by more than MaxFractionPerGameSecond and the sum stays 1 (snow and rain each stay between the
        // old and the new target, so their sum never passes 1). A jump sets the target at once.
        internal static TypeMix Limit(TypeMix previous, TypeMix target, double dtGameSeconds, bool jump)
        {
            if (jump) return target;
            float step = (float)(Math.Max(dtGameSeconds, 0.0) * MaxFractionPerGameSecond) * 0.5f;
            float snow = Toward(previous.Snow, target.Snow, step);
            float rain = Toward(previous.Rain, target.Rain, step);
            return new TypeMix { Snow = snow, Rain = rain, Sleet = Math.Max(0f, 1f - snow - rain) };
        }

        private static float Toward(float from, float to, float step)
        {
            float d = to - from;
            return d > step ? from + step : d < -step ? from - step : to;
        }
    }

    // The pulse of a shower: a smooth, deterministic noise of the game time with a period of about 2 to 6 minutes. It multiplies the
    // DISPLAYED level of flurries by 0.35 + 0.65 x pulse, and nothing else: the forecast and the cover read the level without it.
    internal static class ShowerPulse
    {
        // The spacing of the noise lattice in seconds of game time (the pulse has a period of two to three lattice steps).
        internal const double LatticeSeconds = 120.0;

        // The pulse, 0 to 1, at a game time.
        internal static float Pulse(double ut, int seed)
        {
            double p = ut / LatticeSeconds;
            double f = Math.Floor(p);
            float t = (float)(p - f);
            t = t * t * (3f - 2f * t);
            float a = Lattice((long)f, seed), b = Lattice((long)f + 1, seed);
            return a + (b - a) * t;
        }

        // The factor on the displayed level: 1 above level 0.25, 0.35 + 0.65 x pulse in flurries, blended over 0.15 to 0.25.
        internal static float Factor(double ut, int seed, float level01)
        {
            float w = 1f - (level01 - 0.15f) / 0.10f;
            w = w < 0f ? 0f : w > 1f ? 1f : w;
            w = w * w * (3f - 2f * w);
            float pulse = 0.35f + 0.65f * Pulse(ut, seed);
            return 1f + (pulse - 1f) * w;
        }

        private static float Lattice(long i, int seed)
        {
            unchecked
            {
                uint h = (uint)(i * 374761393L) + (uint)seed * 668265263u;
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }
    }
}
