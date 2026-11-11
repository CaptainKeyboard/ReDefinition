using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The level of a water-equivalent precipitation rate, by the ABSOLUTE band thresholds of the body (PrecipRules.BandRatesMmPerH, the
    // physical rates signed in the header of package B10) and not by a quantile of the simulated rates: the frequency of light and heavy
    // weather is then a result of the simulation that can be measured against the knobs, not an input that fits itself.
    //
    // The two knobs shift only the thresholds they name, and they are applied here and nowhere else:
    //   lightWeatherFrequency (1 is neutral, 0 to 2) divides the rates at which Flurries and Light start, so a value above 1 lets lighter
    //   rates count as those bands and a value below 1 needs more rain for them;
    //   heavyShare (1 is neutral, 0 to 2) divides the rates at which Heavy and Blizzard start in the same way.
    // The Moderate threshold stays, and the shifted thresholds never cross it: Flurries and Light stay below it, Heavy and Blizzard
    // above it, so the table stays ascending. The reference population of the knobs is the cold season at 45 to 70 N over at least three
    // simulated years (measured by E40's K1 run). The conversion itself is PrecipScale.LevelFromRate of Common.
    internal static class PrecipRateToLevel
    {
        // The smallest knob value that still counts; below it the thresholds are 20 times the base ones (nearly none of that weather).
        internal const float MinKnob = 0.05f;
        // How close to the Moderate threshold a shifted threshold may come, as a share of it.
        private const float Margin = 0.02f;

        // The thresholds with the knobs applied, written into target (five or six values like the input; the length is that of bands).
        internal static void Shift(float[] bands, float lightWeatherFrequency, float heavyShare, float[] target)
        {
            if (bands == null || bands.Length < 5) throw new ArgumentException("Five band rates are needed.", "bands");
            if (target == null || target.Length != bands.Length) throw new ArgumentException("The target must be as long as the bands.", "target");
            float light = 1f / Math.Max(float.IsNaN(lightWeatherFrequency) ? 1f : lightWeatherFrequency, MinKnob);
            float heavy = 1f / Math.Max(float.IsNaN(heavyShare) ? 1f : heavyShare, MinKnob);
            float moderate = bands[2];
            for (int i = 0; i < bands.Length; i++) target[i] = bands[i];
            // Flurries and Light stay below Moderate, strictly ascending.
            target[1] = Math.Min(bands[1] * light, moderate * (1f - Margin));
            target[0] = Math.Min(bands[0] * light, target[1] * (1f - Margin));
            // Heavy and Blizzard (and the optional top) stay above Moderate, strictly ascending.
            target[3] = Math.Max(bands[3] * heavy, moderate * (1f + Margin));
            target[4] = Math.Max(bands[4] * heavy, target[3] * (1f + Margin));
            if (bands.Length >= 6) target[5] = Math.Max(bands[5] * heavy, target[4] * (1f + Margin));
        }

        // The level of a rate in mm of water per hour for a body, with the knobs. The scratch array is the caller's, so a call allocates
        // nothing; it must be as long as the bands.
        internal static float Level(float rateMmPerH, float[] bands, float lightWeatherFrequency, float heavyShare, float[] scratch)
        {
            Shift(bands, lightWeatherFrequency, heavyShare, scratch);
            return PrecipScale.LevelFromRate(rateMmPerH, scratch);
        }

        // The same with the knobs read from the settings.
        internal static float Level(float rateMmPerH, PrecipRules rules, float[] scratch)
        {
            float[] bands = rules.BandRatesMmPerH;
            return Level(rateMmPerH, bands, AtmospheresSettings.LightWeatherFrequency, AtmospheresSettings.HeavyShare, scratch);
        }
    }
}
