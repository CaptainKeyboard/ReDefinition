using System;
using static ReDefinition.Atmospheres.Mathx;

namespace ReDefinition.Atmospheres
{
    // Wind redistribution of the cover (optional): d' = d x (1 + drift x N(x - w t k)). N is the deterministic SnowNoise advected
    // downwind and stretched along the wind (snow drifts) or ridged with crests across the wind (dunes of sand and dust), plus a bias
    // from the ground's normal: more on the lee side, less on the windward side. Pure: the same place, wind and travel give the same
    // value in the game and in the tests; the travel is CoverWindTravel's. Technique after the plan's section 6.1; the pattern's
    // constants are tuning keys.
    internal static class CoverWind
    {
        // The advection of the pattern: metres of travel of the pattern for each metre per second of wind and each second of game time.
        // Small, so that a drift moves over hours, not seconds. [VERIFY: the look is for the owner (T6)]
        internal const double AdvectionPerSecond = 0.01;

        // The noise cells per body radius before the stretch: one cell is a radius over this number (1.25 km on a 600 km body).
        internal const double FeaturesPerRadius = 480.0;

        // The most the lee slope moves the depth, as a share of drift.
        internal const double LeeBias = 0.25;

        // The factor 1 + drift x N, N in -1..1, at a place on the unit sphere (direction x, y, z). The wind in m/s east and north at the
        // place gives the pattern's direction (stretch along it or dune crests across it); travelEastM and travelNorthM are how far the
        // pattern has been carried so far, in metres (CoverWindTravel: the integral of the wind, so a change of the wind never moves the
        // pattern as a whole). stretch is the pattern's length along the wind over its width (1 and up); ridged turns the pattern into
        // dune crests. Pure.
        internal static double Factor(double x, double y, double z, double windEastMs, double windNorthMs, double travelEastM, double travelNorthM,
            double drift, double stretch, bool ridged, double bodyRadiusM)
        {
            if (!(drift > 0) || double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z)) return 1.0;
            double speed = Math.Sqrt(windEastMs * windEastMs + windNorthMs * windNorthMs);
            double ex, ey, ez, nx, ny, nz;
            LocalFrame(x, y, z, out ex, out ey, out ez, out nx, out ny, out nz);
            // The advected position: back by the travel so far, in metres, converted to the unit sphere.
            double metresToUnit = 1.0 / Math.Max(1.0, bodyRadiusM);
            double sx = x - (ex * travelEastM + nx * travelNorthM) * metresToUnit;
            double sy = y - (ey * travelEastM + ny * travelNorthM) * metresToUnit;
            double sz = z - (ez * travelEastM + nz * travelNorthM) * metresToUnit;
            double n = PatternNoise(sx, sy, sz, windEastMs, windNorthMs, speed, stretch, ridged, ex, ey, ez, nx, ny, nz);
            return Math.Max(0.0, 1.0 + drift * n);
        }

        // The depth after the drift: d (0 to 1) times the factor, plus the lee-slope bias. The slope terms are the ground normal's
        // horizontal components east and north (each -1..1, the sine of the tilt toward that direction); the lee side is where the ground
        // faces along the wind. At drift 0 the depth is d.
        internal static double Redistribute(double depth01, double factor, double drift, double normalEast, double normalNorth, double windEastMs, double windNorthMs)
        {
            if (!(drift > 0) || depth01 <= 0) return Clamp01(depth01);
            double speed = Math.Sqrt(windEastMs * windEastMs + windNorthMs * windNorthMs);
            double lee = 0;
            if (speed > 0.1)
            {
                // A normal that leans along the wind is the lee face (the ground drops away downwind).
                lee = Clamp((normalEast * windEastMs + normalNorth * windNorthMs) / speed, -1, 1);
            }
            double d = depth01 * factor * (1.0 + LeeBias * drift * lee);
            return Clamp01(d);
        }

        // The noise samples that are averaged along the smear direction: at most this many, spaced half a noise cell apart (less apart
        // for a very long streak), so that a step along the line moves the window by less than the spacing and changes the mean little.
        private const int MaxSmearSamples = 15;
        private const double SmearSpacingCells = 0.5;

        // The pattern N, -1..1. The lattice noise is averaged over a line through the place: along the wind for snow (long drifts
        // that lie with the wind) and across it for dunes (crests across the wind); the line is `stretch` noise cells long. The average
        // of weakly related samples is flatter than one sample, so it is scaled back toward the range of one. Ridged turns it into
        // crests (1 - 2 |n|). Stateless and continuous.
        private static double PatternNoise(double sx, double sy, double sz, double we, double wn, double speed, double stretch, bool ridged,
            double ex, double ey, double ez, double nx, double ny, double nz)
        {
            double st = Math.Max(1.0, stretch);
            // Along-wind and cross-wind unit vectors in the tangent plane (east and north when the wind is calm).
            double ae = speed > 0.01 ? we / speed : 1.0, an = speed > 0.01 ? wn / speed : 0.0;
            double ax = ex * ae + nx * an, ay = ey * ae + ny * an, az = ez * ae + nz * an;
            double dx = ridged ? -(ex * an) + nx * ae : ax;
            double dy = ridged ? -(ey * an) + ny * ae : ay;
            double dz = ridged ? -(ez * an) + nz * ae : az;
            // The line is `st` cells long, the samples at least half a cell apart, an odd number of them around the place.
            int samples = (int)Math.Round(st / SmearSpacingCells) | 1;
            if (samples < 3) samples = 3;
            if (samples > MaxSmearSamples) samples = MaxSmearSamples;
            double stepCells = st / (samples - 1);
            double sum = 0;
            for (int k = -(samples / 2); k <= samples / 2; k++)
            {
                double s = k * stepCells / FeaturesPerRadius;
                sum += SnowNoise.Lattice((sx + dx * s) * FeaturesPerRadius + 0.37, (sy + dy * s) * FeaturesPerRadius + 0.11, (sz + dz * s) * FeaturesPerRadius + 0.73);
            }
            // The mean of a line of st cells is flatter than one sample: about st independent values (the noise is related over a cell), so
            // its spread falls as the square root of st; scaled back toward the range of one.
            double v = sum / samples * Math.Sqrt(st);
            if (ridged) v = 1.0 - 2.0 * Math.Abs(v);
            return Clamp(v, -1, 1);
        }

        // The unit east and north vectors at a direction on the unit sphere (PQS convention: y is the pole axis). At a pole east is x.
        internal static void LocalFrame(double x, double y, double z, out double ex, out double ey, out double ez, out double nx, out double ny, out double nz)
        {
            double len = Math.Sqrt(x * x + y * y + z * z);
            if (len <= 0)
            {
                ex = 1; ey = 0; ez = 0; nx = 0; ny = 1; nz = 0;
                return;
            }
            x /= len; y /= len; z /= len;
            // East: the pole axis (0,1,0) crossed with the direction, normalised.
            ex = z; ey = 0; ez = -x;
            double el = Math.Sqrt(ex * ex + ez * ez);
            if (el < 1e-9)
            {
                ex = 1; ey = 0; ez = 0;
            }
            else
            {
                ex /= el; ez /= el;
            }
            // North: the direction crossed with east.
            nx = y * ez - z * ey;
            ny = z * ex - x * ez;
            nz = x * ey - y * ex;
            double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl > 0)
            {
                nx /= nl; ny /= nl; nz /= nl;
            }
        }
    }
}
