using System;

namespace ReDefinition.Core
{
    // A map of a whole body that a field fills: equirectangular, row 0 at the north pole, column 0 at longitude -180. The consumer
    // keeps one instance and passes it to every GetMap, so a read allocates only when the size changes.
    public sealed class WeatherMap
    {
        // The size in texels.
        public int Width;
        public int Height;
        // The values, row by row (Width x Height), in the unit of the map kind.
        public float[] Values = new float[0];
        // The same values as bytes from 0 to 255, filled by PackBytes where a consumer needs an R8 texture.
        public byte[] Bytes = new byte[0];
        // What the map shows, and for which body and time.
        public WeatherMapKind Kind;
        public int BodyIndex;
        public double Ut;
        // The field's Version when the map was filled.
        public long Version;
        public FieldReadiness Readiness;
        public bool Approximate;

        // Sets the size; the arrays are reallocated only when it differs.
        public void Resize(int width, int height)
        {
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException("width");
            if (Width != width || Height != height || Values.Length != width * height)
            {
                Width = width;
                Height = height;
                Values = new float[width * height];
                Bytes = new byte[0];
            }
        }

        // The value at a place, bilinear, with the longitude wrapped and the latitude clamped.
        public float Sample(double latDeg, double lonDeg)
        {
            if (Values.Length != Width * Height || Width < 1) return 0f;
            double x = (lonDeg + 180.0) / 360.0 * Width - 0.5;
            double y = (90.0 - latDeg) / 180.0 * Height - 0.5;
            if (y < 0) y = 0;
            if (y > Height - 1) y = Height - 1;
            double x0 = Math.Floor(x);
            double fx = x - x0;
            int ix0 = Wrap((int)x0, Width), ix1 = Wrap((int)x0 + 1, Width);
            int iy0 = (int)Math.Floor(y), iy1 = Math.Min(iy0 + 1, Height - 1);
            double fy = y - iy0;
            double top = Values[iy0 * Width + ix0] * (1 - fx) + Values[iy0 * Width + ix1] * fx;
            double bottom = Values[iy1 * Width + ix0] * (1 - fx) + Values[iy1 * Width + ix1] * fx;
            return (float)(top * (1 - fy) + bottom * fy);
        }

        // Fills Bytes from Values, mapping min..max to 0..255 (clamped).
        public void PackBytes(float min, float max)
        {
            if (Bytes.Length != Values.Length) Bytes = new byte[Values.Length];
            float scale = max > min ? 255f / (max - min) : 0f;
            for (int i = 0; i < Values.Length; i++)
            {
                float v = (Values[i] - min) * scale;
                Bytes[i] = v <= 0f ? (byte)0 : v >= 255f ? (byte)255 : (byte)(v + 0.5f);
            }
        }

        private static int Wrap(int i, int n)
        {
            int r = i % n;
            return r < 0 ? r + n : r;
        }
    }

    // A local 3D cut-out of a field around a place, for the volume pass: cloud water, updraft and temperature on a regular grid
    // in the local frame. The consumer keeps one instance and passes it to every FillWindow.
    public sealed class WeatherWindow
    {
        // The number of cells along east, north and up.
        public int Nx;
        public int Ny;
        public int Nz;
        // The place and radius it was filled for.
        public double CentreLatDeg;
        public double CentreLonDeg;
        public double RadiusKm;
        // The cell sizes in metres, and the altitude above sea level of the lowest layer's centre.
        public float SpacingHorizontalM;
        public float SpacingVerticalM;
        public float OriginAltM;
        // Cloud water in grams per cubic metre [VERIFY the unit in the simulation package], updraft in m/s, temperature in kelvin;
        // index = (iz * Ny + iy) * Nx + ix.
        public float[] CloudWater = new float[0];
        public float[] Updraft = new float[0];
        public float[] Temperature = new float[0];
        public int BodyIndex;
        public double Ut;
        // The field's Version when the window was filled.
        public long Version;
        public FieldReadiness Readiness;
        public bool Approximate;

        // The number of cells.
        public int Count
        {
            get { return Nx * Ny * Nz; }
        }

        // Sets the dimensions; the arrays are reallocated only when the cell count changes.
        public void Resize(int nx, int ny, int nz)
        {
            if (nx < 1 || ny < 1 || nz < 1) throw new ArgumentOutOfRangeException("nx");
            Nx = nx;
            Ny = ny;
            Nz = nz;
            int n = nx * ny * nz;
            if (CloudWater.Length != n)
            {
                CloudWater = new float[n];
                Updraft = new float[n];
                Temperature = new float[n];
            }
        }

        // The array index of a cell.
        public int Index(int ix, int iy, int iz)
        {
            return (iz * Ny + iy) * Nx + ix;
        }
    }
}
