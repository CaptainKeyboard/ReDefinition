using System;

namespace ReDefinition.Atmospheres
{
    // The frame's share of the step offset of the layer march (E40 L2 E1; the shader's _DitherP.x and _DitherP.w). Every pixel keeps one spatial offset and every frame adds the
    // same phase to it, so that the history of the pass sees each pixel's offsets spread over the grid step. The sequence of phases decides how the history's mean of a pixel
    // errs: the golden-ratio sequence (layers.ditherPhase 1) turns the harmonics of order 5, 8, 13 of that error by only a few hundredths of a turn per frame, so they
    // stand nearly still for tens of frames and the pixel-fixed pattern of the offsets shows in the picture; the others give no harmonic that stands still.
    internal static class StepPhase
    {
        internal const int Golden = 1, RadicalInverse = 2, ShuffledStrata = 3, White = 4, GoldenBlocks = 5, Count = 5;
        internal const int Block = 8;
        private const double GoldenRatio = 0.6180339887498949;

        // The phase of a frame, in [0, 1), for the sequence (1 .. Count; others clamp).
        internal static double Of(int sequence, int frame)
        {
            if (frame < 0) frame = 0;
            switch (Math.Max(1, Math.Min(Count, sequence)))
            {
                case RadicalInverse:
                    return Radical(frame);
                case ShuffledStrata:
                {
                    // A random order of the eight strata of a block, and a random place inside the stratum, both from the block's hash: every run of eight aligned frames has one
                    // phase in each eighth of the step.
                    int block = frame / Block, i = frame % Block;
                    return (StratumOf(block, i) + Unit(Mix((uint)frame, 0x9E3779B9u))) / Block;
                }
                case White:
                    return Unit(Mix((uint)frame, 0x85EBCA6Bu));
                case GoldenBlocks:
                {
                    // The golden sequence inside a block of eight frames, shifted by a random amount per block: the slow harmonics do not carry from one block to the next.
                    int block = frame / Block, i = frame % Block;
                    return Fraction(i * GoldenRatio + Unit(Mix((uint)block, 0xC2B2AE35u)));
                }
                default:
                    return Fraction(frame * GoldenRatio);
            }
        }

        // The per-frame argument of the shader's gradient noise (Ign(p, w) shifts the pixel by 5.588 w in x and y): the pattern moves by a step that does not repeat. 0 = still.
        // The frame is taken modulo 4096 so that the float keeps its fraction at long sessions.
        internal static float Shift(int frame, float scale)
        {
            if (scale == 0f) return 0f;
            return (float)((frame & 4095) * (double)scale);
        }

        private static double Fraction(double x)
        {
            return x - Math.Floor(x);
        }

        // The van der Corput sequence in base 2: the bits of the frame number mirrored about the point.
        private static double Radical(int frame)
        {
            uint n = (uint)frame;
            n = (n << 16) | (n >> 16);
            n = ((n & 0x00FF00FFu) << 8) | ((n & 0xFF00FF00u) >> 8);
            n = ((n & 0x0F0F0F0Fu) << 4) | ((n & 0xF0F0F0F0u) >> 4);
            n = ((n & 0x33333333u) << 2) | ((n & 0xCCCCCCCCu) >> 2);
            n = ((n & 0x55555555u) << 1) | ((n & 0xAAAAAAAAu) >> 1);
            return n * (1.0 / 4294967296.0);
        }

        // The stratum that position i of a block takes: the order a * i + b (mod 8) with a odd and b from the block's hash, one of 32 permutations of 0 .. 7 (no allocation).
        private static int StratumOf(int block, int i)
        {
            uint h = Mix((uint)block, 0x27D4EB2Fu);
            int a = (int)(h & 3u) * 2 + 1;
            int b = (int)((h >> 2) & 7u);
            return (a * i + b) % Block;
        }

        private static uint Mix(uint x, uint salt)
        {
            uint h = x * 0x9E3779B1u ^ salt;
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h;
        }

        private static double Unit(uint h)
        {
            return (h >> 8) * (1.0 / 16777216.0);
        }
    }
}
