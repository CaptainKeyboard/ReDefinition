using System;

namespace ReDefinition.Atmospheres
{
    // How a painted map lies on the sphere, pure. EVE reads a layer's map as an equirectangular picture in the LAYER's frame
    // (Utils/TextureWrapper.cs:596-604): a point of the layer is the unit vector n = normalize(cloudRotationMatrix x worldPosition),
    // u = 0.5 + atan2(n.x, n.z) / 2 pi and v = acos(n.y) / pi (EveCoverageMath.Uv, the same formula). v is linear in latitude, u is
    // linear in longitude, so a field's map (WeatherMap: equirectangular, row 0 at the north pole, column 0 at longitude -180) and a
    // layer's map differ by a shift of longitude and, where the shader reads the layer's x axis the other way, a mirror:
    //   field u = fract(layer u + shift / 360)                 (not mirrored)
    //   field u = fract(0.5 - (layer u - 0.5) + shift / 360)   (mirrored)
    // The shift (degrees) is the body longitude of the layer frame's zero meridian. ShiftFrom works it out from one place whose
    // layer coordinates are known (the binding reads them from SampleCoverage's own rotation matrix), so the painter and EVE agree
    // whatever the rotation of the layer is. Rows are not shifted: a layer whose pole axis is not the body's pole fails LatitudeError.
    internal static class EveMapLayout
    {
        // The latitude in degrees of a layer v, and the v of a latitude.
        internal static double LatitudeOfV(double v)
        {
            return 90.0 - v * 180.0;
        }

        internal static double VOfLatitude(double latDeg)
        {
            return (90.0 - latDeg) / 180.0;
        }

        // The u of EVE's texture for a body longitude (degrees) given the layer's shift and mirror.
        internal static double LayerU(double lonDeg, double shiftDeg, bool mirrored)
        {
            double fieldU = (lonDeg + 180.0) / 360.0;
            double u = mirrored ? 0.5 - (fieldU - shiftDeg / 360.0 - 0.5) : fieldU - shiftDeg / 360.0;
            return Fract(u);
        }

        // The u of the field's map that a layer u shows.
        internal static double FieldU(double layerU, double shiftDeg, bool mirrored)
        {
            double u = mirrored ? 0.5 - (layerU - 0.5) + shiftDeg / 360.0 : layerU + shiftDeg / 360.0;
            return Fract(u);
        }

        // The shift that makes a place at body longitude lonDeg appear at layer u.
        internal static double ShiftFrom(double lonDeg, double layerU, bool mirrored)
        {
            double fieldU = (lonDeg + 180.0) / 360.0;
            double s = mirrored ? (fieldU - 0.5 + (layerU - 0.5)) * 360.0 : (fieldU - layerU) * 360.0;
            return WrapDegrees(s);
        }

        // Where a place on the body lies in the layer's map: the callee turns the latitude and longitude into a world position and runs
        // EVE's formula on it (the layer's CloudRotationMatrix and LayerCoordinates).
        internal delegate void LayerOfPlace(double latDeg, double lonDeg, out double u, out double v);

        // How a layer's map is laid on the body, found from three places: the shift, whether the layer's longitude runs the other way,
        // and the largest latitude error. Returns false where no shift and mirror makes the three places fit within toleranceDeg of
        // longitude and latitude (a tilted layer): the painter then leaves the layer alone.
        internal static bool Calibrate(LayerOfPlace layerOf, double toleranceDeg, out double shiftDeg, out bool mirrored, out double worstErrorDeg)
        {
            double[,] places = { { 0.0, 0.0 }, { 35.0, 100.0 }, { -50.0, -140.0 } };
            double u0, v0;
            layerOf(places[0, 0], places[0, 1], out u0, out v0);
            // Two candidates; the one that fits all three places wins.
            double bestError = double.MaxValue;
            shiftDeg = 0;
            mirrored = false;
            worstErrorDeg = double.MaxValue;
            for (int m = 0; m < 2; m++)
            {
                bool flip = m == 1;
                double shift = ShiftFrom(places[0, 1], u0, flip);
                double worst = 0;
                for (int k = 0; k < 3; k++)
                {
                    double u, v;
                    layerOf(places[k, 0], places[k, 1], out u, out v);
                    double expectedU = LayerU(places[k, 1], shift, flip);
                    double du = Math.Abs(u - expectedU);
                    if (du > 0.5) du = 1.0 - du;
                    worst = Math.Max(worst, Math.Max(du * 360.0, LatitudeError(places[k, 0], v)));
                }
                if (worst < bestError)
                {
                    bestError = worst;
                    shiftDeg = shift;
                    mirrored = flip;
                }
            }
            worstErrorDeg = bestError;
            return bestError <= toleranceDeg;
        }

        // The layer coordinates of a direction in the layer's frame (EVE's formula).
        internal static void LayerCoordinates(double x, double y, double z, out double u, out double v)
        {
            EveCoverageMath.Uv(x, y, z, out u, out v);
        }

        // How far the layer's v of a place is from the latitude's v, in degrees of latitude: 0 where the layer's pole axis is the
        // body's. A tilt shows as an error that depends on the longitude.
        internal static double LatitudeError(double latDeg, double layerV)
        {
            return Math.Abs(LatitudeOfV(layerV) - latDeg);
        }

        // The value of a field map (bilinear, longitude wraps, latitude clamps) at the field coordinates fu, fv in 0 to 1.
        internal static float SampleBilinear(float[] values, int width, int height, double fu, double fv)
        {
            double x = fu * width - 0.5;
            double y = fv * height - 0.5;
            if (y < 0) y = 0;
            if (y > height - 1) y = height - 1;
            double x0 = Math.Floor(x);
            double fx = x - x0;
            int ix0 = Wrap((int)x0, width), ix1 = Wrap((int)x0 + 1, width);
            int iy0 = (int)Math.Floor(y), iy1 = Math.Min(iy0 + 1, height - 1);
            double fy = y - iy0;
            double top = values[iy0 * width + ix0] * (1 - fx) + values[iy0 * width + ix1] * fx;
            double bottom = values[iy1 * width + ix0] * (1 - fx) + values[iy1 * width + ix1] * fx;
            return (float)(top * (1 - fy) + bottom * fy);
        }

        // Paints a layer-frame map (width x height bytes) from a field map: the CPU twin of the painter pass's resample, without the
        // slew. Texel (i, j) has its centre at ((i + 0.5) / width, (j + 0.5) / height).
        internal static void Resample(float[] values, int fieldWidth, int fieldHeight, byte[] target, int width, int height,
            double shiftDeg, bool mirrored, float scale = 1f)
        {
            ResampleRows(values, fieldWidth, fieldHeight, target, width, height, shiftDeg, mirrored, 0, height, scale);
        }

        // The same for the rows row0 to row0 + rows - 1 only (the CPU mirror works through the map a band of rows a frame).
        internal static void ResampleRows(float[] values, int fieldWidth, int fieldHeight, byte[] target, int width, int height,
            double shiftDeg, bool mirrored, int row0, int rows, float scale = 1f)
        {
            int end = Math.Min(height, row0 + rows);
            for (int j = Math.Max(row0, 0); j < end; j++)
            {
                double v = (j + 0.5) / height;
                for (int i = 0; i < width; i++)
                {
                    double u = (i + 0.5) / width;
                    float value = SampleBilinear(values, fieldWidth, fieldHeight, FieldU(u, shiftDeg, mirrored), v) * scale;
                    target[j * width + i] = value <= 0f ? (byte)0 : value >= 1f ? (byte)255 : (byte)(value * 255f + 0.5f);
                }
            }
        }

        // The Pearson correlation of two byte maps of the same length; 1 for two equal constant maps, 0 for one constant and one not.
        internal static double Correlation(byte[] a, byte[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            if (n == 0) return 0;
            double ma = 0, mb = 0;
            for (int i = 0; i < n; i++)
            {
                ma += a[i];
                mb += b[i];
            }
            ma /= n;
            mb /= n;
            double saa = 0, sbb = 0, sab = 0;
            for (int i = 0; i < n; i++)
            {
                double da = a[i] - ma, db = b[i] - mb;
                saa += da * da;
                sbb += db * db;
                sab += da * db;
            }
            if (saa <= 0 && sbb <= 0) return Math.Abs(ma - mb) < 0.5 ? 1.0 : 0.0;
            if (saa <= 0 || sbb <= 0) return 0.0;
            return sab / Math.Sqrt(saa * sbb);
        }

        // The correlation of a small map with a large one: the large one sampled at the small one's texel centres (bilinear).
        internal static double CorrelationAcrossSizes(byte[] small, int sw, int sh, byte[] large, int lw, int lh)
        {
            var asFloat = new float[large.Length];
            for (int i = 0; i < large.Length; i++) asFloat[i] = large[i] / 255f;
            var resampled = new byte[small.Length];
            for (int j = 0; j < sh; j++)
            {
                for (int i = 0; i < sw; i++)
                {
                    float v = SampleBilinear(asFloat, lw, lh, (i + 0.5) / sw, (j + 0.5) / sh);
                    resampled[j * sw + i] = (byte)Math.Min(255, Math.Max(0, (int)Math.Round(v * 255f)));
                }
            }
            return Correlation(small, resampled);
        }

        private static double Fract(double u)
        {
            double f = u - Math.Floor(u);
            return f;
        }

        internal static double WrapDegrees(double d)
        {
            d = (d + 180.0) % 360.0;
            if (d < 0) d += 360.0;
            return d - 180.0;
        }

        private static int Wrap(int i, int n)
        {
            int r = i % n;
            return r < 0 ? r + n : r;
        }
    }
}
