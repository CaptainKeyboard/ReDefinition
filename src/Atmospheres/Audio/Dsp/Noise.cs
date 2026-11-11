using System;
using System.Runtime.CompilerServices;

namespace ReDefinition.Atmospheres.Audio
{
    // Marsaglia xorshift32: the white noise source of every synthesiser here. One instance is one stream; the seed decides it.
    public struct Xorshift32
    {
        private uint state;

        public Xorshift32(uint seed)
        {
            state = Mix(seed);
        }

        // A stream seed derived from a base seed and a stream number, so that the streams of one mixer do not repeat each other.
        public static uint Derive(uint seed, int stream)
        {
            return Mix(seed + 0x9E3779B9u * (uint)(stream + 1));
        }

        private static uint Mix(uint x)
        {
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return x == 0 ? 0x6D2B79F5u : x;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint NextUInt()
        {
            uint x = state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            state = x;
            return x;
        }

        // Uniform in [-1, 1).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float NextSigned()
        {
            return (int)NextUInt() * (1.0f / 2147483648.0f);
        }

        // Uniform in [0, 1).
        public float NextUnit()
        {
            return UnitOf(NextUInt());
        }

        // A word of the stream as a number in [0, 1), and as one in [-1, 1).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float UnitOf(uint word)
        {
            return (word >> 8) * (1.0f / 16777216.0f);
        }

        public static float SignedOf(uint word)
        {
            return (int)word * (1.0f / 2147483648.0f);
        }
    }

    // Pink-ish noise by the Voss-McCartney scheme: seven rows of white noise, row k renewed every 2^k samples, plus a fresh white
    // value. Roughly -3 dB per octave; the output is scaled to about unit peak.
    public struct PinkNoise
    {
        private Xorshift32 rng;
        private float r0, r1, r2, r3, r4, r5, r6;
        private uint counter;

        public PinkNoise(uint seed)
        {
            rng = new Xorshift32(seed);
            r0 = r1 = r2 = r3 = r4 = r5 = r6 = 0f;
            counter = 0;
        }

        public float Next()
        {
            counter++;
            uint c = counter;
            float white = rng.NextSigned();
            // The index of the lowest set bit picks the row to renew.
            if ((c & 1u) != 0) r0 = rng.NextSigned();
            else if ((c & 2u) != 0) r1 = rng.NextSigned();
            else if ((c & 4u) != 0) r2 = rng.NextSigned();
            else if ((c & 8u) != 0) r3 = rng.NextSigned();
            else if ((c & 16u) != 0) r4 = rng.NextSigned();
            else if ((c & 32u) != 0) r5 = rng.NextSigned();
            else if ((c & 64u) != 0) r6 = rng.NextSigned();
            return (white + r0 + r1 + r2 + r3 + r4 + r5 + r6) * (1.0f / 8.0f);
        }
    }
}
