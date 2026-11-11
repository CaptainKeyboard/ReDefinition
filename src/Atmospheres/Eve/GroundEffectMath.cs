using System;

namespace ReDefinition.Atmospheres
{
    // How the field's precipitation type scales EVE's ground effects at the camera, pure (plan 09 section 3.6).
    // EVE draws rain, snow, droplets, wet surfaces, lightning and the ambient sound from the coverage it reads at the camera and the
    // values of the cloud type there. The field says what falls: the three fractions of snow, sleet and rain (they add up to 1) and the
    // level. A sleet fall counts half as rain and half as snow.
    internal static class GroundEffectMath
    {
        // The level from which lightning may strike: below it no strike at all, snow thunder never.
        internal const float LightningFromLevel = 0.5f;

        // The factors EVE's values are multiplied by, each 0 to 1.
        internal struct Factors
        {
            // The rain particle field's coverage and the snow particle field's.
            public float RainField, SnowField;
            // The droplets on windows and the wet surfaces.
            public float Droplets, Wet;
            // The lightning frequency.
            public float Lightning;
            // The rain sound and the snow (wind) sound.
            public float RainSound, SnowSound;
        }

        // The factors for a mix of fractions at a level. With no precipitation (level <= 0) all are 0.
        internal static Factors From(float snow, float sleet, float rain, float level01)
        {
            var f = new Factors();
            if (!(level01 > 0f)) return f;
            float sum = snow + sleet + rain;
            if (!(sum > 0f)) return f;
            snow /= sum;
            sleet /= sum;
            rain /= sum;
            f.RainField = Clamp01(rain + 0.5f * sleet);
            f.SnowField = Clamp01(snow + 0.5f * sleet);
            f.Droplets = Clamp01(rain + 0.5f * sleet);
            f.Wet = Clamp01(rain + 0.3f * sleet);
            f.Lightning = level01 >= LightningFromLevel ? Clamp01(rain) : 0f;
            f.RainSound = f.RainField;
            f.SnowSound = f.SnowField;
            return f;
        }

        // EVE's own weather mapped to the precipitation scale (cloudSource Both, useEveWeather): Peve = 0.55 x coverage^0.8, up to
        // 0.8 for a layer of the thunder kind; the cap without a reading of the type is 0.6.
        internal static float EveLevel(float coverage01, bool thunderKind, bool typeKnown)
        {
            float c = Clamp01(coverage01);
            float shaped = (float)Math.Pow(c, 0.8);
            if (thunderKind) return 0.8f * shaped;
            return Math.Min(0.55f * shaped, typeKnown ? 0.55f : 0.6f);
        }

        // The union of two levels (independent sources of the same sky): 1 - (1 - a)(1 - b).
        internal static float Union(float a, float b)
        {
            a = Clamp01(a);
            b = Clamp01(b);
            return 1f - (1f - a) * (1f - b);
        }

        // A cloud type value scaled by a factor and put back: the written value is original x factor (never a bare 0), the restore
        // is the original.
        internal static float Written(float original, float factor)
        {
            return original * Clamp01(factor);
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : v > 1f ? 1f : v;
        }
    }
}
