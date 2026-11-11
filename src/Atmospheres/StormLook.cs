using System;
using static ReDefinition.Atmospheres.Mathx;

namespace ReDefinition.Atmospheres
{
    // What a storm intensity (0 to 1) looks like: visibility, fog density, veil, flakes. Pure functions, so that the numbers are tested;
    // StormEffect applies them.
    // Numbers: the fog is ExponentialSquared with density sqrt(3) / visibility, so that 5 % of the contrast is left at the
    // visibility. The visibility goes from 20 km at intensity 0 down to stormVisibility (60 m) at 1, and is about 245 m at 0.5. The
    // fog colour is grey-white scaled by the sun's elevation. The storm is full below 4 km above sea level and gone from 10 km.
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

        // Snowfall: flakes emitted per second at full intensity over all layers, and the most each layer holds.
        // A blizzard is a dense curtain of many small flakes with a few large near ones: 2400 a second, of which 6 % are near, 34 % mid
        // and 60 % far, at most 24000 alive in a layer.
        internal const float FlakesPerSecond = 2400f;
        internal const int MaxFlakesPerLayer = 24000;

        // The rate grows with the intensity to this power: 0.35 gives 0.43 of the full rate.
        internal const double FlakeRateCurve = 0.8;

        // Snow falls slowly: the layers' fall speeds (FlakeLayer.FallMin, FallMax) are scaled by a factor that goes from FallFactorLight
        // at intensity 0 to 1 at intensity 1. The horizontal wind at 1 is strong against it, so that a storm drifts sideways.
        internal const float FallFactorLight = 0.85f;
        internal const float WindAtFull = 4.5f;

        // Gusts: the wind speed is multiplied by a factor that swings between 1 - GustDepth and 1 + GustDepth.
        internal const float GustDepth = 0.4f;

        // The turbulence of every flake (the noise of its particle system): a displacement in metres per axis, vertical weaker than
        // horizontal, and how fast the noise changes. It makes the flakes sway and tumble instead of falling in straight lines.
        internal const float SwayHorizontal = 1.3f, SwayVertical = 0.45f, SwayFrequency = 0.45f, SwayScroll = 0.35f;

        // The stretch of a flake along its velocity: length = size + speed * scale. Tiny, so that a flake stays a round disc and is
        // at most StreakRatioMax times its size long, in a storm too.
        internal const float StreakScaleLight = 0.0008f, StreakScaleFull = 0.0012f;
        internal const double StreakRatioMax = 1.5;

        // The size limits a flake keeps on the screen, as a part of the viewport height: far flakes stay visible, near ones do not
        // fill it.
        internal const float MinScreenSize = 0.0022f, MaxScreenSize = 0.03f;

        // Storm sky: the default of the darkening setting, the storm grey by day, and the sine of the elevation from which the sky is
        // fully that grey.
        internal const float SkyDarkeningDefault = 0.6f;
        internal static readonly float[] SkyGreyDay = { 0.34f, 0.36f, 0.40f };
        internal const double SkyFullSinElevation = 0.4;

        // The most the ambient light is lowered at full intensity and full darkening.
        internal const double AmbientDropAtFull = 0.6;

        // A storm layer of flakes: a box around the camera, the size range at full intensity in metres, the share of the rate and
        // the fall speed range in m/s (larger flakes fall faster). Near flakes are translucent, so that they read as out-of-focus discs.
        internal struct FlakeLayer
        {
            internal readonly float BoxWidth, BoxHeight, SizeMin, SizeMax, RateShare, FallMin, FallMax, Alpha;

            internal FlakeLayer(float boxWidth, float boxHeight, float sizeMin, float sizeMax, float rateShare, float fallMin, float fallMax, float alpha)
            {
                BoxWidth = boxWidth;
                BoxHeight = boxHeight;
                SizeMin = sizeMin;
                SizeMax = sizeMax;
                RateShare = rateShare;
                FallMin = fallMin;
                FallMax = fallMax;
                Alpha = alpha;
            }
        }

        // Three depths. Near: few, large and translucent. Mid: the bulk. Far: many small ones that make the grain of the storm.
        internal static readonly FlakeLayer NearLayer = new FlakeLayer(7f, 6f, 0.10f, 0.26f, 0.06f, 1.2f, 2.0f, 0.5f);
        internal static readonly FlakeLayer MidLayer = new FlakeLayer(18f, 10f, 0.03f, 0.08f, 0.34f, 0.9f, 1.6f, 0.95f);
        internal static readonly FlakeLayer FarLayer = new FlakeLayer(36f, 12f, 0.02f, 0.05f, 0.60f, 0.8f, 1.3f, 1.0f);

        // The veil is heavier toward the horizon, where the distant ground is. The boost is the most alpha added there, and it is
        // gone from the elevation whose sine is the band.
        internal const double HorizonBoost = 0.08, HorizonBandSin = 0.25;

        // The storm fades with height: full below FadeStartM, gone from FadeEndM above sea level.
        internal const double FadeStartM = 4000, FadeEndM = 10000;

        // Visibility in metres at an intensity: ClearVisibilityM at 0, minVisibility at 1, in between along a curve that is flat
        // near 0 in the exponent.
        internal static double VisibilityMetres(double intensity, double minVisibility)
        {
            double i = ClampOrMin(intensity, 0, 1);
            double min = ClampOrMin(minVisibility, MinVisibilityLow, MinVisibilityHigh);
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
            return ClampOrMin(veilAtFull, 0, VeilMax) * Math.Pow(ClampOrMin(intensity, 0, 1), VeilCurve);
        }

        // The alpha of the veil toward the horizon: the ground alpha plus up to HorizonBoost, at most 0.97. The boost fades out with the
        // sine of the elevation, and a faint veil gets less of it.
        internal static double HorizonAlpha(double groundAlpha, double sinElevation)
        {
            if (double.IsNaN(sinElevation) || double.IsNaN(groundAlpha) || groundAlpha <= 0) return Math.Max(groundAlpha, 0);
            double t = 1 - ClampOrMin(Math.Abs(sinElevation) / HorizonBandSin, 0, 1);
            double smooth = t * t * (3 - 2 * t);
            return Math.Min(0.97, groundAlpha + HorizonBoost * smooth * ClampOrMin(groundAlpha / 0.5, 0, 1));
        }

        // Flakes per second over both layers at an intensity; none below 0.02.
        internal static double FlakeRate(double intensity)
        {
            double i = ClampOrMin(intensity, 0, 1);
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
            return 0.7 + 0.3 * ClampOrMin(intensity, 0, 1);
        }

        // The factor on the layers' fall speeds at an intensity.
        internal static double FallFactor(double intensity)
        {
            return FallFactorLight + (1 - FallFactorLight) * ClampOrMin(intensity, 0, 1);
        }

        // The slowest and the fastest fall of a layer's flakes in m/s at an intensity.
        internal static double FallMin(FlakeLayer layer, double intensity)
        {
            return layer.FallMin * FallFactor(intensity);
        }

        internal static double FallMax(FlakeLayer layer, double intensity)
        {
            return layer.FallMax * FallFactor(intensity);
        }

        // Horizontal wind speed in m/s at an intensity, before the gusts.
        internal static double WindSpeed(double intensity)
        {
            return WindAtFull * ClampOrMin(intensity, 0, 1);
        }

        // The factor of the gusts at a time in seconds: a slow and a quicker swing added, between 1 - GustDepth and 1 + GustDepth.
        // The phase shifts the swings so that two seeds do not gust together.
        internal static double GustFactor(double seconds, double phase)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || double.IsNaN(phase)) return 1;
            double slow = Math.Sin(2 * Math.PI * seconds / 13.0 + phase);
            double quick = Math.Sin(2 * Math.PI * seconds / 4.1 + 2.3 * phase + 1.1);
            return 1 + GustDepth * (0.65 * slow + 0.35 * quick);
        }

        // The fastest a flake of a layer moves at an intensity, in m/s: its fall and the gusting wind, with the spread of the wind
        // among flakes (WindSpread).
        internal static double TopSpeed(FlakeLayer layer, double intensity)
        {
            double wind = WindSpeed(intensity) * (1 + GustDepth) * (1 + WindSpread);
            double fall = FallMax(layer, intensity);
            return Math.Sqrt(wind * wind + fall * fall);
        }

        // Each flake's wind is the wind times a random factor between 1 - WindSpread and 1 + WindSpread.
        internal const float WindSpread = 0.2f;

        // The scale of a flake's stretch (length = size + speed * scale) at an intensity.
        internal static double StreakScale(double intensity)
        {
            return StreakScaleLight + (StreakScaleFull - StreakScaleLight) * ClampOrMin(intensity, 0, 1);
        }

        // How long the flake of a layer is relative to its size at an intensity, for its smallest size and its top speed.
        internal static double StreakRatio(FlakeLayer layer, double intensity)
        {
            double size = layer.SizeMin * SizeFactor(intensity);
            return (size + StreakScale(intensity) * TopSpeed(layer, intensity)) / size;
        }

        // Seconds a flake lives so that it falls through the box of a layer: the box height over the mean fall speed.
        internal static double Lifetime(FlakeLayer layer, double intensity)
        {
            return layer.BoxHeight / Math.Max(0.5 * (FallMin(layer, intensity) + FallMax(layer, intensity)), 0.2);
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
            return (int)Math.Ceiling(FlakeRate(intensity, layer) * Lifetime(layer, intensity));
        }

        // The alpha of the veil toward the sky at an intensity: a little heavier than toward the ground, and it grows more evenly.
        internal static double SkyVeilAlpha(double intensity, double veilAtFull)
        {
            return ClampOrMin(veilAtFull * 1.1, 0, 0.97) * Math.Pow(ClampOrMin(intensity, 0, 1), 1.1);
        }

        // How much of the storm grey a direction gets: 0 at and below the horizon, 1 from SkyFullSinElevation up, smooth in between.
        // The grey arrives before the veil gets opaque (full from an intensity of 0.125), so the sky never gets lighter on the way.
        // The darkening setting reaches the full grey from 0.67. sinElevation is the sine of the direction above the local horizon.
        internal static double SkyGreyShare(double sinElevation, double intensity, double darkening)
        {
            if (double.IsNaN(sinElevation)) return 0;
            double t = ClampOrMin(sinElevation / SkyFullSinElevation, 0, 1);
            double smooth = t * t * (3 - 2 * t);
            return smooth * ClampOrMin(intensity * 8, 0, 1) * ClampOrMin(darkening * 1.5, 0, 1);
        }

        // The factor on the ambient light at an intensity and darkening setting: 1 in clear air, 1 - AmbientDropAtFull at both 1.
        internal static double AmbientFactor(double intensity, double darkening)
        {
            return 1 - AmbientDropAtFull * ClampOrMin(darkening, 0, 1) * Math.Pow(ClampOrMin(intensity, 0, 1), 0.8);
        }

        // The factor by which the storm reaches a height above sea level: 1 up to FadeStartM, 0 from FadeEndM.
        internal static double HeightFactor(double altitudeMetres)
        {
            if (double.IsNaN(altitudeMetres)) return 0;
            double t = ClampOrMin((FadeEndM - altitudeMetres) / (FadeEndM - FadeStartM), 0, 1);
            return t * t * (3 - 2 * t);
        }

        // The share of the sun that lights the snow veil from the sun's elevation: 0 from 3 degrees below the horizon, 1 from 6 degrees above.
        internal static double SunVisibility(double sinElevation)
        {
            double t = ClampOrMin((sinElevation + 0.05) / 0.16, 0, 1);
            return t * t * (3 - 2 * t);
        }

        // How much the ambient light of the veil is the blue-grey of the night: 0 from the sun's sine 0.1 up, 1 from -0.1 down.
        internal static double NightShare(double sinElevation)
        {
            double t = ClampOrMin((0.1 - sinElevation) / 0.2, 0, 1);
            return t * t * (3 - 2 * t);
        }

        // The share of a low sun's warm tint: 1 at the horizon, 0 from the sine 0.35 (about 20 degrees) up.
        internal static double WarmShare(double sinElevation)
        {
            double t = ClampOrMin(1 - sinElevation / 0.35, 0, 1);
            return t * t * (3 - 2 * t);
        }

        // The brightness of the fog from the sun's elevation: sinElevation is the sine of the sun above the local horizon.
        // Grey-white by day, dark grey by night.
        internal static double FogBrightness(double sinElevation)
        {
            double t = ClampOrMin((sinElevation + 0.1) / 0.4, 0, 1);
            double smooth = t * t * (3 - 2 * t);
            return 0.12 + 0.88 * smooth;
        }
    }
}
