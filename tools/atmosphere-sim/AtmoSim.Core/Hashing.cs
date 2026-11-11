using System;

namespace AtmoSim.Core
{
    // Integer hashing for the seeded parts of the generator (system generations, lattice cells, synthetic maps). Only
    // unsigned 64-bit multiplications, additions, xors and shifts: identical on every platform. The mixer is a
    // multiply-xorshift construction with constants chosen for this project; its avalanche is checked by a unit test
    // (every input bit flips every output bit with a probability within 0.02 of one half).
    public static class Hash
    {
        private const ulong M1 = 0xD9B4E019A7F3C56BUL, M2 = 0xA3C59AC2D5E7F41BUL, K1 = 0xE7037ED1A0B428DBUL, K2 = 0x8EBC6AF09C88C6E3UL, K3 = 0x589965CC75374CC3UL;

        public static ulong Mix(ulong h)
        {
            h ^= h >> 29; h *= M1;
            h ^= h >> 32; h *= M2;
            h ^= h >> 28;
            return h;
        }

        public static ulong Combine(ulong h, long v)
        {
            return Mix((h ^ ((ulong)v * K1)) + K2 + (h << 7));
        }

        public static ulong Of(ulong seed, long a, long b, long c)
        {
            return Combine(Combine(Combine(Mix(seed ^ K3), a), b), c);
        }

        // 53 bits to a double in [0, 1).
        public static double Unit(ulong h) { return (h >> 11) * (1.0 / 9007199254740992.0); }

        // The next draw of a chain: stream k of a hash.
        public static double Draw(ulong h, int k) { return Unit(Mix(h + (ulong)(k + 1) * K2)); }

        // Folds a double into a hash by its bit pattern (for result hashes in the determinism tests).
        public static ulong Fold(ulong h, double v)
        {
            return Mix(h ^ (ulong)BitConverter.DoubleToInt64Bits(v) ^ K3);
        }
    }

    // A small deterministic counter-based generator (the state advances by an odd constant, the output is the mixed state), used for the synthetic maps.
    public sealed class Rng
    {
        private ulong s;
        public Rng(ulong seed) { s = Hash.Mix(seed ^ 0x5851F42D4C957F2DUL); }
        public ulong Next()
        {
            s += 0xB5AD4ECEDA1CE2A9UL;
            return Hash.Mix(s);
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
