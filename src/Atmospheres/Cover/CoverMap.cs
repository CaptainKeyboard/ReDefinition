using System;

namespace ReDefinition.Atmospheres
{
    // The field's surface layer of one body resampled to an equirectangular R16 map of 1024 x 512 texels (1 MB), row 0 at the north pole,
    // column 0 at longitude -180, the same layout as the field's WeatherMap. The value is the depth over the layer's full depth
    // (CoverDepth.Quanta = full). The map is cut into tiles of 64 x 64 texels (16 x 8 tiles); every tile has a version number that rises
    // when its texels moved by more than the change threshold, so that a repainter touches only the quads under a changed tile.
    //
    // A map is built on a worker thread from a copy of the field's values (Resample) and handed over as a whole (Publish); readers on
    // the main thread take the current Frame, which is never written again. Pure numbers, no engine type.
    internal sealed class CoverMap
    {
        internal const int Width = 1024;
        internal const int Height = 512;
        internal const int TileSize = 64;
        internal const int TilesX = Width / TileSize;
        internal const int TilesY = Height / TileSize;

        // A tile counts as changed when a texel moved by at least this much of the full depth (1/200: below the step of an 8-bit colour).
        internal const double ChangeThreshold = 0.005;

        // One finished state of the map: never written after it was published.
        internal sealed class Frame
        {
            internal readonly ushort[] Texels;
            internal readonly int[] TileVersions;
            // The version of the field the frame was made from, and the field's time.
            internal readonly long SourceVersion;
            internal readonly double Ut;
            // The largest value of the frame, 0 to 65535: 0 means the whole body is bare.
            internal readonly ushort Peak;
            // The number of the frame (rises with each publish); a tile's version is the frame number that last changed it.
            internal readonly int Number;
            // The size of the field's map the frame was made from. The texels lie on the lattice of that map's texel centres refined to
            // Width x Height (texel i is the source at coordinate i x sourceWidth / Width, the centres at whole numbers), so that a
            // bilinear read of the frame gives the source's own bilinear value at every place, also at its steps.
            internal readonly int SourceWidth;
            internal readonly int SourceHeight;

            internal Frame(ushort[] texels, int[] tileVersions, long sourceVersion, double ut, ushort peak, int number, int sourceWidth, int sourceHeight)
            {
                Texels = texels;
                TileVersions = tileVersions;
                SourceVersion = sourceVersion;
                Ut = ut;
                Peak = peak;
                Number = number;
                SourceWidth = sourceWidth;
                SourceHeight = sourceHeight;
            }

            // The depth 0 to 1 at a place, bilinear, the longitude wrapped, the latitude clamped.
            internal double Sample01(double latDeg, double lonDeg)
            {
                double x = ((lonDeg + 180.0) / 360.0 * SourceWidth - 0.5) * Width / SourceWidth;
                double y = ((90.0 - latDeg) / 180.0 * SourceHeight - 0.5) * Height / SourceHeight;
                if (y < 0) y = 0;
                if (y > Height - 1) y = Height - 1;
                double x0 = Math.Floor(x);
                double fx = x - x0;
                int ix0 = Wrap((int)x0, Width), ix1 = Wrap((int)x0 + 1, Width);
                int iy0 = (int)Math.Floor(y), iy1 = Math.Min(iy0 + 1, Height - 1);
                double fy = y - iy0;
                double top = Texels[iy0 * Width + ix0] * (1 - fx) + Texels[iy0 * Width + ix1] * fx;
                double bottom = Texels[iy1 * Width + ix0] * (1 - fx) + Texels[iy1 * Width + ix1] * fx;
                return (top * (1 - fy) + bottom * fy) / CoverDepth.Quanta;
            }
        }

        private readonly object gate = new object();
        private Frame current;
        private int numbers;

        // The frame now; an empty one (all bare, no source) before the first publish.
        internal Frame Current
        {
            get
            {
                lock (gate) return current ?? (current = new Frame(new ushort[Width * Height], new int[TilesX * TilesY], -1, double.NaN, 0, 0, 256, 128));
            }
        }

        // The version of the field behind the current frame (-1 before the first one).
        internal long SourceVersion
        {
            get { return Current.SourceVersion; }
        }

        // Resamples the field's values (values x width x height, metres of depth, row 0 at the north pole) to a new frame against the
        // current one: each texel is the bilinear value over maxDepthM; a tile whose texels all moved less than ChangeThreshold keeps the
        // old texels and the old version. Safe on any thread; call Publish with the result on the main thread (or any thread).
        internal Frame Resample(float[] values, int width, int height, float maxDepthM, long sourceVersion, double ut)
        {
            Frame old = Current;
            var texels = new ushort[Width * Height];
            var versions = (int[])old.TileVersions.Clone();
            int number;
            lock (gate) number = ++numbers;
            if (values == null || width < 1 || height < 1 || values.Length < width * height || !(maxDepthM > 0f))
            {
                // No field map: the layer is bare everywhere. Tiles that held cover change.
                MarkChanged(old.Texels, texels, versions, number);
                return new Frame(texels, versions, sourceVersion, ut, 0, number, old.SourceWidth, old.SourceHeight);
            }
            // Pass 1: the source rows, cleaned of bad cells (not a number, negative) and in the unit of the full depth, stretched to
            // Width columns. Texel i reads the source at coordinate i x width / Width (the centres of the source at whole numbers).
            float scale = CoverDepth.Quanta / maxDepthM;
            var x0 = new int[Width];
            var x1 = new int[Width];
            var fx = new float[Width];
            for (int i = 0; i < Width; i++)
            {
                double sx = (double)i * width / Width;
                int f = (int)Math.Floor(sx);
                fx[i] = (float)(sx - f);
                x0[i] = Wrap(f, width);
                x1[i] = Wrap(f + 1, width);
            }
            var wide = new float[height][];
            for (int y = 0; y < height; y++)
            {
                var row = new float[Width];
                int at = y * width;
                for (int i = 0; i < Width; i++)
                {
                    float a = values[at + x0[i]], b = values[at + x1[i]];
                    if (!(a > 0f) || float.IsInfinity(a)) a = 0f;
                    if (!(b > 0f) || float.IsInfinity(b)) b = 0f;
                    row[i] = (a + (b - a) * fx[i]) * scale;
                }
                wide[y] = row;
            }
            // Pass 2: the rows between them. Texel j reads the source row at coordinate j x height / Height (clamped at the last row).
            ushort peak = 0;
            for (int j = 0; j < Height; j++)
            {
                double sy = (double)j * height / Height;
                int y0 = (int)Math.Floor(sy);
                if (y0 > height - 1) y0 = height - 1;
                int y1 = Math.Min(y0 + 1, height - 1);
                float fy = (float)(sy - y0);
                if (y0 == height - 1) fy = 0f;
                float[] r0 = wide[y0], r1 = wide[y1];
                int outRow = j * Width;
                for (int i = 0; i < Width; i++)
                {
                    float v = r0[i] + (r1[i] - r0[i]) * fy;
                    ushort q = v <= 0f ? (ushort)0 : v >= CoverDepth.Quanta ? (ushort)CoverDepth.Quanta : (ushort)(v + 0.5f);
                    texels[outRow + i] = q;
                    if (q > peak) peak = q;
                }
            }
            MarkChanged(old.Texels, texels, versions, number);
            return new Frame(texels, versions, sourceVersion, ut, peak, number, width, height);
        }

        // Makes the frame the current one.
        internal void Publish(Frame frame)
        {
            if (frame == null) return;
            lock (gate) current = frame;
        }

        // Per tile: where no texel moved by the threshold, the old texels stay (so a slow drift does not repaint every tile at every
        // resample); where one did, the tile takes the new texels and the frame's number.
        private static void MarkChanged(ushort[] old, ushort[] fresh, int[] versions, int number)
        {
            int threshold = (int)Math.Ceiling(ChangeThreshold * CoverDepth.Quanta);
            for (int ty = 0; ty < TilesY; ty++)
            {
                for (int tx = 0; tx < TilesX; tx++)
                {
                    bool changed = false;
                    for (int j = ty * TileSize; j < (ty + 1) * TileSize && !changed; j++)
                    {
                        int at = j * Width + tx * TileSize;
                        for (int i = 0; i < TileSize; i++)
                        {
                            if (Math.Abs(fresh[at + i] - old[at + i]) >= threshold)
                            {
                                changed = true;
                                break;
                            }
                        }
                    }
                    if (changed)
                    {
                        versions[ty * TilesX + tx] = number;
                        continue;
                    }
                    for (int j = ty * TileSize; j < (ty + 1) * TileSize; j++)
                        Array.Copy(old, j * Width + tx * TileSize, fresh, j * Width + tx * TileSize, TileSize);
                }
            }
        }

        private static int Wrap(int value, int size)
        {
            int r = value % size;
            return r < 0 ? r + size : r;
        }

        // The tile (column, row) a place falls in.
        internal static void TileOf(double latDeg, double lonDeg, out int tx, out int ty)
        {
            double x = (lonDeg + 180.0) / 360.0 * TilesX;
            double y = (90.0 - latDeg) / 180.0 * TilesY;
            tx = Wrap((int)Math.Floor(x), TilesX);
            ty = Math.Max(0, Math.Min(TilesY - 1, (int)Math.Floor(y)));
        }

        // The largest tile version among the tiles that a rectangle of latitude and longitude (degrees, west to east, may cross the
        // antimeridian when lonMax < lonMin) touches; 0 where none was ever changed.
        internal static int MaxVersion(int[] tileVersions, double latMin, double latMax, double lonMin, double lonMax)
        {
            // The rectangle grows by the texels a bilinear read reaches into (the lattice of the frame is a half source texel off the
            // tiles' own), so that a texel just over a tile's edge still counts.
            const double MarginTexels = 2.5;
            latMin -= MarginTexels * 180.0 / Height;
            latMax += MarginTexels * 180.0 / Height;
            if (lonMax >= lonMin)
            {
                lonMin -= MarginTexels * 360.0 / Width;
                lonMax += MarginTexels * 360.0 / Width;
                if (lonMax - lonMin >= 360.0)
                {
                    lonMin = -180.0;
                    lonMax = 180.0 - 1e-6;
                }
            }
            if (lonMin < -180.0) lonMin += 360.0;
            if (lonMax > 180.0) lonMax -= 360.0;
            if (latMin < -90.0) latMin = -90.0;
            if (latMax > 90.0) latMax = 90.0;
            int best = 0;
            int txA, tyA, txB, tyB;
            TileOf(latMax, lonMin, out txA, out tyA);
            TileOf(latMin, lonMax, out txB, out tyB);
            int count = txB >= txA ? txB - txA + 1 : TilesX - txA + txB + 1;
            if (lonMax - lonMin >= 360.0) count = TilesX;
            for (int ty = tyA; ty <= tyB; ty++)
            {
                for (int k = 0; k < count; k++)
                {
                    int tx = (txA + k) % TilesX;
                    int v = tileVersions[ty * TilesX + tx];
                    if (v > best) best = v;
                }
            }
            return best;
        }
    }
}
