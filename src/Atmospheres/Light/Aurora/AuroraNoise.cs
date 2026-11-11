using System;

namespace ReDefinition.Atmospheres
{
    // Package D38. The noise texture of the aurora shell: four tileable channels of value noise, generated from a fixed seed so that the
    // look is the same in every run. R is the low-frequency field that folds the curtains, G the mid-frequency brightness of the
    // ribbons, B the high-frequency stripes of the vertical rays and A a slow field for the pulsation. Pure (bytes only).
    internal static class AuroraNoise
    {
        internal const int DefaultSize = 128;

        // The lattice period (cells across the texture) and octave count of each channel.
        private static readonly int[] Cells = { 6, 16, 48, 10 };
        private static readonly int[] Octaves = { 3, 3, 2, 2 };

        // The texels, row by row, four bytes (RGBA) each.
        internal static byte[] Generate(int size, uint seed)
        {
            var bytes = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        float v = Fbm(x / (float)size, y / (float)size, Cells[c], Octaves[c], seed * 31u + (uint)c * 7919u);
                        bytes[(y * size + x) * 4 + c] = (byte)Math.Max(0, Math.Min(255, (int)(v * 255f + 0.5f)));
                    }
            return bytes;
        }

        // Octave sum 0 to 1 of the tileable noise at a point of the unit square.
        internal static float Fbm(float u, float v, int cells, int octaves, uint salt)
        {
            float sum = 0f, weight = 0.5f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += weight * Tile(u, v, cells << o, salt + (uint)o * 101u);
                norm += weight;
                weight *= 0.5f;
            }
            return sum / norm;
        }

        // One octave: value noise on a lattice of period cells that wraps at the edges of the unit square.
        internal static float Tile(float u, float v, int cells, uint salt)
        {
            float x = u * cells, y = v * cells;
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            float fx = x - ix, fy = y - iy;
            float sx = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f), sy = fy * fy * fy * (fy * (fy * 6f - 15f) + 10f);
            float a = Cell(ix, iy, cells, salt), b = Cell(ix + 1, iy, cells, salt), c = Cell(ix, iy + 1, cells, salt), d = Cell(ix + 1, iy + 1, cells, salt);
            return (a + (b - a) * sx) + ((c + (d - c) * sx) - (a + (b - a) * sx)) * sy;
        }

        private static float Cell(int x, int y, int cells, uint salt)
        {
            int wx = ((x % cells) + cells) % cells, wy = ((y % cells) + cells) % cells;
            return AuroraModel.Lattice(wy * 4099L + wx, salt);
        }
    }
}
