using System;

namespace ReDefinition.Atmospheres
{
    // The coast and depth of a body's sea as a 1024 x 512 map, made once per load from the planet's heights (pure numbers, a worker
    // thread). Layout of the sphere's frame like OrbitMask (u = longitude / 2 pi from +z toward +x, v = latitude / pi + 0.5, row 0 at
    // the south pole), because the heights are read from the PQS in that frame. Four bytes a texel:
    //   R  the shore term 0..255: how early the water freezes, from the distance to the nearest land (a chamfer distance that knows the
    //      texel's length on the sphere) and the depth (shallow freezes first);
    //   G  the mean of R over the sea texels around (the footprint of one texel of the field's 256 x 128 map), so that the shore term
    //      minus its mean has zero mean over a field texel (IceMask keeps the field's fraction);
    //   B  255 on the sea, 0 on land;
    //   A  the depth term 0..255 (1 at the shore, 0 from 200 m down), for the look.
    // The distance is a two-pass chamfer transform (Borgefors) with the texel's length on the sphere as the step.
    internal static class ShoreGrid
    {
        internal const int Width = 1024;
        internal const int Height = 512;

        // The distance over which the coast term falls to 1/e, as a share of the body's radius (42 km on Kerbin).
        internal const float ShoreLengthRadii = 0.07f;
        // The weight of the coast term against the depth term in the shore term.
        internal const float ShoreShare = 0.65f;
        // The depth in metres from which the depth term is 0.
        internal const float DepthScaleM = 200f;
        // The half width in texels of the box that gives the mean (4: the footprint of a texel of the 256 x 128 field map).
        internal const int MeanRadius = 4;

        // Whether a height is sea: at or below the sea level of the body.
        internal static bool IsSea(float heightM, float seaLevelM)
        {
            return !float.IsNaN(heightM) && heightM <= seaLevelM;
        }

        // The map for a body: heights has Width * Height metres (NaN counts as land), seaLevelM is the ocean's height, radiusM the body's.
        internal static byte[] Build(float[] heights, float seaLevelM, float radiusM)
        {
            if (heights == null || heights.Length < Width * Height) throw new ArgumentException("heights", "the height grid is not " + Width + " x " + Height);
            int n = Width * Height;
            var sea = new bool[n];
            for (int i = 0; i < n; i++) sea[i] = IsSea(heights[i], seaLevelM);
            float[] dist = Distance(sea, radiusM);
            float length = Math.Max(1f, ShoreLengthRadii * radiusM);
            var term = new float[n];
            var depth = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (!sea[i]) continue;
                float d = dist[i];
                float coast = d >= 1e11f ? 0f : (float)Math.Exp(-d / length);
                float depthM = Math.Max(0f, seaLevelM - heights[i]);
                float t = Math.Min(1f, depthM / DepthScaleM);
                float shallow = 1f - t * t * (3f - 2f * t);
                depth[i] = shallow;
                term[i] = ShoreShare * coast + (1f - ShoreShare) * shallow;
            }
            float[] mean = MaskedMean(term, sea);
            var rgba = new byte[n * 4];
            for (int i = 0; i < n; i++)
            {
                if (!sea[i]) continue;
                rgba[i * 4] = ToByte(term[i]);
                rgba[i * 4 + 1] = ToByte(mean[i]);
                rgba[i * 4 + 2] = 255;
                rgba[i * 4 + 3] = ToByte(depth[i]);
            }
            return rgba;
        }

        private static byte ToByte(float v)
        {
            v = v < 0f ? 0f : v > 1f ? 1f : v;
            return (byte)(v * 255f + 0.5f);
        }

        // The distance in metres from every sea texel to the nearest land texel (0 on land, 1e12 where there is no land).
        internal static float[] Distance(bool[] sea, float radiusM)
        {
            const float Inf = 1e12f;
            int n = Width * Height;
            var d = new float[n];
            for (int i = 0; i < n; i++) d[i] = sea[i] ? Inf : 0f;
            float dy = radiusM * (float)Math.PI / Height;
            var dx = new float[Height];
            var diag = new float[Height];
            for (int row = 0; row < Height; row++)
            {
                double lat = ((row + 0.5) / Height - 0.5) * Math.PI;
                dx[row] = (float)(radiusM * Math.Cos(lat) * 2.0 * Math.PI / Width);
                diag[row] = (float)Math.Sqrt(dx[row] * dx[row] + dy * dy);
            }
            // Two rounds so that the wrap in longitude is seen.
            for (int round = 0; round < 2; round++)
            {
                for (int row = 0; row < Height; row++)
                    for (int col = 0; col < Width; col++)
                    {
                        int at = row * Width + col;
                        float best = d[at];
                        int left = row * Width + (col + Width - 1) % Width;
                        best = Math.Min(best, d[left] + dx[row]);
                        if (row > 0)
                        {
                            int below = (row - 1) * Width;
                            best = Math.Min(best, d[below + col] + dy);
                            best = Math.Min(best, d[below + (col + Width - 1) % Width] + diag[row]);
                            best = Math.Min(best, d[below + (col + 1) % Width] + diag[row]);
                        }
                        d[at] = best;
                    }
                for (int row = Height - 1; row >= 0; row--)
                    for (int col = Width - 1; col >= 0; col--)
                    {
                        int at = row * Width + col;
                        float best = d[at];
                        int right = row * Width + (col + 1) % Width;
                        best = Math.Min(best, d[right] + dx[row]);
                        if (row < Height - 1)
                        {
                            int above = (row + 1) * Width;
                            best = Math.Min(best, d[above + col] + dy);
                            best = Math.Min(best, d[above + (col + 1) % Width] + diag[row]);
                            best = Math.Min(best, d[above + (col + Width - 1) % Width] + diag[row]);
                        }
                        d[at] = best;
                    }
            }
            return d;
        }

        // The mean of value over the sea texels in a (2 MeanRadius + 1)-square box around each texel (longitude wraps, latitude is
        // cut); texels that are not sea contribute nothing. Two separable passes.
        internal static float[] MaskedMean(float[] value, bool[] sea)
        {
            int n = Width * Height;
            var sumH = new float[n];
            var cntH = new float[n];
            for (int row = 0; row < Height; row++)
                for (int col = 0; col < Width; col++)
                {
                    float s = 0f, c = 0f;
                    for (int k = -MeanRadius; k <= MeanRadius; k++)
                    {
                        int at = row * Width + (col + k + Width) % Width;
                        if (!sea[at]) continue;
                        s += value[at];
                        c += 1f;
                    }
                    sumH[row * Width + col] = s;
                    cntH[row * Width + col] = c;
                }
            var mean = new float[n];
            for (int row = 0; row < Height; row++)
                for (int col = 0; col < Width; col++)
                {
                    float s = 0f, c = 0f;
                    for (int k = -MeanRadius; k <= MeanRadius; k++)
                    {
                        int r = row + k;
                        if (r < 0 || r >= Height) continue;
                        s += sumH[r * Width + col];
                        c += cntH[r * Width + col];
                    }
                    mean[row * Width + col] = c > 0f ? s / c : 0f;
                }
            return mean;
        }

        // A channel (0 R, 1 G, 2 B, 3 A) of the map at texture coordinates u (wraps) and v (clamped), bilinear, 0 to 1: what the shader's
        // bilinear sample returns.
        internal static float Sample(byte[] rgba, int channel, double u, double v)
        {
            if (rgba == null || rgba.Length < Width * Height * 4) return 0f;
            double fx = u * Width - 0.5;
            double fy = v * Height - 0.5;
            if (fy < 0) fy = 0;
            if (fy > Height - 1) fy = Height - 1;
            int x0 = (int)Math.Floor(fx);
            int y0 = (int)Math.Floor(fy);
            double tx = fx - x0, ty = fy - y0;
            int x1 = Wrap(x0 + 1, Width);
            x0 = Wrap(x0, Width);
            int y1 = Math.Min(y0 + 1, Height - 1);
            double a = rgba[(y0 * Width + x0) * 4 + channel], b = rgba[(y0 * Width + x1) * 4 + channel];
            double c = rgba[(y1 * Width + x0) * 4 + channel], d = rgba[(y1 * Width + x1) * 4 + channel];
            return (float)(((a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty) / 255.0);
        }

        private static int Wrap(int value, int size)
        {
            int r = value % size;
            return r < 0 ? r + size : r;
        }

        // The texture coordinates of a unit direction in the sphere's frame.
        internal static void Uv(double x, double y, double z, out double u, out double v)
        {
            u = Math.Atan2(x, z) / (2.0 * Math.PI);
            if (u < 0) u += 1.0;
            double c = y < -1 ? -1 : y > 1 ? 1 : y;
            v = Math.Asin(c) / Math.PI + 0.5;
        }

        // Whether a direction is on the sea: the B channel above one half.
        internal static bool IsSeaAt(byte[] rgba, double x, double y, double z)
        {
            double u, v;
            Uv(x, y, z, out u, out v);
            return Sample(rgba, 2, u, v) > 0.5f;
        }
    }
}
