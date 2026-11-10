using System;

namespace ReDefinition.Seasons
{
    // The pure part of EveCellLayer: the equirectangular map of a cloud layer's coverage (the way EVE reads it) and the painting of storm
    // cells into it, as functions of numbers so that they can be tested without the game.
    //
    // EVE's coverage map is a 2D texture on the sphere of the layer. A point of the layer is a unit vector in the layer's frame: the
    // world position times CloudsRaymarchedVolume.CloudRotationMatrix, normalised. Its texture coordinates are
    //   u = 0.5 + atan2(x, z) / (2 pi),   v = acos(y) / pi
    // (TextureWrapper.GetEquirectangularUV in EVE Redux). Texel (i, j) has its centre at ((i + 0.5) / Width, (j + 0.5) / Height).
    // Row 0 is the bottom row of a Unity texture, which is v = 0, the pole where y = 1.
    internal static class EveCoverageMath
    {
        // The size of the map we paint, in texels, one byte (red) each. 4096 x 2048 is 0.92 km a texel on Kerbin.
        internal const int Width = 4096, Height = 2048;

        // The soft edge: full coverage up to this share of the radius, none from the radius on (before the ragged edge moves it).
        internal const double CoreShare = 0.45;

        // How far the ragged edge moves the rim, as a share of the radius, either way.
        internal const double Raggedness = 0.20;

        // Cells are painted up to this much beyond their radius, as a share, so that the ragged rim fits.
        internal const double ReachShare = 1.0 + Raggedness;

        // A cell as the painter takes it: the unit vector of its centre in the layer's frame, its radius in m, its strength (the peak
        // coverage, 0 to 1) and a seed for the ragged edge.
        internal struct Blob
        {
            internal double X, Y, Z;
            internal double RadiusM;
            internal double Peak;
            internal ulong Seed;
        }

        // A rectangle of texels; columns count from Col0 and wrap around the width (the date line), rows do not wrap.
        internal struct Rect
        {
            internal int Col0, Cols, Row0, Rows;

            internal bool IsEmpty
            {
                get { return Cols <= 0 || Rows <= 0; }
            }

            internal bool Contains(int col, int row)
            {
                if (row < Row0 || row >= Row0 + Rows) return false;
                if (Cols >= Width) return true;
                int d = col - Col0;
                if (d < 0) d += Width;
                return d < Cols;
            }
        }

        // The texture coordinates of a direction, EVE's formula. The vector need not be normalised.
        internal static void Uv(double x, double y, double z, out double u, out double v)
        {
            double length = Math.Sqrt(x * x + y * y + z * z);
            if (length <= 0)
            {
                u = 0.5;
                v = 0.5;
                return;
            }
            double c = y / length;
            v = Math.Acos(c < -1 ? -1 : c > 1 ? 1 : c) / Math.PI;
            u = 0.5 + Math.Atan2(x, z) / (2 * Math.PI);
        }

        // The unit vector of texture coordinates: the inverse of Uv.
        internal static void Direction(double u, double v, out double x, out double y, out double z)
        {
            double theta = v * Math.PI;
            double a = (u - 0.5) * 2 * Math.PI;
            double s = Math.Sin(theta);
            x = s * Math.Sin(a);
            y = Math.Cos(theta);
            z = s * Math.Cos(a);
        }

        // The texel that holds the coordinates (floor), kept inside the map: the column wraps, the row is clamped.
        internal static void Texel(double u, double v, out int col, out int row)
        {
            col = (int)Math.Floor(u * Width) % Width;
            if (col < 0) col += Width;
            row = (int)Math.Floor(v * Height);
            if (row < 0) row = 0;
            if (row >= Height) row = Height - 1;
        }

        // The cell's coverage at a distance from its centre: 1 inside the core, falling smoothly to 0 at the radius.
        // ragged, -1 to 1, moves the rim by up to Raggedness of the radius.
        internal static double Falloff(double distanceM, double radiusM, double ragged)
        {
            if (radiusM <= 0) return 0;
            double x = distanceM / radiusM + ragged * Raggedness;
            double t = (x - CoreShare) / (1.0 - CoreShare);
            if (t <= 0) return 1;
            if (t >= 1) return 0;
            return 1 - t * t * (3 - 2 * t);
        }

        // Deterministic noise of a texel and a seed, -1 to 1, smooth over about 8 and 3 texels (two octaves, 2:1), so that the rim of a
        // cell is ragged but the same every time it is painted.
        internal static double Ragged(int col, int row, ulong seed)
        {
            double a = Lattice(col / 8.0, row / 8.0, seed);
            double b = Lattice(col / 3.0, row / 3.0, seed ^ 0x9E3779B97F4A7C15UL);
            return a * 0.65 + b * 0.35;
        }

        private static double Lattice(double x, double y, ulong seed)
        {
            double fx = Math.Floor(x), fy = Math.Floor(y);
            double tx = x - fx, ty = y - fy;
            tx = tx * tx * (3 - 2 * tx);
            ty = ty * ty * (3 - 2 * ty);
            long ix = (long)fx, iy = (long)fy;
            double v00 = Hash(ix, iy, seed), v10 = Hash(ix + 1, iy, seed), v01 = Hash(ix, iy + 1, seed), v11 = Hash(ix + 1, iy + 1, seed);
            double top = v00 + (v10 - v00) * tx, bottom = v01 + (v11 - v01) * tx;
            return top + (bottom - top) * ty;
        }

        private static double Hash(long x, long y, ulong seed)
        {
            ulong h = seed + 0x9E3779B97F4A7C15UL;
            h ^= (ulong)x * 0xBF58476D1CE4E5B9UL;
            h = (h ^ (h >> 30)) * 0x94D049BB133111EBUL;
            h ^= (ulong)y * 0xD6E8FEB86659FD93UL;
            h = (h ^ (h >> 27)) * 0xBF58476D1CE4E5B9UL;
            h ^= h >> 31;
            return (h >> 11) * (2.0 / 9007199254740992.0) - 1.0;
        }

        // The rectangle of texels that holds the circle of angular radius around a direction (radians), with a margin of two texels.
        // The rows are the circle's; the columns are the width of the circle at its most poleward row, or all of them where the circle
        // reaches a pole or half the globe.
        internal static Rect RectAround(double x, double y, double z, double angularRadius)
        {
            double u, v;
            Uv(x, y, z, out u, out v);
            int rowHalf = (int)Math.Ceiling(angularRadius / Math.PI * Height) + 2;
            int centreRow = (int)Math.Floor(v * Height);
            int row0 = centreRow - rowHalf, row1 = centreRow + rowHalf;
            bool pole = row0 <= 0 || row1 >= Height - 1;
            if (row0 < 0) row0 = 0;
            if (row1 > Height - 1) row1 = Height - 1;
            var rect = new Rect { Row0 = row0, Rows = row1 - row0 + 1 };
            double poleward = Math.Max(Math.Abs(Math.PI / 2 - (row0 + 0.5) / Height * Math.PI), Math.Abs(Math.PI / 2 - (row1 + 0.5) / Height * Math.PI));
            double cosine = Math.Cos(poleward);
            if (pole || angularRadius >= Math.PI / 2 || cosine < 0.05)
            {
                rect.Col0 = 0;
                rect.Cols = Width;
                return rect;
            }
            double halfLon = angularRadius / cosine;
            if (halfLon >= Math.PI)
            {
                rect.Col0 = 0;
                rect.Cols = Width;
                return rect;
            }
            int colHalf = (int)Math.Ceiling(halfLon / (2 * Math.PI) * Width) + 2;
            if (colHalf * 2 + 1 >= Width)
            {
                rect.Col0 = 0;
                rect.Cols = Width;
                return rect;
            }
            int col = (int)Math.Floor(u * Width);
            int col0 = (col - colHalf) % Width;
            if (col0 < 0) col0 += Width;
            rect.Col0 = col0;
            rect.Cols = colHalf * 2 + 1;
            return rect;
        }

        // Sets the texels of a rectangle to zero.
        internal static void Clear(byte[] data, Rect rect)
        {
            for (int r = 0; r < rect.Rows; r++)
            {
                int rowStart = (rect.Row0 + r) * Width;
                for (int k = 0; k < rect.Cols; k++) data[rowStart + (rect.Col0 + k) % Width] = 0;
            }
        }

        // Paints one cell into the map (the larger value wins where cells overlap) and returns the rectangle it touched. The map is
        // Width x Height bytes, row 0 first. longitudeShift moves the cell along u (the corrective offset of the calibration).
        internal static Rect Paint(byte[] data, Blob blob, double planetRadiusM, double longitudeShift)
        {
            double cx = blob.X, cy = blob.Y, cz = blob.Z;
            double length = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            if (length <= 0 || blob.RadiusM <= 0 || blob.Peak <= 0 || planetRadiusM <= 0) return default(Rect);
            cx /= length; cy /= length; cz /= length;
            if (longitudeShift != 0)
            {
                double u0, v0;
                Uv(cx, cy, cz, out u0, out v0);
                Direction(u0 + longitudeShift, v0, out cx, out cy, out cz);
            }
            double reach = blob.RadiusM * ReachShare / planetRadiusM;
            Rect rect = RectAround(cx, cy, cz, reach);
            double cosReach = Math.Cos(Math.Min(reach, Math.PI));

            for (int r = 0; r < rect.Rows; r++)
            {
                int row = rect.Row0 + r;
                double theta = (row + 0.5) / Height * Math.PI;
                double sinTheta = Math.Sin(theta), cosTheta = Math.Cos(theta);
                double rowDot = cy * cosTheta;
                int rowStart = row * Width;
                for (int k = 0; k < rect.Cols; k++)
                {
                    int col = (rect.Col0 + k) % Width;
                    double a = ((col + 0.5) / Width - 0.5) * 2 * Math.PI;
                    double dot = rowDot + sinTheta * (cx * Math.Sin(a) + cz * Math.Cos(a));
                    if (dot < cosReach) continue;
                    double angle = Math.Acos(dot > 1 ? 1 : dot);
                    double ragged = Ragged(col, row, blob.Seed);
                    // The plateau is not flat: a slow swell in it lets the cloud's own noise open here and there.
                    double swell = 0.86 + 0.14 * ragged;
                    double coverage = Falloff(angle * planetRadiusM, blob.RadiusM, ragged) * blob.Peak * swell;
                    int value = (int)Math.Round(coverage * 255.0);
                    if (value > data[rowStart + col]) data[rowStart + col] = (byte)(value > 255 ? 255 : value);
                }
            }
            return rect;
        }

        // The bilinear value of the map at texture coordinates, 0 to 1, the way Texture2D.GetPixelBilinear reads it (texel centres at
        // half texels, the column wraps, the row is clamped).
        internal static double Sample(byte[] data, double u, double v)
        {
            double fx = u * Width - 0.5, fy = v * Height - 0.5;
            double x0 = Math.Floor(fx), y0 = Math.Floor(fy);
            double tx = fx - x0, ty = fy - y0;
            int c0 = (int)(((long)x0 % Width + Width) % Width), c1 = (c0 + 1) % Width;
            int r0 = Clamp((int)y0, 0, Height - 1), r1 = Clamp((int)y0 + 1, 0, Height - 1);
            double a = data[r0 * Width + c0], b = data[r0 * Width + c1], c = data[r1 * Width + c0], d = data[r1 * Width + c1];
            double top = a + (b - a) * tx, bottom = c + (d - c) * tx;
            return (top + (bottom - top) * ty) / 255.0;
        }

        private static int Clamp(int value, int low, int high)
        {
            return value < low ? low : value > high ? high : value;
        }
    }
}
