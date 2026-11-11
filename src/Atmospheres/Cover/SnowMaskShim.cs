using System;
using static ReDefinition.Atmospheres.Mathx;

namespace ReDefinition.Atmospheres
{
    // The season-line cover function of the Kerbin-only snow mod, kept as a shim for the one caller that is not package D34's:
    // StormEffect.UpdateRainToSnow (package C20) and its test still read it. The ground, the vehicles, the tracks and the orbit view no
    // longer do: their depth is the weather field's. The shim leaves with StormEffect; nothing new may call it.
    [Obsolete("The cover is the weather field's (CoverDepth, CoverMap). Leaves with StormEffect.")]
    internal static class SnowMask
    {
        internal const double MeanLineDegrees = 60;
        internal const double DefaultWinterLineDegrees = 42;
        internal const double LowestWinterLineDegrees = 30;
        internal const double HighestWinterLineDegrees = MeanLineDegrees;
        internal const double BlendHalfWidthDegrees = 6;
        internal const double HeightBonusDegreesPerKm = 1.5;
        internal const double NoiseDegrees = 4;
        internal const double LagYears = 0.06;
        internal const double CapStartDegrees = 78;
        internal const double CapFullDegrees = 82;

        internal static double LineDegrees(double hemispherePhase, double winterLineDegrees)
        {
            double winter = Clamp(winterLineDegrees, LowestWinterLineDegrees, HighestWinterLineDegrees);
            double lagged = SeasonClock.Wrap(hemispherePhase - LagYears);
            return MeanLineDegrees - (MeanLineDegrees - winter) * Math.Cos(2 * Math.PI * lagged);
        }

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
