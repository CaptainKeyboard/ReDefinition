using System;
using static ReDefinition.Seasons.Mathx;

namespace ReDefinition.Seasons
{
    // How much of the ground is snow-covered: a pure function of latitude, height above sea level, the season phase and a noise value.
    // Every ground and orbit view of the snow reads this one function, so that they agree. The numbers are design choices, not game
    // data; they are all here.
    internal static class SnowMask
    {
        // The latitude in degrees at which the snow line sits when it is at its mean.
        internal const double MeanLineDegrees = 60;

        // The default snow line at the hemisphere's midwinter, in degrees; the setting changes it. In midsummer the line is as far
        // poleward of the mean as the winter line is equatorward (78 degrees).
        internal const double DefaultWinterLineDegrees = 42;

        // The limits of the winter line setting: a line at the mean means no seasons.
        internal const double LowestWinterLineDegrees = 30;
        internal const double HighestWinterLineDegrees = MeanLineDegrees;

        // Half the width of the transition across the line, in degrees.
        internal const double BlendHalfWidthDegrees = 6;

        // How far the line moves equatorward for each kilometre of height, in degrees.
        internal const double HeightBonusDegreesPerKm = 1.5;

        // The most the noise value (-1 to 1) moves the line, in degrees.
        internal const double NoiseDegrees = 4;

        // How far the snow trails the sun, as a part of the year: the coldest time is after the solstice.
        internal const double LagYears = 0.06;

        // The permanent polar caps: no snow below CapStart, full snow from CapFull, in degrees of latitude.
        internal const double CapStartDegrees = 78;
        internal const double CapFullDegrees = 82;

        // The latitude of the snow line in degrees for a hemisphere phase (0 is its midwinter), without height and noise.
        internal static double LineDegrees(double hemispherePhase, double winterLineDegrees)
        {
            double winter = Clamp(winterLineDegrees, LowestWinterLineDegrees, HighestWinterLineDegrees);
            double lagged = SeasonClock.Wrap(hemispherePhase - LagYears);
            return MeanLineDegrees - (MeanLineDegrees - winter) * Math.Cos(2 * Math.PI * lagged);
        }

        // The snow cover from 0 (bare) to 1 (covered) at a latitude in degrees (north positive), a height above sea level in metres, the
        // season phase of the clock (SeasonClock.Phase) and a noise value from -1 to 1. A value that is not a number gives 0.
        internal static double Coverage(double latitudeDegrees, double heightMetres, double seasonPhase, double noise, double winterLineDegrees)
        {
            if (double.IsNaN(latitudeDegrees) || double.IsNaN(heightMetres) || double.IsNaN(seasonPhase) || double.IsNaN(noise)) return 0;
            double distance = Math.Abs(Clamp(latitudeDegrees, -90, 90));
            double line = LineDegrees(SeasonClock.HemispherePhase(seasonPhase, latitudeDegrees >= 0), winterLineDegrees);
            double effective = distance + Math.Max(0, heightMetres) / 1000 * HeightBonusDegreesPerKm + Clamp(noise, -1, 1) * NoiseDegrees;
            double seasonal = SmoothStep(line - BlendHalfWidthDegrees, line + BlendHalfWidthDegrees, effective);
            double cap = SmoothStep(CapStartDegrees, CapFullDegrees, distance);
            return Clamp(Math.Max(seasonal, cap), 0, 1);
        }
    }
}
