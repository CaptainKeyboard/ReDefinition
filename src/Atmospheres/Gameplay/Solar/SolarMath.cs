using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The arithmetic of the solar panel dimming (package D31), pure.
    internal static class SolarMath
    {
        // The share of the sun's direct light a cloud or storm takes from a panel, at most, in the plan's wording the storm solar loss factor:
        // a sky whose transmission along the sun ray is 0 leaves the panel with 1 - 0.85. [OWNER taste; default 0.85]
        internal const double StormSolarLoss = 0.85;
        // The smoothing time of the factor in seconds, and the interval at which a vessel's factor is worked out.
        internal const double SmoothingSeconds = 2.0;
        internal const double UpdateIntervalSeconds = 0.5;
        // A change of the factor smaller than this does not rebuild the panels' curve.
        internal const double ApplyEpsilon = 0.01;

        // The factor from the transmission along the sun ray (the one definition, SunTransmission.From): 1 for clear sky.
        // bodySolarLoss is the body node's solarLoss, strength the setting solarDimmingStrength.
        internal static double Factor(double transmission, double bodySolarLoss, double strength)
        {
            double t = double.IsNaN(transmission) ? 1.0 : transmission < 0.0 ? 0.0 : transmission > 1.0 ? 1.0 : transmission;
            double loss = (1.0 - t) * StormSolarLoss * Math.Max(0.0, bodySolarLoss) * Math.Max(0.0, strength);
            return loss > 1.0 ? 0.0 : 1.0 - loss;
        }

        // The transmission of a sky from the vertical optical depth and the sun's elevation.
        internal static double Transmission(double opticalDepthVertical, double sunElevationRad)
        {
            return SunTransmission.From((float)opticalDepthVertical, (float)sunElevationRad);
        }

        // The factor one step on: it approaches the target with the smoothing time.
        internal static double Smooth(double current, double target, double dt)
        {
            if (dt <= 0.0) return current;
            double k = 1.0 - Math.Exp(-dt / SmoothingSeconds);
            return current + (target - current) * k;
        }
    }
}
