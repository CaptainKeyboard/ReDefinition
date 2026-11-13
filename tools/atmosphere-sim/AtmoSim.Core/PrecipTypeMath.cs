using System;

namespace AtmoSim.Core
{
    // The type of a fall (snow, sleet, rain) from the temperature: ONE definition for the generator (and so for the forecast, the
    // vehicle cover, the wet surfaces and the ground effects, which read the generator's fractions) and for the frame driver that draws
    // the sky (ReDefinition.Atmospheres PrecipTypeModel, which calls this). A wet-bulb estimate, a logistic share of snow around the body's
    // centre temperature, rain as the rest and sleet where the two overlap:
    //   Tw = T - 273.15 K - offset(level)   (offset 0.7 K in light fall, 0.3 K in heavy fall, smooth between the levels 0.2 and 0.6)
    //   snow = 1 / (1 + exp((Tw - centre) / width)),  rain = 1 - snow,  sleet = 4 snow rain.
    // The three are not normalised here; the sum is 1 + 4 snow rain.
    public static class PrecipTypeMath
    {
        public const double DefaultCentreC = 1.0, DefaultWidthC = 0.9;
        public const double WetBulbOffsetLightK = 0.7, WetBulbOffsetHeavyK = 0.3;
        public const double OffsetFromLevel = 0.2, OffsetSpanLevel = 0.4;
        public const double MinWidthC = 0.05;

        // The level the generator assumes where no band table is given (the offline tool): the middle of the offset's range.
        public const double ToolLevel = 0.4;

        // The wet-bulb offset in kelvin at a level of precipitation (0 to 1).
        public static double WetBulbOffsetK(double level01)
        {
            double t = (level01 - OffsetFromLevel) / OffsetSpanLevel;
            t = t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
            t = t * t * (3.0 - 2.0 * t);
            return WetBulbOffsetLightK + (WetBulbOffsetHeavyK - WetBulbOffsetLightK) * t;
        }

        // The share of snow in the fall. The centre is in degrees C of the wet-bulb temperature (a body whose water freezes elsewhere
        // states it in its PRECIP rule: BodyGuess puts the water's freezing point into it).
        public static double SnowShare(double temperatureK, double level01, double centreC, double widthC)
        {
            double twC = temperatureK - 273.15 - WetBulbOffsetK(level01);
            double x = (twC - centreC) / Math.Max(widthC, MinWidthC);
            return 1.0 / (1.0 + DMath.Exp(x));
        }

        // The three fractions (not normalised).
        public static void Fractions(double temperatureK, double level01, double centreC, double widthC,
            out double snow, out double sleet, out double rain)
        {
            snow = SnowShare(temperatureK, level01, centreC, widthC);
            rain = 1.0 - snow;
            sleet = 4.0 * snow * rain;
        }

        // The fixed model of the rounds before the type was unified (BodyParams.PrecipTypeLegacy; the historical profiles Round1 to Round3 and
        // Signed use it so that their recorded hashes still reproduce): the wet-bulb offset 1.2 K at any level, the body's snow freezing point,
        // the centre 1.0 C and the width 0.9 C.
        public static void LegacyFractions(double temperatureK, double snowFreezeK, out double snow, out double sleet, out double rain)
        {
            double twC = temperatureK - snowFreezeK - 1.2;
            snow = 1.0 / (1.0 + DMath.Exp((twC - 1.0) / 0.9));
            rain = 1.0 - snow;
            sleet = 4.0 * snow * rain;
        }

        // The kind with the largest fraction; a tie goes to sleet, the type in between; None where all three are zero.
        public static PrecipKind Dominant(double snow, double sleet, double rain)
        {
            if (sleet >= snow && sleet >= rain) return sleet > 0.0 ? PrecipKind.Sleet : PrecipKind.None;
            return snow > rain ? PrecipKind.Snow : PrecipKind.Rain;
        }
    }
}
