using System;

namespace ReDefinition.Core
{
    // What falls from the sky. The type follows the temperature (snow, sleet, rain) or the body (dust, sand, ash, methane); the
    // field also gives the three fractions of a mixed fall (WeatherSample.SnowFraction, SleetFraction, RainFraction).
    public enum PrecipType
    {
        // Nothing falls.
        None = 0,
        Snow = 1,
        Rain = 2,
        Sleet = 3,
        FreezingRain = 4,
        Dust = 5,
        Sand = 6,
        Ash = 7,
        Methane = 8
    }

    // The named steps of the level scale from Flurries to Blizzard. One pure function (PrecipScale.BandOf) derives it from the
    // level, so the interface, the HUD, the forecast and the pictures agree.
    public enum PrecipBand
    {
        // Level below 0.04.
        None = 0,
        // 0.04 to 0.20: showers, intermittent.
        Flurries = 1,
        // 0.20 to 0.40.
        Light = 2,
        // 0.40 to 0.60.
        Moderate = 3,
        // 0.60 to 0.80; also 0.80 to 1.0 where the sustained wind stays below the blizzard limit.
        Heavy = 4,
        // 0.80 to 1.0 with a sustained wind of at least PrecipScale.BlizzardWindMs.
        Blizzard = 5
    }

    // The level scale of precipitation: Level01 from 0 to 1 and the bands it falls into, plus the pure conversion from a
    // water-equivalent rate to a level. Common holds it so that the field, the forecast, the renderer and the HUD use one table.
    public static class PrecipScale
    {
        // The level at which Flurries start.
        public const float FlurriesFrom = 0.04f;
        // The level at which Light starts.
        public const float LightFrom = 0.20f;
        // The level at which Moderate starts.
        public const float ModerateFrom = 0.40f;
        // The level at which Heavy starts.
        public const float HeavyFrom = 0.60f;
        // The level at which Blizzard starts, where the wind condition holds.
        public const float BlizzardFrom = 0.80f;
        // The sustained wind in m/s (35 mph) a Blizzard needs; below it the top band is named Heavy.
        public const float BlizzardWindMs = 15.6f;

        private static readonly float[] Starts = { FlurriesFrom, LightFrom, ModerateFrom, HeavyFrom, BlizzardFrom };

        // The band of a level. Blizzard needs both the level and a sustained wind of at least BlizzardWindMs; without the wind the
        // band is Heavy (the look still follows the level). NaN is None.
        public static PrecipBand BandOf(float level01, float sustainedWindMs)
        {
            if (!(level01 >= FlurriesFrom)) return PrecipBand.None;
            if (level01 < LightFrom) return PrecipBand.Flurries;
            if (level01 < ModerateFrom) return PrecipBand.Light;
            if (level01 < HeavyFrom) return PrecipBand.Moderate;
            if (level01 < BlizzardFrom) return PrecipBand.Heavy;
            return sustainedWindMs >= BlizzardWindMs ? PrecipBand.Blizzard : PrecipBand.Heavy;
        }

        // The level at which a band starts (0 for None).
        public static float StartOf(PrecipBand band)
        {
            int index = (int)band - 1;
            return index < 0 || index >= Starts.Length ? 0f : Starts[index];
        }

        // The plain English name of a band; the names of other types and of a body come from its configuration.
        public static string DefaultName(PrecipBand band)
        {
            switch (band)
            {
                case PrecipBand.Flurries: return "Flurries";
                case PrecipBand.Light: return "Light";
                case PrecipBand.Moderate: return "Moderate";
                case PrecipBand.Heavy: return "Heavy";
                case PrecipBand.Blizzard: return "Blizzard";
                default: return "None";
            }
        }

        // The level of a water-equivalent rate in mm per hour. bandRatesMmPerH holds the rates at which the bands start, the PRECIP
        // node of the body's climate: five ascending values for Flurries, Light, Moderate, Heavy and Blizzard, optionally a sixth for
        // level 1. Between them the level is linear; below the first it runs from 0 to FlurriesFrom; above the last it rises to 1
        // over a span equal to the last band's width (with five values) and stays at 1. The result is monotone in the rate.
        public static float LevelFromRate(float rateMmPerH, float[] bandRatesMmPerH)
        {
            if (bandRatesMmPerH == null || bandRatesMmPerH.Length < 5) throw new ArgumentException("Five band rates are needed.", "bandRatesMmPerH");
            if (!(rateMmPerH > 0f)) return 0f;
            float[] rates = bandRatesMmPerH;
            if (rateMmPerH < rates[0]) return FlurriesFrom * (rateMmPerH / rates[0]);
            for (int i = 0; i < 4; i++)
            {
                if (rateMmPerH < rates[i + 1]) return Lerp(Starts[i], Starts[i + 1], rates[i], rates[i + 1], rateMmPerH);
            }
            float top = rates.Length >= 6 ? rates[5] : rates[4] + (rates[4] - rates[3]);
            if (rateMmPerH >= top) return 1f;
            return Lerp(BlizzardFrom, 1f, rates[4], top, rateMmPerH);
        }

        private static float Lerp(float levelA, float levelB, float rateA, float rateB, float rate)
        {
            float span = rateB - rateA;
            if (!(span > 0f)) return levelB;
            return levelA + (levelB - levelA) * ((rate - rateA) / span);
        }
    }
}
