using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The procedural atlas of the camera frost: one 1024 x 1024 RGBA32 image (4 MB), painted in C# at start-up, no file shipped.
    //   R  frost mask: domain-warped fbm mixed with the value of a Voronoi cell, so that the edge of the frost breaks along
    //      crystal-like facets; tileable
    //   G  stuck flakes: a 4 x 4 grid of cells, each one an irregular soft-rimmed disc (the cell index is the flake's variant)
    //   B  relief normal x   (0.5 is flat)
    //   A  relief normal y   the relief is the same fbm plus the Voronoi cell borders as ridges
    // The image is painted at 128 x 128 and enlarged to 1024 by bilinear interpolation with wrapping: the detail the picture needs is
    // coarser than a texel of 128 (a tile is about 0.6 picture heights, so a texel is 5 pixels at 1080 lines), and the work stays under
    // 60 ms in the game's runtime. The build is pure (no engine call), so it can run on a worker thread; MakeTexture is the one engine call.
    // The hash is PrecipMath.Hash (lowbias32, credited there). Techniques: value-noise fbm, domain warping (I. Quilez,
    // iquilezles.org/articles/warp) and the F2 - F1 ridges of cellular noise (S. Worley, "A cellular texture basis function", 1996).
    internal static class FrostAtlas
    {
        internal const int Size = 1024;
        internal const int Bytes = Size * Size * 4;
        internal const int Work = 128;
        internal const int FlakeGrid = 4;
        internal const int DefaultSeed = 0x4652;

        // A random value 0..1 from a seed and two lattice coordinates.
        private static float Rand(uint seed, int a, int b, uint salt)
        {
            unchecked
            {
                uint h = PrecipMath.Hash(PrecipMath.Hash((uint)a * 0x9e3779b1u + salt) ^ ((uint)b * 0x85ebca6bu + seed));
                return (h >> 8) * (1f / 16777216f);
            }
        }

        // A periodic lattice of random values: the base of the tileable value noise.
        private sealed class Lattice
        {
            internal readonly int Period;
            internal readonly float[] Values;

            internal Lattice(int period, uint seed, uint salt)
            {
                Period = period;
                Values = new float[period * period];
                for (int y = 0; y < period; y++)
                    for (int x = 0; x < period; x++)
                        Values[y * period + x] = Rand(seed, x, y, salt);
            }

            internal float At(int ix, int iy)
            {
                return Values[(iy & (Period - 1)) * Period + (ix & (Period - 1))];
            }
        }

        private static float Fade(float t)
        {
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }

        // Tileable value noise at a point of the unit square (period = the lattice's own).
        private static float ValueNoise(Lattice lattice, float u, float v)
        {
            float x = u * lattice.Period, y = v * lattice.Period;
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            float fx = Fade(x - ix), fy = Fade(y - iy);
            float a = lattice.At(ix, iy), b = lattice.At(ix + 1, iy), c = lattice.At(ix, iy + 1), d = lattice.At(ix + 1, iy + 1);
            return (a + (b - a) * fx) + ((c + (d - c) * fx) - (a + (b - a) * fx)) * fy;
        }

        private static float Fbm(Lattice[] octaves, int count, float u, float v)
        {
            float sum = 0f, amp = 0.5f, norm = 0f;
            for (int o = 0; o < count; o++)
            {
                sum += amp * ValueNoise(octaves[o], u, v);
                norm += amp;
                amp *= 0.5f;
            }
            return sum / norm;
        }

        // The channels of the atlas as pixels, row 0 first, 4 bytes (R, G, B, A) each.
        internal static byte[] Build(int seed = DefaultSeed)
        {
            uint s = unchecked((uint)seed);
            const int W = Work;
            var octaves = new Lattice[5];
            for (int o = 0; o < octaves.Length; o++) octaves[o] = new Lattice(4 << o, s, 11u + (uint)o);
            var warpA = new Lattice(4, s, 31u);
            var warpB = new Lattice(8, s, 32u);
            var warpC = new Lattice(4, s, 33u);
            var warpD = new Lattice(8, s, 34u);

            // The Voronoi cells: one feature point and one value per cell of a periodic grid.
            const int Cells = 10;
            var px = new float[Cells * Cells];
            var py = new float[Cells * Cells];
            var pv = new float[Cells * Cells];
            for (int j = 0; j < Cells; j++)
                for (int i = 0; i < Cells; i++)
                {
                    px[j * Cells + i] = 0.15f + 0.7f * Rand(s, i, j, 41u);
                    py[j * Cells + i] = 0.15f + 0.7f * Rand(s, i, j, 42u);
                    pv[j * Cells + i] = Rand(s, i, j, 43u);
                }

            var height = new float[W * W];
            var mask = new float[W * W];
            for (int y = 0; y < W; y++)
            {
                float v = (y + 0.5f) / W;
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W;
                    // The domain warp: the point is pushed by two low-frequency noises.
                    float wu = u + 0.045f * (ValueNoise(warpA, u, v) + 0.5f * ValueNoise(warpB, u, v) - 0.75f);
                    float wv = v + 0.045f * (ValueNoise(warpC, u, v) + 0.5f * ValueNoise(warpD, u, v) - 0.75f);
                    float f = Fbm(octaves, 4, wu, wv);
                    f = FrostShape.Clamp01((f - 0.5f) * 2.4f + 0.5f);

                    // F1, F2 and the value of the nearest cell.
                    float gx = wu * Cells, gy = wv * Cells;
                    int ci = (int)Math.Floor(gx), cj = (int)Math.Floor(gy);
                    float f1 = 1e9f, f2 = 1e9f, nearest = 0f;
                    for (int dj = -1; dj <= 1; dj++)
                    {
                        for (int di = -1; di <= 1; di++)
                        {
                            int ni = ci + di, nj = cj + dj;
                            int wi = ((ni % Cells) + Cells) % Cells, wj = ((nj % Cells) + Cells) % Cells;
                            float dx = ni + px[wj * Cells + wi] - gx, dy = nj + py[wj * Cells + wi] - gy;
                            float d = dx * dx + dy * dy;
                            if (d < f1)
                            {
                                f2 = f1;
                                f1 = d;
                                nearest = pv[wj * Cells + wi];
                            }
                            else if (d < f2)
                            {
                                f2 = d;
                            }
                        }
                    }
                    float edge = (float)Math.Sqrt(f2) - (float)Math.Sqrt(f1);
                    float ridge = 1f - FrostShape.Smooth(0f, 0.12f, edge);
                    mask[y * W + x] = FrostShape.Clamp01(0.5f * f + 0.5f * nearest);
                    height[y * W + x] = 0.5f * f + 0.5f * ridge;
                }
            }

            // The work image: R mask, G flake, B/A relief normal.
            var work = new float[W * W * 4];
            const float Slope = 1.6f;
            for (int y = 0; y < W; y++)
            {
                int y0 = (y + W - 1) % W, y1 = (y + 1) % W;
                for (int x = 0; x < W; x++)
                {
                    int x0 = (x + W - 1) % W, x1 = (x + 1) % W;
                    float gxv = (height[y * W + x0] - height[y * W + x1]) * Slope;
                    float gyv = (height[y0 * W + x] - height[y1 * W + x]) * Slope;
                    int o = (y * W + x) * 4;
                    work[o] = mask[y * W + x];
                    work[o + 2] = FrostShape.Clamp01(0.5f + 0.5f * gxv);
                    work[o + 3] = FrostShape.Clamp01(0.5f + 0.5f * gyv);
                }
            }
            PaintFlakes(work, W, s);
            return Enlarge(work, W);
        }

        // The flakes of the G channel: cell k of the 4 x 4 grid holds a disc whose rim is lobed differently from the others.
        private static void PaintFlakes(float[] work, int w, uint seed)
        {
            int cell = w / FlakeGrid;
            for (int k = 0; k < FlakeGrid * FlakeGrid; k++)
            {
                int cx = (k % FlakeGrid) * cell, cy = (k / FlakeGrid) * cell;
                int lobes = 3 + (int)(Rand(seed, k, 1, 51u) * 4f);
                float depth = 0.06f + 0.10f * Rand(seed, k, 2, 52u);
                float phase = Rand(seed, k, 3, 53u) * 6.2831853f;
                float lobes2 = 2f * lobes + 1f;
                float depth2 = 0.03f * Rand(seed, k, 4, 54u);
                float squash = 0.88f + 0.12f * Rand(seed, k, 5, 55u);
                for (int y = 0; y < cell; y++)
                {
                    float fy = ((y + 0.5f) / cell * 2f - 1f) / squash;
                    for (int x = 0; x < cell; x++)
                    {
                        float fx = (x + 0.5f) / cell * 2f - 1f;
                        float r = (float)Math.Sqrt(fx * fx + fy * fy);
                        float theta = (float)Math.Atan2(fy, fx);
                        float radius = 0.78f + depth * (float)Math.Cos(lobes * theta + phase) + depth2 * (float)Math.Cos(lobes2 * theta - phase);
                        float rr = r / radius;
                        float a = 1f - FrostShape.Smooth(0.45f, 1f, rr);
                        work[((cy + y) * w + cx + x) * 4 + 1] = a;
                    }
                }
            }
        }

        // The work image enlarged four times in each direction by bilinear interpolation with wrapping (the sampler's own convention:
        // texel centres at +0.5). At a factor of four the fractions of the source position take only four values, 5/8, 7/8, 1/8 and 3/8,
        // so the work is done in integers: a horizontal pass into a wide buffer, then a vertical pass into the bytes.
        private static byte[] Enlarge(float[] work, int w)
        {
            int f = Size / w;
            if (w * f != Size || (f & 1) != 0 || f > 16) throw new InvalidOperationException("the enlargement is by an even factor up to 16");
            int denom = 2 * f;
            // Output pixel k of each group of f lies at source position g + (2k + 1 - f) / (2f): between the previous texel and this one
            // for the first half of the group, between this one and the next for the second. weight[k] is the share (of denom) of the
            // later texel of the pair.
            var weight = new int[f];
            for (int k = 0; k < f; k++) weight[k] = 2 * k + 1 < f ? f + 2 * k + 1 : 2 * k + 1 - f;
            var source = new byte[w * w * 4];
            for (int i = 0; i < source.Length; i++) source[i] = (byte)(work[i] * 255f + 0.5f);

            // Horizontal: every source row becomes Size values per channel, scaled by denom.
            var wide = new ushort[w * Size * 4];
            for (int y = 0; y < w; y++)
            {
                int row = y * w * 4, outRow = y * Size * 4;
                for (int x = 0; x < w; x++)
                {
                    int xp = (x + w - 1) % w, xn = (x + 1) % w;
                    int here = row + x * 4, before = row + xp * 4, after = row + xn * 4;
                    for (int k = 0; k < f; k++)
                    {
                        int first = 2 * k + 1 < f ? before : here, second = 2 * k + 1 < f ? here : after;
                        int p = weight[k];
                        int o = outRow + (x * f + k) * 4;
                        wide[o] = (ushort)(source[first] * (denom - p) + source[second] * p);
                        wide[o + 1] = (ushort)(source[first + 1] * (denom - p) + source[second + 1] * p);
                        wide[o + 2] = (ushort)(source[first + 2] * (denom - p) + source[second + 2] * p);
                        wide[o + 3] = (ushort)(source[first + 3] * (denom - p) + source[second + 3] * p);
                    }
                }
            }

            // Vertical: every output row mixes two rows of the wide buffer; the sum is scaled by denom squared.
            var pixels = new byte[Bytes];
            int rowValues = Size * 4;
            int total = denom * denom, half = total / 2, shift = 0;
            while ((1 << shift) < total) shift++;
            if ((1 << shift) != total) throw new InvalidOperationException("the factor is a power of two");
            for (int y = 0; y < Size; y++)
            {
                int g = y / f, k = y % f;
                int sa = 2 * k + 1 < f ? (g + w - 1) % w : g, sb = 2 * k + 1 < f ? g : (g + 1) % w;
                int p = weight[k];
                int a = sa * rowValues, b = sb * rowValues, o = y * rowValues;
                for (int i = 0; i < rowValues; i++)
                    pixels[o + i] = (byte)((wide[a + i] * (denom - p) + wide[b + i] * p + half) >> shift);
            }
            return pixels;
        }

        // Bilinear sample of the atlas with wrapping, as the sampler reads it (texel centres at +0.5). Channels come back 0 to 1. Used by
        // the tests to run the shader's arithmetic on the CPU.
        internal static void Sample(byte[] atlas, float u, float v, out float r, out float g, out float b, out float a)
        {
            float x = u * Size - 0.5f, y = v * Size - 0.5f;
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            float fx = x - ix, fy = y - iy;
            int xa = ((ix % Size) + Size) % Size, xb = (xa + 1) % Size;
            int ya = ((iy % Size) + Size) % Size, yb = (ya + 1) % Size;
            int pa = (ya * Size + xa) * 4, pb = (ya * Size + xb) * 4, pc = (yb * Size + xa) * 4, pd = (yb * Size + xb) * 4;
            r = Mix(atlas, pa, pb, pc, pd, 0, fx, fy);
            g = Mix(atlas, pa, pb, pc, pd, 1, fx, fy);
            b = Mix(atlas, pa, pb, pc, pd, 2, fx, fy);
            a = Mix(atlas, pa, pb, pc, pd, 3, fx, fy);
        }

        private static float Mix(byte[] p, int a, int b, int c, int d, int ch, float fx, float fy)
        {
            float top = p[a + ch] + (p[b + ch] - p[a + ch]) * fx;
            float bottom = p[c + ch] + (p[d + ch] - p[c + ch]) * fx;
            return (top + (bottom - top) * fy) / 255f;
        }

        // The atlas as a texture: RGBA32, repeating, bilinear, no mip chain (4 MB). The one engine call of this class.
        internal static Texture2D MakeTexture(byte[] pixels)
        {
            if (pixels == null || pixels.Length != Bytes) throw new ArgumentException("the atlas is " + Bytes + " bytes");
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false, true)
            {
                name = "ReDefinition Atmospheres frost atlas",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.LoadRawTextureData(pixels);
            texture.Apply(false, true);
            return texture;
        }
    }
}
