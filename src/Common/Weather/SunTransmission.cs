using System;

namespace ReDefinition.Core
{
    // The one definition of how much of the sun's direct light a cloud lets through: exp(-tau x air mass). WeatherView.SunTransmission
    // (along the sun ray at the camera, written by the frame driver), the vessel's solar panels and the raytraced scene all use it, so
    // they agree.
    public static class SunTransmission
    {
        // The largest air mass the slant path is counted with: a sun at the horizon does not give an infinite path.
        public const float MaxAirMass = 20f;

        // The air mass of a cloud layer for a sun at that elevation above the horizon in radians: 1 overhead, rising as the sun gets
        // lower, clamped to MaxAirMass.
        public static float AirMass(float sunElevationRad)
        {
            if (float.IsNaN(sunElevationRad)) return 1f;
            double s = Math.Sin(sunElevationRad);
            if (s <= 1.0 / MaxAirMass) return MaxAirMass;
            return (float)(1.0 / s);
        }

        // The transmission from 0 to 1: 1 for no cloud, falling with the vertical optical depth and with a lower sun. A negative or
        // NaN optical depth counts as none.
        public static float From(float opticalDepthVertical, float sunElevationRad)
        {
            if (!(opticalDepthVertical > 0f)) return 1f;
            double t = Math.Exp(-(double)opticalDepthVertical * AirMass(sunElevationRad));
            return t < 0.0 ? 0f : t > 1.0 ? 1f : (float)t;
        }
    }
}
