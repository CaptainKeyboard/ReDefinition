using System;

namespace AtmoSim.Core
{
    // Integer hashing for the seeded parts of the generator (system generations, lattice cells, synthetic maps). Only
    // unsigned 64-bit multiplications, xors and shifts: identical on every platform.
    public static class Hash
    {
        public static ulong Mix(ulong h)
        {
            h ^= h >> 30; h *= 0xBF58476D1CE4E5B9UL;
            h ^= h >> 27; h *= 0x94D049BB133111EBUL;
            h ^= h >> 31;
            return h;
        }

        public static ulong Combine(ulong h, long v)
        {
            return Mix(h ^ (0x9E3779B97F4A7C15UL + (ulong)v + (h << 6) + (h >> 2)));
        }

        public static ulong Of(ulong seed, long a, long b, long c)
        {
            return Combine(Combine(Combine(Mix(seed ^ 0x2545F4914F6CDD1DUL), a), b), c);
        }

        // 53 bits to a double in [0, 1).
        public static double Unit(ulong h) { return (h >> 11) * (1.0 / 9007199254740992.0); }

        // The next draw of a chain: stream k of a hash.
        public static double Draw(ulong h, int k) { return Unit(Mix(h + (ulong)(k + 1) * 0x9E3779B97F4A7C15UL)); }

        // Folds a double into a hash by its bit pattern (for result hashes in the determinism tests).
        public static ulong Fold(ulong h, double v)
        {
            return Mix(h ^ (ulong)BitConverter.DoubleToInt64Bits(v) ^ 0x6A09E667F3BCC909UL);
        }
    }

    // A small deterministic generator (xorshift64*), used for the synthetic maps.
    public sealed class Rng
    {
        private ulong s;
        public Rng(ulong seed) { s = seed == 0 ? 0x2545F4914F6CDD1DUL : seed; for (int i = 0; i < 4; i++) Next(); }
        public ulong Next()
        {
            s ^= s >> 12; s ^= s << 25; s ^= s >> 27;
            return s * 0x2545F4914F6CDD1DUL;
        }
        public double Uniform() { return (Next() >> 11) * (1.0 / 9007199254740992.0); }
        // Sum of twelve uniforms minus six: approximately standard normal, without log or sqrt.
        public double Gauss()
        {
            double t = 0; for (int i = 0; i < 12; i++) t += Uniform();
            return t - 6.0;
        }
    }
}
