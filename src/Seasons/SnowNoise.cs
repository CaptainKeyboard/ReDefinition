using System;
using static ReDefinition.Seasons.Mathx;

namespace ReDefinition.Seasons
{
    // A cheap deterministic value noise on the unit sphere, from -1 to 1. It breaks the snow line up so that it does not run along
    // one latitude. The same direction always gives the same value, in the game and in the tests.
    internal static class SnowNoise
    {
        // The cells per unit of length of the two octaves: about 25 km and 6 km on Kerbin's 600 km radius.
        internal const double CoarseFrequency = 24;
        internal const double FineFrequency = 96;

        // The share of the coarse octave.
        internal const double CoarseWeight = 0.65;

        // The noise at a direction from the planet's centre (it need not be normalised), from -1 to 1.
        internal static double Value(double x, double y, double z)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z) || double.IsInfinity(x) || double.IsInfinity(y) || double.IsInfinity(z)) return 0;
            double length = Math.Sqrt(x * x + y * y + z * z);
            if (length <= 0) return 0;
            x /= length;
            y /= length;
            z /= length;
            double value = CoarseWeight * Octave(x * CoarseFrequency, y * CoarseFrequency, z * CoarseFrequency)
                           + (1 - CoarseWeight) * Octave(x * FineFrequency + 31.7, y * FineFrequency + 17.3, z * FineFrequency + 5.1);
            return value < -1 ? -1 : value > 1 ? 1 : value;
        }

        private static double Octave(double x, double y, double z)
        {
            double fx = Math.Floor(x), fy = Math.Floor(y), fz = Math.Floor(z);
            int ix = (int)fx, iy = (int)fy, iz = (int)fz;
            double tx = Smooth(x - fx), ty = Smooth(y - fy), tz = Smooth(z - fz);
            double c00 = Lerp(Corner(ix, iy, iz), Corner(ix + 1, iy, iz), tx);
            double c10 = Lerp(Corner(ix, iy + 1, iz), Corner(ix + 1, iy + 1, iz), tx);
            double c01 = Lerp(Corner(ix, iy, iz + 1), Corner(ix + 1, iy, iz + 1), tx);
            double c11 = Lerp(Corner(ix, iy + 1, iz + 1), Corner(ix + 1, iy + 1, iz + 1), tx);
            return Lerp(Lerp(c00, c10, ty), Lerp(c01, c11, ty), tz);
        }

        // The random value of a lattice corner, from -1 to 1.
        internal static double Corner(int x, int y, int z)
        {
            unchecked
            {
                uint h = (uint)x * 374761393u + (uint)y * 668265263u + (uint)z * 2147483647u;
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return h / (double)uint.MaxValue * 2 - 1;
            }
        }

        private static double Smooth(double t)
        {
            return t * t * (3 - 2 * t);
        }
    }
}
