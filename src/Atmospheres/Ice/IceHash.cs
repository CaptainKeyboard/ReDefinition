using System;

namespace ReDefinition.Atmospheres
{
    // The integer hash and the cell noises of the sea ice, written so that the shader ReDefinitionAtmospheresIceOverlay.shader repeats
    // them operation by operation: the same constants, the same order, 32-bit unsigned arithmetic that wraps, 24-bit integers turned into
    // floats (exact). The orbit picture (a worker thread, CPU) and the near-ground overlay (GPU) therefore draw one pattern. The tests
    // compare the integer results exactly and the float results within a few ulp (IceHashTests); the series probe compares the GPU's
    // values with these on a table of points (rows D35.hash_*).
    //
    // The 32-bit mixer is lowbias32 of Chris Wellons' hash-prospector (The Unlicense, https://github.com/skeeto/hash-prospector), the
    // same mixer as PrecipMath.Hash; docs/credits-and-licences.md lists this use. A lattice cell is hashed by chaining the mixer over
    // z, y and x (no multiplier constants besides the mixer's). The salts are fractional parts of the golden ratio (JitterYSalt:
    // 0x9E3779B9; JitterZSalt: the low word of the 64-bit form 0x9E3779B97F4A7C15) and of the square roots of 2 and 3 (ValueSalt,
    // DitherSalt). The Worley cells and the value noise are the textbook forms (F1/F2 over the 27 cells around a point, a jittered
    // feature point per cell; trilinear value noise with a smoothstep weight).
    internal static class IceHash
    {
        // The constants the shader repeats (a test reads them from the shader text).
        internal const uint MixMultiplierA = 0x7feb352du;
        internal const uint MixMultiplierB = 0x846ca68bu;
        internal const uint JitterYSalt = 0x9E3779B9u;
        internal const uint JitterZSalt = 0x7F4A7C15u;
        internal const uint ValueSalt = 0x6A09E667u;
        internal const uint DitherSalt = 0xBB67AE85u;

        // The feature point of a cell lies in JitterLow .. JitterLow + JitterSpan of the cell, so that two points are never nearly equal.
        internal const float JitterLow = 0.1f;
        internal const float JitterSpan = 0.8f;

        // The lowbias32 mixer of 32 bits.
        internal static uint Mix(uint v)
        {
            unchecked
            {
                v ^= v >> 16;
                v *= MixMultiplierA;
                v ^= v >> 15;
                v *= MixMultiplierB;
                v ^= v >> 16;
                return v;
            }
        }

        // The hash of a cell of the integer lattice, with a seed.
        internal static uint Cell(int x, int y, int z, uint seed)
        {
            unchecked
            {
                return Mix(Mix(Mix(seed + (uint)z) + (uint)y) + (uint)x);
            }
        }

        // The top 24 bits of a hash as a number in [0, 1): exact in a float.
        internal static float Unit(uint h)
        {
            return (h >> 8) * (1f / 16777216f);
        }

        // The result of one Worley evaluation: the distances to the nearest and the second nearest feature point (in cell units), the
        // hash of the nearest cell and a uniform number in [0, 1) that belongs to that cell.
        internal struct Worley
        {
            internal float F1;
            internal float F2;
            internal uint CellHash;
            internal float CellValue;
        }

        // The Worley noise at a point in cell units.
        internal static Worley WorleyAt(float px, float py, float pz, uint seed)
        {
            int ix = (int)Math.Floor(px), iy = (int)Math.Floor(py), iz = (int)Math.Floor(pz);
            float f1 = 1e9f, f2 = 1e9f;
            uint nearest = 0u;
            for (int dz = -1; dz <= 1; dz++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int cx = ix + dx, cy = iy + dy, cz = iz + dz;
                        uint h = Cell(cx, cy, cz, seed);
                        float jx = JitterLow + JitterSpan * Unit(h);
                        float jy = JitterLow + JitterSpan * Unit(Mix(h + JitterYSalt));
                        float jz = JitterLow + JitterSpan * Unit(Mix(h + JitterZSalt));
                        float qx = cx + jx - px, qy = cy + jy - py, qz = cz + jz - pz;
                        float d = (float)Math.Sqrt(qx * qx + qy * qy + qz * qz);
                        if (d < f1)
                        {
                            f2 = f1;
                            f1 = d;
                            nearest = h;
                        }
                        else if (d < f2)
                        {
                            f2 = d;
                        }
                    }
            return new Worley { F1 = f1, F2 = f2, CellHash = nearest, CellValue = Unit(Mix(nearest ^ ValueSalt)) };
        }

        // Trilinear value noise from -1 to 1 at a point in cell units, with the smoothstep weight.
        internal static float ValueNoise(float px, float py, float pz, uint seed)
        {
            float fx = (float)Math.Floor(px), fy = (float)Math.Floor(py), fz = (float)Math.Floor(pz);
            int ix = (int)fx, iy = (int)fy, iz = (int)fz;
            float tx = px - fx, ty = py - fy, tz = pz - fz;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            tz = tz * tz * (3f - 2f * tz);
            float c000 = Corner(ix, iy, iz, seed), c100 = Corner(ix + 1, iy, iz, seed);
            float c010 = Corner(ix, iy + 1, iz, seed), c110 = Corner(ix + 1, iy + 1, iz, seed);
            float c001 = Corner(ix, iy, iz + 1, seed), c101 = Corner(ix + 1, iy, iz + 1, seed);
            float c011 = Corner(ix, iy + 1, iz + 1, seed), c111 = Corner(ix + 1, iy + 1, iz + 1, seed);
            float x00 = c000 + (c100 - c000) * tx, x10 = c010 + (c110 - c010) * tx;
            float x01 = c001 + (c101 - c001) * tx, x11 = c011 + (c111 - c011) * tx;
            float y0 = x00 + (x10 - x00) * ty, y1 = x01 + (x11 - x01) * ty;
            return y0 + (y1 - y0) * tz;
        }

        private static float Corner(int x, int y, int z, uint seed)
        {
            return Unit(Cell(x, y, z, seed)) * 2f - 1f;
        }

        // A value in [0, 1) for a lattice cell of a world position (the dither of the overlay for thin ice): the shader floors its world
        // position to a lattice of that size and hashes the cell.
        internal static float Dither(int x, int y, int z, uint seed)
        {
            return Unit(Cell(x, y, z, seed ^ DitherSalt));
        }
    }
}
