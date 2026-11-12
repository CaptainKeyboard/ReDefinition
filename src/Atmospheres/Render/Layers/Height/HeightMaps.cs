using System;

namespace ReDefinition.Atmospheres
{
    // The offsets in m at one place: the root offset (condensation level against the reference, and the size trend), the top term (the
    // updraft's share of the top) and the free variation.
    internal struct LocalHeights
    {
        public float RootM, TopM, VariationM;
    }

    // The height maps of one layer, equirectangular like the weather maps (row 0 at the north pole, column 0 at longitude -180), three
    // bytes per texel: the root offset, the top term and the variation. A byte b means (b - 128) / 127 times the channel's range in m, so
    // that 128 is exactly zero and the bilinear filter of the GPU reads the same numbers as Sample. The ranges travel with the maps.
    internal sealed class HeightMaps
    {
        internal HeightMaps(int width, int height, float rootRangeM, float topRangeM, float variationRangeM)
        {
            if (width < 2 || height < 2) throw new ArgumentOutOfRangeException("width");
            Width = width;
            Height = height;
            RootRangeM = rootRangeM;
            TopRangeM = topRangeM;
            VariationRangeM = variationRangeM;
            Bytes = new byte[width * height * 3];
            for (int i = 0; i < Bytes.Length; i++) Bytes[i] = 128;
        }

        internal int Width { get; }
        internal int Height { get; }
        internal float RootRangeM { get; }
        internal float TopRangeM { get; }
        internal float VariationRangeM { get; }
        // Width x Height x 3 bytes: root, top, variation.
        internal byte[] Bytes { get; }
        // The same texels as RGBA32 for the GPU (the fourth byte 255), made by MakeRgba on the worker so that the main thread only copies.
        internal byte[] Rgba { get; private set; }

        // Fills Rgba from Bytes (once; a second call keeps the first). Not for the main thread of the game: 131072 texels.
        internal byte[] MakeRgba()
        {
            byte[] rgba = Rgba;
            if (rgba != null) return rgba;
            int n = Width * Height;
            rgba = new byte[n * 4];
            for (int i = 0; i < n; i++)
            {
                rgba[i * 4] = Bytes[i * 3];
                rgba[i * 4 + 1] = Bytes[i * 3 + 1];
                rgba[i * 4 + 2] = Bytes[i * 3 + 2];
                rgba[i * 4 + 3] = 255;
            }
            Rgba = rgba;
            return rgba;
        }

        internal static byte Encode(double valueM, double rangeM)
        {
            if (!(rangeM > 0.0)) return 128;
            double v = 128.0 + Math.Round(valueM / rangeM * 127.0);
            return (byte)Math.Max(1.0, Math.Min(255.0, v));
        }

        internal static double Decode(byte b, double rangeM)
        {
            return (b - 128) / 127.0 * rangeM;
        }

        // The offsets at a place, bilinear like the GPU's filter (longitude wraps, latitude clamps).
        internal LocalHeights Sample(double latDeg, double lonDeg)
        {
            double x = (lonDeg + 180.0) / 360.0 * Width - 0.5;
            double y = (90.0 - latDeg) / 180.0 * Height - 0.5;
            if (y < 0) y = 0;
            if (y > Height - 1) y = Height - 1;
            double x0 = Math.Floor(x), y0 = Math.Floor(y);
            double fx = x - x0, fy = y - y0;
            int ix0 = Wrap((int)x0), ix1 = Wrap((int)x0 + 1);
            int iy0 = (int)y0, iy1 = Math.Min(iy0 + 1, Height - 1);
            double r = Mix(0, ix0, ix1, iy0, iy1, fx, fy), t = Mix(1, ix0, ix1, iy0, iy1, fx, fy), v = Mix(2, ix0, ix1, iy0, iy1, fx, fy);
            return new LocalHeights
            {
                RootM = (float)(r / 127.0 * RootRangeM),
                TopM = (float)(t / 127.0 * TopRangeM),
                VariationM = (float)(v / 127.0 * VariationRangeM)
            };
        }

        private int Wrap(int x)
        {
            int r = x % Width;
            return r < 0 ? r + Width : r;
        }

        private double Mix(int channel, int ix0, int ix1, int iy0, int iy1, double fx, double fy)
        {
            double a = Bytes[(iy0 * Width + ix0) * 3 + channel] - 128, b = Bytes[(iy0 * Width + ix1) * 3 + channel] - 128;
            double c = Bytes[(iy1 * Width + ix0) * 3 + channel] - 128, d = Bytes[(iy1 * Width + ix1) * 3 + channel] - 128;
            return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
        }
    }

    // The maps of the field a layer's heights are built from, all Width x Height and equirectangular; any may be null (that input then
    // contributes nothing: no humidity gives the reference spread, no updraft gives a top term of zero).
    internal sealed class HeightInputs
    {
        public int Width, Height;
        // Cloud cover 0 to 1 (the size trend reads its neighbourhood mean), the relative humidity 0 to 1, the air temperature in kelvin
        // (a constant DefaultTemperatureK where null) and the updraft in m/s at the height of the cells.
        public float[] Coverage, Humidity, TemperatureK, Updraft;
        public float DefaultTemperatureK = 285f;
    }

    // Builds the height maps of a layer from the field's maps (package T13). Deterministic: the same inputs, tuning, body radius and seed give
    // the same bytes.
    internal static class HeightMapBuilder
    {
        internal static HeightMaps Build(HeightInputs input, HeightTuning t, double bodyRadiusM, uint seed, HeightNoiseCache cache = null)
        {
            int w = input.Width, h = input.Height, n = w * h;
            var maps = new HeightMaps(w, h, t.RootRangeM, t.TopRangeM, t.VariationRangeM);
            float[] root = new float[n], top = new float[n], variation = new float[n];

            if (t.BasePhysics && input.Humidity != null)
            {
                var spread = new float[n];
                double sw = 0.0, sum = 0.0;
                for (int y = 0; y < h; y++)
                {
                    double wt = Math.Cos((0.5 - (y + 0.5) / h) * Math.PI);
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        double temp = input.TemperatureK != null ? input.TemperatureK[i] : input.DefaultTemperatureK;
                        if (double.IsNaN(temp) || temp < 150.0) temp = input.DefaultTemperatureK;
                        spread[i] = (float)HeightModel.DewPointSpreadK(temp, input.Humidity[i]);
                        sw += wt;
                        sum += wt * spread[i];
                    }
                }
                double reference = t.ReferenceSpreadK > 0f ? t.ReferenceSpreadK : sum / sw;
                for (int i = 0; i < n; i++)
                    root[i] = (float)Math.Max(-t.PhysicsMaxM, Math.Min(t.PhysicsMaxM,
                        HeightModel.LclM(spread[i], t.LclMPerK) - HeightModel.LclM(reference, t.LclMPerK)));
            }

            if (t.BaseVariation)
            {
                if (input.Coverage != null && t.SizeTrendM != 0f)
                {
                    float[] near = NeighbourhoodMean(input.Coverage, w, h, bodyRadiusM, t.SizeRadiusM);
                    for (int i = 0; i < n; i++) root[i] += t.SizeTrendM * (near[i] - 0.5f);
                }
                if (t.VariationAmplitudeM > 0f)
                {
                    float[] noise = StandardNoise(cache, w, h, bodyRadiusM, t.VariationWavelengthM, seed);
                    for (int i = 0; i < n; i++) variation[i] = noise[i] * t.VariationAmplitudeM;
                }
            }

            if (t.TopFromUpdraft && input.Updraft != null)
            {
                float[] jitter = null;
                if (t.TopJitterM > 0f) jitter = StandardNoise(cache, w, h, bodyRadiusM, 0.35 * t.VariationWavelengthM, seed ^ 0x9E3779B9u);
                for (int i = 0; i < n; i++)
                    top[i] = (float)HeightModel.TopTermM(t, input.Updraft[i]) + (jitter != null ? jitter[i] * t.TopJitterM : 0f);
            }

            for (int i = 0; i < n; i++)
            {
                maps.Bytes[i * 3] = HeightMaps.Encode(root[i], maps.RootRangeM);
                maps.Bytes[i * 3 + 1] = HeightMaps.Encode(top[i], maps.TopRangeM);
                maps.Bytes[i * 3 + 2] = HeightMaps.Encode(variation[i], maps.VariationRangeM);
            }
            return maps;
        }

        // The noise field of a size, wavelength and seed at zero mean and unit deviation: it does not depend on the weather, so a cache holds it
        // (read only: callers must not write into the array). Without a cache it is made each time; the values are the same.
        private static float[] StandardNoise(HeightNoiseCache cache, int w, int h, double bodyRadiusM, double wavelengthM, uint seed)
        {
            if (cache != null) return cache.Get(w, h, bodyRadiusM, wavelengthM, seed);
            float[] noise = SphereNoise(w, h, bodyRadiusM, wavelengthM, seed);
            Standardise(noise, h, w);
            return noise;
        }

        // Two octaves of value noise on the sphere, wavelength in m on the ground, values about -1 to 1.
        internal static float[] SphereNoise(int w, int h, double bodyRadiusM, double wavelengthM, uint seed)
        {
            var result = new float[w * h];
            double k = bodyRadiusM / Math.Max(1.0, wavelengthM);
            for (int y = 0; y < h; y++)
            {
                double lat = (0.5 - (y + 0.5) / h) * Math.PI;
                double cl = Math.Cos(lat), sl = Math.Sin(lat);
                for (int x = 0; x < w; x++)
                {
                    double lon = ((x + 0.5) / w - 0.5) * 2.0 * Math.PI;
                    double dx = cl * Math.Cos(lon) * k, dy = cl * Math.Sin(lon) * k, dz = sl * k;
                    double a = HeightModel.ValueNoise(dx, dy, dz, seed) * 2.0 - 1.0;
                    double b = HeightModel.ValueNoise(dx * 2.17 + 31.0, dy * 2.17 + 31.0, dz * 2.17 + 31.0, seed ^ 0x68E31DA4u) * 2.0 - 1.0;
                    result[y * w + x] = (float)(0.75 * a + 0.25 * b);
                }
            }
            return result;
        }

        // Shifts and scales the field to zero mean and unit standard deviation over the sphere (rows weighted by the cosine of the latitude).
        internal static void Standardise(float[] values, int h, int w)
        {
            double sw = 0.0, sum = 0.0, sq = 0.0;
            for (int y = 0; y < h; y++)
            {
                double wt = Math.Cos((0.5 - (y + 0.5) / h) * Math.PI);
                for (int x = 0; x < w; x++)
                {
                    double v = values[y * w + x];
                    sw += wt;
                    sum += wt * v;
                    sq += wt * v * v;
                }
            }
            double mean = sum / sw, std = Math.Sqrt(Math.Max(1e-12, sq / sw - mean * mean));
            for (int i = 0; i < values.Length; i++) values[i] = (float)((values[i] - mean) / std);
        }

        // The mean of a map within a radius on the ground: a box blur along the row of the width the radius makes at that latitude
        // (wrapping at the date line, at most half the row) and then along the column.
        internal static float[] NeighbourhoodMean(float[] map, int w, int h, double bodyRadiusM, double radiusM)
        {
            var rows = new float[w * h];
            double texelRad = 2.0 * Math.PI / w;
            for (int y = 0; y < h; y++)
            {
                double cl = Math.Max(0.02, Math.Cos((0.5 - (y + 0.5) / h) * Math.PI));
                int half = (int)Math.Round(radiusM / bodyRadiusM / texelRad / cl);
                half = Math.Max(0, Math.Min(w / 2 - 1, half));
                double sum = 0.0;
                for (int k = -half; k <= half; k++) sum += map[y * w + Mod(k, w)];
                for (int x = 0; x < w; x++)
                {
                    rows[y * w + x] = (float)(sum / (2 * half + 1));
                    sum += map[y * w + Mod(x + half + 1, w)] - map[y * w + Mod(x - half, w)];
                }
            }
            var result = new float[w * h];
            int vhalf = Math.Max(0, Math.Min(h / 2, (int)Math.Round(radiusM / bodyRadiusM / (Math.PI / h))));
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    int y0 = Math.Max(0, y - vhalf), y1 = Math.Min(h - 1, y + vhalf);
                    double sum = 0.0;
                    for (int k = y0; k <= y1; k++) sum += rows[k * w + x];
                    result[y * w + x] = (float)(sum / (y1 - y0 + 1));
                }
            }
            return result;
        }

        private static int Mod(int a, int n)
        {
            int r = a % n;
            return r < 0 ? r + n : r;
        }
    }
}
