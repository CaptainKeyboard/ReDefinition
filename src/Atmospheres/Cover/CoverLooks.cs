using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Builds the CoverLook of a pass from the settings, the tuning keys and the field: the one place that reads them for the ground, so
    // that the build of a quad and its later repaints agree. Tuning keys (A01): cover.edgeNoise 0.12 (0..0.4).
    internal static class CoverLooks
    {
        internal const string EdgeNoiseKey = "cover.edgeNoise";
        internal const float EdgeNoiseDefault = 0.12f;

        // The look of a body for the game time ut, without wind (the wind is set per quad).
        internal static CoverLook Make(CoverBodyState state, double ut)
        {
            double drift = Drift(state);
            if (drift > 0) AdvanceTravel(state, ut);
            return new CoverLook
            {
                Def = state.Def,
                Strength = AtmospheresSettings.CoverStrength,
                EdgeNoise = Tuning.Get(EdgeNoiseKey, EdgeNoiseDefault),
                Drift = drift,
                Ut = ut,
                RadiusM = state.Body.Radius,
                TravelEastM = state.WindTravel.EastM,
                TravelNorthM = state.WindTravel.NorthM
            };
        }

        // The places the body's reference wind is the mean of: six points at 40 degrees N/S latitude, 120 degrees apart.
        private static readonly double[] ReferenceLat = { 40, 40, 40, -40, -40, -40 };
        private static readonly double[] ReferenceLon = { 0, 120, 240, 60, 180, 300 };

        // Carries the body's pattern on by the mean wind since the last step (at most once a minute of game time).
        private static void AdvanceTravel(CoverBodyState state, double ut)
        {
            CoverWindTravel travel = state.WindTravel;
            if (!travel.Due(ut)) return;
            double east = 0, north = 0;
            for (int i = 0; i < ReferenceLat.Length; i++)
            {
                double e, n;
                WindAt(state, ReferenceLat[i], ReferenceLon[i], 0.0, ut, out e, out n);
                east += e;
                north += n;
            }
            travel.Advance(ut, east / ReferenceLat.Length, north / ReferenceLat.Length);
        }

        // The drift in force: the layer's own times the setting (both 0..1; 0 is off).
        internal static double Drift(CoverBodyState state)
        {
            double d = state.Def.DriftAmount * AtmospheresSettings.CoverDrift;
            return d < 0.001 ? 0.0 : d > 1.0 ? 1.0 : d;
        }

        // A number that changes when anything a paint reads besides the map changes (strength, edge noise, drift, the layer).
        internal static int SettingsStamp(CoverBodyState state)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + AtmospheresSettings.CoverStrength.GetHashCode();
                h = h * 31 + Tuning.Get(EdgeNoiseKey, EdgeNoiseDefault).GetHashCode();
                h = h * 31 + Drift(state).GetHashCode();
                h = h * 31 + state.Def.GetHashCode();
                return h;
            }
        }

        // The wind at the place of a quad in m/s east and north, from the field; 0 where the field has none. Only asked for where the
        // drift is on.
        internal static void WindAt(CoverBodyState state, double latDeg, double lonDeg, double altM, double ut, out double east, out double north)
        {
            east = 0;
            north = 0;
            WeatherSample s;
            try
            {
                if (!WeatherHub.Sample(state.Index, latDeg, lonDeg, Math.Max(0.0, altM), ut, out s)) return;
            }
            catch (Exception)
            {
                return;
            }
            east = s.WindWorld.x;
            north = s.WindWorld.y;
        }
    }
}
