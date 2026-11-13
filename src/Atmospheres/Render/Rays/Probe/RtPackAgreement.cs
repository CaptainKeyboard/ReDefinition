using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // The agreement of the ray-side pack with the cloud shadow of T04 at places of a real or a test sky (package T18). Pure (no Unity): the series probe
    // (T18Probe) runs it against the game's stack and the unit tests run it against test skies, so that the selection of the five places is checked before a
    // game start. The reference is CloudShadowField.DirectTransmission at 64 steps a span (the converged value); the field at the shadow pass's own 8 steps is
    // reported beside it (its quadrature over the widened tight interval of a layer with height variation is coarse). The sun is a direction towards it of the
    // given elevation, from the east (azimuth 90 degrees), one ray.
    internal sealed class RtAgreementPlace
    {
        public string Name = "";
        public double LatDeg, LonDeg, ElevationDeg;
        // The direct light that reaches the place: the shadow field at 64 steps, at 8 steps, and by the pack's rays.
        public double Reference, Quick, Ray;

        public double Diff { get { return Math.Abs(Ray - Reference) / Math.Max(1e-9, Reference); } }
        public double QuickDiff { get { return Math.Abs(Quick - Reference) / Math.Max(1e-9, Reference); } }
    }

    internal sealed class RtAgreementScan
    {
        public int Places;
        public double Median, P90, Worst;
    }

    internal static class RtPackAgreement
    {
        internal const double SunAzimuthDeg = 90.0;

        // The direction towards a sun at an elevation and azimuth (degrees, from north towards east) seen from a place, as a map-frame vector.
        internal static void SunTowards(double latDeg, double lonDeg, double elevationDeg, double azimuthDeg, out double sx, out double sy, out double sz)
        {
            double la = latDeg * Math.PI / 180.0, lo = lonDeg * Math.PI / 180.0;
            double ux = Math.Cos(la) * Math.Cos(lo), uy = Math.Cos(la) * Math.Sin(lo), uz = Math.Sin(la);
            double ex = -Math.Sin(lo), ey = Math.Cos(lo), ez = 0.0;
            double nx = -Math.Sin(la) * Math.Cos(lo), ny = -Math.Sin(la) * Math.Sin(lo), nz = Math.Cos(la);
            double e = elevationDeg * Math.PI / 180.0, a = azimuthDeg * Math.PI / 180.0;
            double h = Math.Cos(e), v = Math.Sin(e);
            sx = h * (Math.Cos(a) * nx + Math.Sin(a) * ex) + v * ux;
            sy = h * (Math.Cos(a) * ny + Math.Sin(a) * ey) + v * uy;
            sz = h * (Math.Cos(a) * nz + Math.Sin(a) * ez) + v * uz;
        }

        private static void Position(double bodyRadiusM, double latDeg, double lonDeg, out double x, out double y, out double z)
        {
            double la = latDeg * Math.PI / 180.0, lo = lonDeg * Math.PI / 180.0;
            x = Math.Cos(la) * Math.Cos(lo) * bodyRadiusM;
            y = Math.Cos(la) * Math.Sin(lo) * bodyRadiusM;
            z = Math.Sin(la) * bodyRadiusM;
        }

        private sealed class Context
        {
            public ICloudLayerSource Source;
            public CloudShadowField Reference, Quick;
            public RtCloudPack Pack;
        }

        private static double RefAt(Context c, double lat, double lon, double elevation)
        {
            double x, y, z, sx, sy, sz;
            Position(c.Source.BodyRadiusM, lat, lon, out x, out y, out z);
            SunTowards(lat, lon, elevation, SunAzimuthDeg, out sx, out sy, out sz);
            return c.Reference.DirectTransmission(x, y, z, sx, sy, sz);
        }

        private static RtAgreementPlace At(Context c, string name, double lat, double lon, double elevation)
        {
            double x, y, z, sx, sy, sz;
            Position(c.Source.BodyRadiusM, lat, lon, out x, out y, out z);
            SunTowards(lat, lon, elevation, SunAzimuthDeg, out sx, out sy, out sz);
            return new RtAgreementPlace
            {
                Name = name, LatDeg = lat, LonDeg = lon, ElevationDeg = elevation,
                Reference = c.Reference.DirectTransmission(x, y, z, sx, sy, sz),
                Quick = c.Quick.DirectTransmission(x, y, z, sx, sy, sz),
                Ray = RtCloudRayMath.LightTransmission(c.Pack, x, y, z, sx, sy, sz)
            };
        }

        // The optical depth of one layer along the sun ray from a place, with its strength: how much of the shadow is the layer's.
        private static double LayerShare(Context c, int layer, double lat, double lon, double elevation)
        {
            double x, y, z, sx, sy, sz;
            Position(c.Source.BodyRadiusM, lat, lon, out x, out y, out z);
            SunTowards(lat, lon, elevation, SunAzimuthDeg, out sx, out sy, out sz);
            return c.Reference.LayerOpticalDepth(layer, x, y, z, sx, sy, sz, double.MaxValue) * c.Source.LayerAt(layer).ShadowStrength;
        }

        // The five places of the brief round (lat0, lon0): under a broken field, at an edge, under a deck, behind a layer pair, at the terminator. Each is the
        // place of its kind nearest to (lat0, lon0) on a lattice of +-halfSpanDeg at stepDeg, where the shadow is neither 0 nor 1. A kind with no place is
        // named in `missing` (comma separated) and absent from the list. A kind is:
        //   p1 broken field: the reference factor between 0.3 and 0.7;
        //   p2 edge: between 0.35 and 0.65, and a place 0.6 degrees to the west or east with a factor that differs by more than 0.4;
        //   p3 deck: between 0.02 and 0.5, and the places 0.4 degrees round it all between 0.02 and 0.6;
        //   p4 layer pair: between 0.02 and 0.9, and two layers each with a share of the optical depth along the sun ray (strength applied) of 0.1 or more;
        //   p5 terminator: the sun at 6 degrees, the factor between 0.05 and 0.95.
        internal static List<RtAgreementPlace> Pick(ICloudLayerSource source, LayerTypeTable[] tables, RtCloudPack pack, double lat0, double lon0, double halfSpanDeg,
                                                    double stepDeg, double elevationDeg, out string missing)
        {
            var c = new Context
            {
                Source = source, Pack = pack,
                Reference = new CloudShadowField(source, tables) { StepsPerSpan = 64, DiscSamples = 1 },
                Quick = new CloudShadowField(source, tables) { StepsPerSpan = 8, DiscSamples = 1 }
            };
            int n = (int)Math.Round(halfSpanDeg / stepDeg);
            var found = new RtAgreementPlace[5];
            double[] best = { double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue };
            for (int i = -n; i <= n; i++)
                for (int j = -n; j <= n; j++)
                {
                    double lat = lat0 + i * stepDeg, lon = lon0 + j * stepDeg;
                    if (Math.Abs(lat) > 85.0) continue;
                    double distance = i * i + j * j;
                    double t = RefAt(c, lat, lon, elevationDeg);
                    if (t >= 0.3 && t <= 0.7 && distance < best[0])
                    {
                        best[0] = distance;
                        found[0] = At(c, "p1 broken field", lat, lon, elevationDeg);
                    }
                    if (t >= 0.35 && t <= 0.65 && distance < best[1])
                    {
                        double west = RefAt(c, lat, lon - 0.6, elevationDeg), east = RefAt(c, lat, lon + 0.6, elevationDeg);
                        if (Math.Abs(west - t) > 0.4 || Math.Abs(east - t) > 0.4)
                        {
                            best[1] = distance;
                            found[1] = At(c, "p2 edge", lat, lon, elevationDeg);
                        }
                    }
                    if (t >= 0.02 && t <= 0.5 && distance < best[2])
                    {
                        bool deck = true;
                        for (int k = 0; k < 8 && deck; k++)
                        {
                            double a = k * Math.PI / 4.0, around = RefAt(c, lat + 0.4 * Math.Cos(a), lon + 0.4 * Math.Sin(a), elevationDeg);
                            if (around < 0.02 || around > 0.6) deck = false;
                        }
                        if (deck)
                        {
                            best[2] = distance;
                            found[2] = At(c, "p3 deck", lat, lon, elevationDeg);
                        }
                    }
                    if (t >= 0.02 && t <= 0.9 && distance < best[3])
                    {
                        int heavy = 0;
                        for (int l = 0; l < source.LayerCount; l++)
                            if (LayerShare(c, l, lat, lon, elevationDeg) >= 0.1) heavy++;
                        if (heavy >= 2)
                        {
                            best[3] = distance;
                            found[3] = At(c, "p4 layer pair", lat, lon, elevationDeg);
                        }
                    }
                    if (distance < best[4])
                    {
                        double low = RefAt(c, lat, lon, 6.0);
                        if (low >= 0.05 && low <= 0.95)
                        {
                            best[4] = distance;
                            found[4] = At(c, "p5 terminator", lat, lon, 6.0);
                        }
                    }
                }
            var list = new List<RtAgreementPlace>();
            var names = new List<string>();
            string[] kinds = { "p1", "p2", "p3", "p4", "p5" };
            for (int k = 0; k < 5; k++)
            {
                if (found[k] != null) list.Add(found[k]);
                else names.Add(kinds[k]);
            }
            missing = string.Join(",", names.ToArray());
            return list;
        }

        // The relative difference of the ray against the reference over every place of the lattice where the reference factor is between 0.05 and 0.95.
        internal static RtAgreementScan Scan(ICloudLayerSource source, LayerTypeTable[] tables, RtCloudPack pack, double lat0, double lon0, double halfSpanDeg,
                                             double stepDeg, double elevationDeg)
        {
            var c = new Context
            {
                Source = source, Pack = pack,
                Reference = new CloudShadowField(source, tables) { StepsPerSpan = 64, DiscSamples = 1 },
                Quick = new CloudShadowField(source, tables) { StepsPerSpan = 8, DiscSamples = 1 }
            };
            int n = (int)Math.Round(halfSpanDeg / stepDeg);
            var diffs = new List<double>();
            for (int i = -n; i <= n; i++)
                for (int j = -n; j <= n; j++)
                {
                    double lat = lat0 + i * stepDeg, lon = lon0 + j * stepDeg;
                    if (Math.Abs(lat) > 85.0) continue;
                    double x, y, z, sx, sy, sz;
                    Position(source.BodyRadiusM, lat, lon, out x, out y, out z);
                    SunTowards(lat, lon, elevationDeg, SunAzimuthDeg, out sx, out sy, out sz);
                    double reference = c.Reference.DirectTransmission(x, y, z, sx, sy, sz);
                    if (reference <= 0.05 || reference >= 0.95) continue;
                    diffs.Add(Math.Abs(RtCloudRayMath.LightTransmission(pack, x, y, z, sx, sy, sz) - reference) / reference);
                }
            diffs.Sort();
            var scan = new RtAgreementScan { Places = diffs.Count };
            if (diffs.Count > 0)
            {
                scan.Median = diffs[diffs.Count / 2];
                scan.P90 = diffs[(int)(diffs.Count * 0.9)];
                scan.Worst = diffs[diffs.Count - 1];
            }
            return scan;
        }
    }
}
