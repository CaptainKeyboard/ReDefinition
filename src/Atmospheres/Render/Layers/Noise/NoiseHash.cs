using System;

namespace ReDefinition.Atmospheres
{
    // The integer hash of the cloud noise (package T02). The same function is written in HLSL (ReDefinitionAtmospheresNoise.cginc) and in
    // NumPy (tools/weather_metrics/noise_model.py), so that a lattice value is the same in the generators, on the GPU and in the tests. The
    // finaliser is "lowbias32" (Chris Wellons, public domain, https://nullprogram.com/blog/2018/07/31/).
    internal static class NoiseHash
    {
        internal static uint LowBias32(uint x)
        {
            unchecked
            {
                x ^= x >> 16;
                x *= 0x7FEB352Du;
                x ^= x >> 15;
                x *= 0x846CA68Bu;
                x ^= x >> 16;
                return x;
            }
        }

        // The hash of an integer lattice point and a seed; the integers wrap to 32 bits.
        internal static uint Hash3(int ix, int iy, int iz, uint seed)
        {
            unchecked
            {
                uint h = (seed * 0x9E3779B1u) ^ ((uint)ix * 0x85EBCA6Bu) ^ ((uint)iy * 0xC2B2AE35u) ^ ((uint)iz * 0x27D4EB2Fu);
                return LowBias32(h);
            }
        }

        // A hash as a float in [0, 1): the top 24 bits.
        internal static double Unit(uint h)
        {
            return (h >> 8) * (1.0 / 16777216.0);
        }

        // Three independent values in [0, 1) from one hash.
        internal static void Vec(uint h, out double a, out double b, out double c)
        {
            a = Unit(h);
            b = Unit(LowBias32(h ^ 0xA511E9B3u));
            c = Unit(LowBias32(h ^ 0x63D83595u));
        }

        // The quintic fade of the gradient and value noise.
        internal static double Fade(double t)
        {
            return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
        }

        internal static int Mod(int a, int n)
        {
            int r = a % n;
            return r < 0 ? r + n : r;
        }
    }
}
