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

        // Snowfall: flakes emitted per second at full intensity over both layers, and the most each layer holds.
        internal const float FlakesPerSecond = 3000f;
        internal const int MaxFlakesPerLayer = 5000;

        // The rate grows with the intensity to this power: 0.35 gives 0.43 of the full rate.
        internal const double FlakeRateCurve = 0.8;

        // Fall speed in m/s at intensity 0 and 1, and the horizontal wind at 1.
        internal const float FallSlow = 3.5f, FallFast = 6f;
        internal const float WindAtFull = 9f;

        // The streak of a flake: its length is the speed times this scale. Small at a light fall, long in a storm.
        internal const float StreakScaleLight = 0.015f, StreakScaleFull = 0.09f;

        // The size limits a flake keeps on the screen, as a part of the viewport height: far flakes stay visible, near ones do not
        // fill it.
        internal const float MinScreenSize = 0.0018f, MaxScreenSize = 0.02f;

        // Storm sky: the default of the darkening setting, the storm grey by day, and the sine of the elevation from which the sky is
        // fully that grey.
        internal const float SkyDarkeningDefault = 0.6f;
        internal static readonly float[] SkyGreyDay = { 0.34f, 0.36f, 0.40f };
        internal const double SkyFullSinElevation = 0.4;

        // The most the ambient light is lowered at full intensity and full darkening.
        internal const double AmbientDropAtFull = 0.6;

        // A storm layer of flakes: a box around the camera, the share of the rate and the size range at full intensity, in metres.
        internal struct FlakeLayer
        {
            internal readonly float BoxWidth, BoxHeight, SizeMin, SizeMax, RateShare;

            internal FlakeLayer(float boxWidth, float boxHeight, float sizeMin, float sizeMax, float rateShare)
            {
                BoxWidth = boxWidth;
                BoxHeight = boxHeight;
                SizeMin = sizeMin;
                SizeMax = sizeMax;
                RateShare = rateShare;
            }
        }

        // The near layer is dense and close to the camera, the far one wide with larger flakes so that they stay visible at distance.
        internal static readonly FlakeLayer NearLayer = new FlakeLayer(9f, 8f, 0.05f, 0.11f, 0.45f);
        internal static readonly FlakeLayer FarLayer = new FlakeLayer(32f, 14f, 0.07f, 0.16f, 0.55f);

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

        // Flakes per second over both layers at an intensity; none below 0.02.
        internal static double FlakeRate(double intensity)
        {
            double i = Clamp(intensity, 0, 1);
            return i < 0.02 ? 0 : FlakesPerSecond * Math.Pow(i, FlakeRateCurve);
        }

        // Flakes per second of one layer.
        internal static double FlakeRate(double intensity, FlakeLayer layer)
        {
            return FlakeRate(intensity) * layer.RateShare;
        }

        // The factor on a layer's flake sizes: flakes are smaller in a light fall (0.7) and full size at intensity 1.
        internal static double SizeFactor(double intensity)
        {
            return 0.7 + 0.3 * Clamp(intensity, 0, 1);
        }

        // Fall speed in m/s at an intensity.
        internal static double FallSpeed(double intensity)
        {
            return FallSlow + (FallFast - FallSlow) * Clamp(intensity, 0, 1);
        }

        // Horizontal wind speed in m/s at an intensity.
        internal static double WindSpeed(double intensity)
        {
            return WindAtFull * Clamp(intensity, 0, 1);
        }

        // The scale of a flake's streak (its length is this times its speed) at an intensity.
        internal static double StreakScale(double intensity)
        {
            return StreakScaleLight + (StreakScaleFull - StreakScaleLight) * Clamp(intensity, 0, 1);
        }

        // Seconds a flake lives so that it falls through the box of a layer: the box height over the fall speed.
        internal static double Lifetime(FlakeLayer layer, double fallSpeed)
        {
            return layer.BoxHeight / Math.Max(fallSpeed, 0.5);
        }

        // How far upwind of the camera the box is moved so that the flakes, which drift by wind * lifetime while they live, are spread
        // evenly around the camera: half the drift.
        internal static double UpwindOffset(double windSpeed, double lifetime)
        {
            return -0.5 * windSpeed * lifetime;
        }

        // The least flakes that are alive at once in a layer, to size the system: rate times lifetime.
        internal static int FlakesAlive(double intensity, FlakeLayer layer)
        {
            double fall = FallSpeed(intensity);
            return (int)Math.Ceiling(FlakeRate(intensity, layer) * Lifetime(layer, fall));
        }

        // The alpha of the veil toward the sky at an intensity: a little heavier than toward the ground, and it grows more evenly.
        internal static double SkyVeilAlpha(double intensity, double veilAtFull)
        {
            return Clamp(veilAtFull * 1.1, 0, 0.97) * Math.Pow(Clamp(intensity, 0, 1), 1.1);
        }

        // How much of the storm grey a direction gets: 0 at and below the horizon, 1 from SkyFullSinElevation up, smooth in between.
        // The grey arrives before the veil gets opaque (full from an intensity of 0.125), so the sky never gets lighter on the way.
        // The darkening setting reaches the full grey from 0.67. sinElevation is the sine of the direction above the local horizon.
        internal static double SkyGreyShare(double sinElevation, double intensity, double darkening)
        {
            if (double.IsNaN(sinElevation)) return 0;
            double t = Clamp(sinElevation / SkyFullSinElevation, 0, 1);
            double smooth = t * t * (3 - 2 * t);
            return smooth * Clamp(intensity * 8, 0, 1) * Clamp(darkening * 1.5, 0, 1);
        }

        // The factor on the ambient light at an intensity and darkening setting: 1 in clear air, 1 - AmbientDropAtFull at both 1.
        internal static double AmbientFactor(double intensity, double darkening)
        {
            return 1 - AmbientDropAtFull * Clamp(darkening, 0, 1) * Math.Pow(Clamp(intensity, 0, 1), 0.8);
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
