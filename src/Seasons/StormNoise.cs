using System;
using static ReDefinition.Seasons.Mathx;

namespace ReDefinition.Seasons
{
    // Tileable value noise for the textures of the storm volume (StormVolume). Pure functions of the coordinates, so that the same
    // picture comes out every time and a texture repeats without a seam.
    internal static class StormNoise
    {
        // The noise at (x, y), 0 to 1, on a lattice of period x period cells that repeats: Value(x + period, y) == Value(x, y).
        internal static float Value(float x, float y, int period, int seed)
        {
            if (period < 1) period = 1;
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            float fx = x - x0, fy = y - y0;
            float sx = fx * fx * (3f - 2f * fx), sy = fy * fy * (3f - 2f * fy);
            float a = Lattice(x0, y0, period, seed), b = Lattice(x0 + 1, y0, period, seed);
            float c = Lattice(x0, y0 + 1, period, seed), d = Lattice(x0 + 1, y0 + 1, period, seed);
            float top = a + (b - a) * sx, bottom = c + (d - c) * sx;
            return top + (bottom - top) * sy;
        }

        // Several octaves of Value, each with twice the frequency and half the weight, brought to 0 to 1. Repeats with the period.
        internal static float Fbm(float x, float y, int period, int octaves, int seed)
        {
            float sum = 0, weight = 0.5f, norm = 0;
            int p = period;
            for (int o = 0; o < octaves; o++)
            {
                sum += weight * Value(x, y, p, seed + o * 31);
                norm += weight;
                x *= 2f;
                y *= 2f;
                p *= 2;
                weight *= 0.5f;
            }
            return norm > 0 ? sum / norm : 0f;
        }

        // The alpha channel of a square cloud texture that tiles: fbm bent so that there are gaps and thick parts (values 0 to 255).
        internal static byte[] CloudAlpha(int size, int period, int octaves, int seed)
        {
            var alpha = new byte[size * size];
            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    float n = Fbm(px / (float)size * period, py / (float)size * period, period, octaves, seed);
                    float v = Clamp01((n - 0.28f) * 2.1f);
                    alpha[py * size + px] = (byte)(v * 255f + 0.5f);
                }
            }
            return alpha;
        }

        // The alpha channel of a soft round puff: a disc that falls off to its edge, broken up by the noise (values 0 to 255). Does not tile.
        internal static byte[] PuffAlpha(int size, int seed)
        {
            var alpha = new byte[size * size];
            float half = (size - 1) * 0.5f;
            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    float dx = (px - half) / half, dy = (py - half) / half;
                    float d = (float)Math.Sqrt(dx * dx + dy * dy);
                    float disc = Clamp01(1f - d);
                    disc = disc * disc * (3f - 2f * disc);
                    float n = Fbm(px / (float)size * 4f, py / (float)size * 4f, 4, 3, seed);
                    float v = disc * Clamp01(0.75f + 0.5f * n);
                    alpha[py * size + px] = (byte)(Clamp01(v) * 255f + 0.5f);
                }
            }
            return alpha;
        }

        // The noise at (x, y, z), 0 to 1, on a lattice of period^3 cells that repeats in every axis.
        internal static float Value3(float x, float y, float z, int period, int seed)
        {
            if (period < 1) period = 1;
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y), z0 = (int)Math.Floor(z);
            float fx = x - x0, fy = y - y0, fz = z - z0;
            float sx = fx * fx * (3f - 2f * fx), sy = fy * fy * (3f - 2f * fy), sz = fz * fz * (3f - 2f * fz);
            float c00 = Lerp(Lattice3(x0, y0, z0, period, seed), Lattice3(x0 + 1, y0, z0, period, seed), sx);
            float c10 = Lerp(Lattice3(x0, y0 + 1, z0, period, seed), Lattice3(x0 + 1, y0 + 1, z0, period, seed), sx);
            float c01 = Lerp(Lattice3(x0, y0, z0 + 1, period, seed), Lattice3(x0 + 1, y0, z0 + 1, period, seed), sx);
            float c11 = Lerp(Lattice3(x0, y0 + 1, z0 + 1, period, seed), Lattice3(x0 + 1, y0 + 1, z0 + 1, period, seed), sx);
            return Lerp(Lerp(c00, c10, sy), Lerp(c01, c11, sy), sz);
        }

        // Several octaves of Value3, brought to 0 to 1. Repeats with the period.
        internal static float Fbm3(float x, float y, float z, int period, int octaves, int seed)
        {
            float sum = 0, weight = 0.5f, norm = 0;
            int p = period;
            for (int o = 0; o < octaves; o++)
            {
                sum += weight * Value3(x, y, z, p, seed + o * 31);
                norm += weight;
                x *= 2f;
                y *= 2f;
                z *= 2f;
                p *= 2;
                weight *= 0.5f;
            }
            return norm > 0 ? sum / norm : 0f;
        }

        // The voxels of a cube of size^3 that tiles in all three axes (x fastest, then y, then z), values 0 to 255: Fbm3 stretched about
        // its middle so that the volume uses the range. The storm shader reads it at several scales.
        internal static byte[] Volume(int size, int period, int octaves, int seed)
        {
            var voxels = new byte[size * size * size];
            for (int z = 0; z < size; z++)
            {
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float n = Fbm3(x / (float)size * period, y / (float)size * period, z / (float)size * period, period, octaves, seed);
                        voxels[(z * size + y) * size + x] = (byte)(Clamp01((n - 0.5f) * 1.6f + 0.5f) * 255f + 0.5f);
                    }
                }
            }
            return voxels;
        }

        // A lattice value, 0 to 1, for a cell of the repeating 3D lattice.
        private static float Lattice3(int ix, int iy, int iz, int period, int seed)
        {
            int wx = ((ix % period) + period) % period, wy = ((iy % period) + period) % period, wz = ((iz % period) + period) % period;
            unchecked
            {
                uint h = (uint)(wx * 374761393 + wy * 668265263 + wz * 1103515245 + seed * 1274126177);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }

        // A lattice value, 0 to 1, for a cell of the repeating lattice.
        private static float Lattice(int ix, int iy, int period, int seed)
        {
            int wx = ((ix % period) + period) % period, wy = ((iy % period) + period) % period;
            unchecked
            {
                uint h = (uint)(wx * 374761393 + wy * 668265263 + seed * 1274126177);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }
    }
}
