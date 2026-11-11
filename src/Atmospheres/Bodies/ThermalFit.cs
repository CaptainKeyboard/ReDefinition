using System;

namespace ReDefinition.Atmospheres
{
    // The air temperature of a body sampled at fixed latitudes, longitudes (hour angles of the sun) and altitudes, in the layout of the
    // reconnaissance dumps of B09 (run2-temperature-*.json): index = ((phase * Lon.Length + longitude) * Alt.Length + altitude) * Lat.Length + latitude.
    internal sealed class ThermalSamples
    {
        // The latitudes in degrees from -90 to 90 in steps of 15, the altitudes in metres (0, 1000, 5000, 10000) and the longitudes.
        internal static readonly double[] StandardLatitudes = { -90, -75, -60, -45, -30, -15, 0, 15, 30, 45, 60, 75, 90 };
        internal static readonly double[] StandardAltitudes = { 0, 1000, 5000, 10000 };
        internal static readonly double[] StandardLongitudes = { -180, -90, 0, 90 };

        internal double[] LatitudeDeg = StandardLatitudes;
        internal double[] AltitudeM = StandardAltitudes;
        internal double[] LongitudeDeg = StandardLongitudes;
        internal int Phases = 1;
        internal double[] Kelvin = new double[0];

        internal double At(int phase, int longitude, int altitude, int latitude)
        {
            return Kelvin[((phase * LongitudeDeg.Length + longitude) * AltitudeM.Length + altitude) * LatitudeDeg.Length + latitude];
        }
    }

    // What the generator takes from the game's temperature curves: the annual-mean zonal profile at sea level (|latitude| 0, 15, ..., 90 degrees,
    // both hemispheres averaged), its mean, the lapse rate and the day-night swing at the equator. The derivation is the one of the offline
    // proof (tools/atmosphere-sim, ReconData.FitClimate), so that the dumps of the reconnaissance reproduce its numbers.
    internal sealed class ThermalFit
    {
        internal double MeanK, LapseKPerM = double.NaN, DiurnalAmpK, EquatorK, PoleK, MaxProfileStepK;
        internal double[] ProfileK = new double[7];

        // The fit of a sample table; null where the table is unusable (empty, a non-finite value, no sea-level altitude).
        internal static ThermalFit From(ThermalSamples s)
        {
            if (s == null || s.Kelvin == null) return null;
            int nl = s.LatitudeDeg.Length, na = s.AltitudeM.Length, no = s.LongitudeDeg.Length, np = s.Phases;
            if (nl == 0 || na == 0 || no == 0 || np == 0 || s.Kelvin.Length != np * no * na * nl) return null;
            for (int i = 0; i < s.Kelvin.Length; i++)
            {
                if (double.IsNaN(s.Kelvin[i]) || double.IsInfinity(s.Kelvin[i])) return null;
            }
            int a0 = -1, a5 = -1;
            for (int a = 0; a < na; a++)
            {
                if (s.AltitudeM[a] == 0.0) a0 = a;
                if (s.AltitudeM[a] == 5000.0) a5 = a;
            }
            if (a0 < 0) return null;

            var obs = new double[nl];
            for (int l = 0; l < nl; l++)
            {
                double sum = 0;
                for (int p = 0; p < np; p++)
                {
                    for (int o = 0; o < no; o++) sum += s.At(p, o, a0, l);
                }
                obs[l] = sum / (np * no);
            }
            var fit = new ThermalFit();
            var count = new int[7];
            for (int l = 0; l < nl; l++)
            {
                int k = (int)Math.Round(Math.Abs(s.LatitudeDeg[l]) / 15.0);
                if (k < 0 || k > 6) continue;
                fit.ProfileK[k] += obs[l];
                count[k]++;
            }
            for (int k = 0; k < 7; k++)
            {
                if (count[k] == 0) return null;
                fit.ProfileK[k] /= count[k];
            }
            double mean = 0, weights = 0;
            for (int l = 0; l < nl; l++)
            {
                // DMath.Cos, not the platform's: MeanK is an input of the generator, so a last-bit difference of libm between machines
                // would change every bit of the body's weather.
                double w = Math.Max(0.05, AtmoSim.Core.DMath.Cos(s.LatitudeDeg[l] * Math.PI / 180.0));
                mean += obs[l] * w;
                weights += w;
            }
            fit.MeanK = mean / weights;
            fit.EquatorK = fit.ProfileK[0];
            fit.PoleK = fit.ProfileK[6];
            for (int k = 1; k < 7; k++) fit.MaxProfileStepK = Math.Max(fit.MaxProfileStepK, Math.Abs(fit.ProfileK[k] - fit.ProfileK[k - 1]));

            int equator = 0;
            double best = double.MaxValue;
            for (int l = 0; l < nl; l++)
            {
                if (Math.Abs(s.LatitudeDeg[l]) < best)
                {
                    best = Math.Abs(s.LatitudeDeg[l]);
                    equator = l;
                }
            }
            if (a5 >= 0)
            {
                double drop = 0;
                int n = 0;
                for (int p = 0; p < np; p++)
                {
                    for (int o = 0; o < no; o++)
                    {
                        for (int l = 0; l < nl; l++)
                        {
                            drop += s.At(p, o, a0, l) - s.At(p, o, a5, l);
                            n++;
                        }
                    }
                }
                fit.LapseKPerM = drop / n / 5000.0;
            }
            double swing = 0;
            for (int p = 0; p < np; p++)
            {
                double m = 0;
                for (int o = 0; o < no; o++) m += s.At(p, o, a0, equator);
                m /= no;
                for (int o = 0; o < no; o++) swing = Math.Max(swing, Math.Abs(s.At(p, o, a0, equator) - m));
            }
            fit.DiurnalAmpK = swing;
            return fit;
        }
    }
}
