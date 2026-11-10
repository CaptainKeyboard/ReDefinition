using System;

namespace ReDefinition.Seasons
{
    // What a storm intensity (0 to 1) looks like: visibility, fog density, veil, flakes. Pure functions, so that the numbers are tested;
    // StormEffect applies them.
    internal static class StormLook
    {
        // The visibility in clear air, in metres: where the fog fades out entirely.
        internal const double ClearVisibilityM = 20000;

        // The visibility at full intensity, in metres: the setting's default and its range.
        internal const float MinVisibilityDefault = 60f;
        internal const float MinVisibilityLow = 20f, MinVisibilityHigh = 500f;

        // The exponent that bends the visibility curve: with the default minimum it gives about 250 m at half intensity.
        internal const double VisibilityCurve = 0.4;

        // The remaining contrast at the visibility distance (5 %) as the argument of the exponential: exp(-3) = 0.05.
        internal const double ContrastExponent = 3;

        // Veil: the alpha at full intensity (the setting's default and range) and how it grows with the intensity.
        internal const float VeilDefault = 0.85f, VeilMax = 0.95f;
        internal const double VeilCurve = 1.3;

        // Snowfall: flakes emitted per second at full intensity, and the most the system holds.
        internal const float FlakesPerSecond = 650f;
        internal const int MaxFlakes = 3200;

        // The storm fades with height: full below FadeStartM, gone from FadeEndM above sea level.
        internal const double FadeStartM = 4000, FadeEndM = 10000;

        // Visibility in metres at an intensity: ClearVisibilityM at 0, minVisibility at 1, in between along a curve that is flat
        // near 0 in the exponent.
        internal static double VisibilityMetres(double intensity, double minVisibility)
        {
            double i = Clamp(intensity, 0, 1);
            double min = Clamp(minVisibility, MinVisibilityLow, MinVisibilityHigh);
            if (i <= 0) return ClearVisibilityM;
            double blend = Math.Pow(i, VisibilityCurve);
            return ClearVisibilityM * Math.Pow(min / ClearVisibilityM, blend);
        }

        // The density of Unity's ExponentialSquared fog that leaves 5 % of the contrast at that distance:
        // exp(-(density * d)^2) = exp(-3).
        internal static double FogDensity(double visibilityMetres)
        {
            return Math.Sqrt(ContrastExponent) / Math.Max(visibilityMetres, 1);
        }

        // The share of the light that reaches the camera from a distance, 0 to 1, for that density.
        internal static double Transmittance(double density, double distance)
        {
            double x = density * distance;
            return Math.Exp(-x * x);
        }

        // The alpha of the veil in front of the camera at an intensity.
        internal static double VeilAlpha(double intensity, double veilAtFull)
        {
            return Clamp(veilAtFull, 0, VeilMax) * Math.Pow(Clamp(intensity, 0, 1), VeilCurve);
        }

        // Flakes per second at an intensity; none below 0.02.
        internal static double FlakeRate(double intensity)
        {
            double i = Clamp(intensity, 0, 1);
            return i < 0.02 ? 0 : FlakesPerSecond * i;
        }

        // The factor by which the storm reaches a height above sea level: 1 up to FadeStartM, 0 from FadeEndM.
        internal static double HeightFactor(double altitudeMetres)
        {
            if (double.IsNaN(altitudeMetres)) return 0;
            double t = Clamp((FadeEndM - altitudeMetres) / (FadeEndM - FadeStartM), 0, 1);
            return t * t * (3 - 2 * t);
        }

        // The brightness of the fog from the sun's elevation: sinElevation is the sine of the sun above the local horizon.
        // Grey-white by day, dark grey by night.
        internal static double FogBrightness(double sinElevation)
        {
            double t = Clamp((sinElevation + 0.1) / 0.4, 0, 1);
            double smooth = t * t * (3 - 2 * t);
            return 0.12 + 0.88 * smooth;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value)) return min;
            return value < min ? min : value > max ? max : value;
        }
    }
}
