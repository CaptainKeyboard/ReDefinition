using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // A copy of one of the field's maps (WeatherMap layout: row 0 at the north pole, column 0 at longitude -180), kept for a worker
    // thread: the live WeatherMap is refilled by the main thread.
    internal sealed class IceMapCopy
    {
        internal int Width;
        internal int Height;
        internal float[] Values = new float[0];

        internal bool Valid
        {
            get { return Width > 0 && Height > 0 && Values.Length == Width * Height; }
        }

        // Copies a WeatherMap; an empty or unusable map gives an invalid copy that reads as 0.
        internal void CopyFrom(WeatherMap map)
        {
            if (map == null || map.Width < 1 || map.Height < 1 || map.Values == null || map.Values.Length != map.Width * map.Height)
            {
                Width = 0;
                Height = 0;
                return;
            }
            if (Values.Length != map.Values.Length) Values = new float[map.Values.Length];
            Array.Copy(map.Values, Values, map.Values.Length);
            Width = map.Width;
            Height = map.Height;
        }

        // The value at a place, bilinear (the same as WeatherMap.Sample).
        internal float Sample(double latDeg, double lonDeg)
        {
            if (!Valid) return 0f;
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

        private static int Wrap(int i, int n)
        {
            int r = i % n;
            return r < 0 ? r + n : r;
        }
    }

    // Everything the ice of one body is drawn from, as plain data: the worker thread of the orbit picture and the tests take it as it is.
    internal sealed class IceInputs
    {
        internal IceParams Params = new IceParams();
        internal KspFrame Frame = KspFrame.Sphere;
        // The coast map (ShoreGrid), 4 bytes a texel.
        internal byte[] Shore;
        internal readonly IceMapCopy Fraction = new IceMapCopy();
        internal readonly IceMapCopy ThicknessM = new IceMapCopy();
        internal readonly IceMapCopy SnowM = new IceMapCopy();
        // The drift offset in cell units.
        internal float DriftX, DriftY, DriftZ;
        // The ice colour of the body.
        internal float Red = 0.85f, Green = 0.92f, Blue = 0.97f;
        // A fraction that replaces the field's everywhere on the sea when it is 0 or more (the series' forced states); negative = off.
        internal float ForcedFraction = -1f;
        // The snow depth in metres that counts as a full white dust on the ice.
        internal float SnowFullM = 0.05f;
    }

    // The ice of the orbit and map view as a picture in the field's map layout (1024 x 512 by default): rgba bytes, alpha the opacity of
    // the ice (0 on open water, in a lead and on land). The picture is made on a worker thread from IceInputs by the same function the
    // ground overlay's shader repeats; the shell in scaled space looks it up per pixel (IceOrbitLayer).
    internal static class IceOrbitPicture
    {
        internal const int Width = 1024;
        internal const int Height = 512;

        // The counts of the last fill: texels on the sea and texels with ice.
        internal struct Stats
        {
            internal int SeaTexels;
            internal int IceTexels;
        }

        // The state of the ice at a place given by latitude and longitude (game degrees); sea is false where the shore map says land.
        internal static IceState StateAt(IceInputs input, double latDeg, double lonDeg, out bool sea)
        {
            double x, y, z;
            input.Frame.Direction(latDeg, lonDeg, out x, out y, out z);
            return StateAtDirection(input, x, y, z, latDeg, lonDeg, out sea);
        }

        internal static IceState StateAtDirection(IceInputs input, double x, double y, double z, double latDeg, double lonDeg, out bool sea)
        {
            double u, v;
            ShoreGrid.Uv(x, y, z, out u, out v);
            sea = input.Shore == null || ShoreGrid.Sample(input.Shore, 2, u, v) > 0.5f;
            if (!sea) return IceMask.None;
            float shore = input.Shore == null ? 0f : ShoreGrid.Sample(input.Shore, 0, u, v);
            float shoreMean = input.Shore == null ? 0f : ShoreGrid.Sample(input.Shore, 1, u, v);
            float fraction = input.ForcedFraction >= 0f ? input.ForcedFraction : input.Fraction.Sample(latDeg, lonDeg);
            float thickness = input.ThicknessM.Sample(latDeg, lonDeg);
            return IceMask.Evaluate(input.Params, (float)x, (float)y, (float)z, fraction, thickness, shore, shoreMean, input.DriftX, input.DriftY, input.DriftZ);
        }

        // Fills rgba (width * height * 4 bytes, row 0 at the north pole like the field's maps).
        internal static Stats Fill(IceInputs input, byte[] rgba, int width, int height)
        {
            var stats = new Stats();
            for (int row = 0; row < height; row++)
            {
                double lat = 90.0 - (row + 0.5) / height * 180.0;
                for (int col = 0; col < width; col++)
                {
                    double lon = (col + 0.5) / width * 360.0 - 180.0;
                    bool sea;
                    IceState s = StateAt(input, lat, lon, out sea);
                    int i = (row * width + col) * 4;
                    if (sea) stats.SeaTexels++;
                    if (!s.Ice)
                    {
                        rgba[i] = ToByte(input.Red);
                        rgba[i + 1] = ToByte(input.Green);
                        rgba[i + 2] = ToByte(input.Blue);
                        rgba[i + 3] = 0;
                        continue;
                    }
                    stats.IceTexels++;
                    float snow = input.SnowM.Valid ? Math.Min(1f, input.SnowM.Sample(lat, lon) / Math.Max(1e-4f, input.SnowFullM)) : 0f;
                    float r, g, b;
                    IceMask.ColourOf(s.ThicknessM, input.Red, input.Green, input.Blue, snow, out r, out g, out b);
                    rgba[i] = ToByte(r);
                    rgba[i + 1] = ToByte(g);
                    rgba[i + 2] = ToByte(b);
                    rgba[i + 3] = ToByte(s.Opacity);
                }
            }
            return stats;
        }

        // The share of sea samples with ice in a window of latitude and longitude (degrees), and the field's mean fraction over the same
        // sea samples: the two numbers the rows compare. A regular grid of step degrees; samples on land are not counted.
        internal static void WindowCoverage(IceInputs input, double latMin, double latMax, double lonMin, double lonMax, double stepDeg,
            out double iceShare, out double fieldMean, out int seaSamples)
        {
            int sea = 0, ice = 0;
            double sum = 0;
            for (double lat = latMin + stepDeg / 2; lat < latMax; lat += stepDeg)
                for (double lon = lonMin + stepDeg / 2; lon < lonMax; lon += stepDeg)
                {
                    bool onSea;
                    IceState s = StateAt(input, lat, lon, out onSea);
                    if (!onSea) continue;
                    sea++;
                    if (s.Ice) ice++;
                    sum += input.ForcedFraction >= 0f ? input.ForcedFraction : input.Params.Permanent ? 1.0 : Math.Min(1.0, Math.Max(0.0, input.Fraction.Sample(lat, lon)));
                }
            seaSamples = sea;
            iceShare = sea > 0 ? ice / (double)sea : 0.0;
            fieldMean = sea > 0 ? sum / sea : 0.0;
        }

        private static byte ToByte(float v)
        {
            v = v < 0f ? 0f : v > 1f ? 1f : v;
            return (byte)(v * 255f + 0.5f);
        }
    }
}
