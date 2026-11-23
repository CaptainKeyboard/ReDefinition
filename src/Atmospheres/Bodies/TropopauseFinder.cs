using System;

namespace ReDefinition.Atmospheres
{
    // The tropopause of a temperature profile by the WMO's lapse-rate rule (WMO 1957, the "first tropopause"): the lowest level at which the lapse rate falls to 2 K/km or
    // less, provided the mean lapse rate between that level and every level above it within 2 km does not exceed 2 K/km. The profile is the game's temperature curve of a
    // body (BodyProbe samples CelestialBody.GetTemperature every 100 m over the atmosphere's depth). Owner 2026-11-23 "Messen, dann Anteile": the cloud heights become shares
    // of the body's measured troposphere instead of an assumed share of the atmosphere's depth.
    internal static class TropopauseFinder
    {
        internal const double CriticalLapseKPerKm = 2.0, CheckDepthM = 2000.0;

        // altitudesM ascending, kelvin the temperature at each; returns NaN where no level meets the rule (a profile that cools to its top, or one too short to check 2 km above).
        internal static double Find(double[] altitudesM, double[] kelvin, out double kelvinAt)
        {
            kelvinAt = double.NaN;
            if (altitudesM == null || kelvin == null || altitudesM.Length != kelvin.Length || altitudesM.Length < 3) return double.NaN;
            int n = altitudesM.Length;
            for (int i = 0; i < n - 1; i++)
            {
                double dz = altitudesM[i + 1] - altitudesM[i];
                if (!(dz > 0)) return double.NaN;
                double lapse = -(kelvin[i + 1] - kelvin[i]) / dz * 1000.0;
                if (lapse > CriticalLapseKPerKm) continue;
                // The level must stay at or below 2 K/km on the mean to every level within 2 km above it, and the profile must reach that far.
                if (altitudesM[n - 1] - altitudesM[i] < CheckDepthM) return double.NaN;
                bool holds = true;
                for (int j = i + 1; j < n && altitudesM[j] - altitudesM[i] <= CheckDepthM; j++)
                {
                    double mean = -(kelvin[j] - kelvin[i]) / (altitudesM[j] - altitudesM[i]) * 1000.0;
                    if (mean > CriticalLapseKPerKm) { holds = false; break; }
                }
                if (!holds) continue;
                kelvinAt = kelvin[i];
                return altitudesM[i];
            }
            return double.NaN;
        }
    }
}
