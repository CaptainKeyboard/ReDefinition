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

        // The differences are in the share of the sun's light (the transmittance T = exp(-tau), 0 to 1): |T_ray - T_reference| is the difference in how
        // dark the ground is, which is what the eye sees. A relative difference of T (or of tau) is large where T is near 0 and invisible there.
        public double Diff { get { return Math.Abs(Ray - Reference); } }
        public double QuickDiff { get { return Math.Abs(Quick - Reference); } }
    }

    internal sealed class RtAgreementScan
    {
        // The places judged: those where the reference or the ray is neither fully clear nor fully dark (T between 0.02 and 0.98). Places where both are
        // clear or both are dark agree by definition and are left out, so that they do not dilute the figures; a place where one is dark and the other is not stays in.
        public int Places;
        // |T_ray - T_reference| over the places: the median, the 90th percentile, the worst.
        public double Median, P90, Worst;
        // The median of tau_ray / tau_reference (tau = -ln T) over the places where the reference has T between 0.1 and 0.9: the scale of the ray side against the field.
        // A factor of two in the cloud's depth, or a layer taken twice or not at all, moves it far from 1; the slab-in-shell approximation does not.
        public double TauRatioMedian;
        public int RatioPlaces;
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

        // The five places of the brief round (lat0, lon0): under a broken field, at an edge, under a closed cloud layer, behind a layer pair, at the terminator. Each is the
        // place of its kind nearest to (lat0, lon0) on a lattice of +-halfSpanDeg at stepDeg, where the shadow is neither 0 nor 1. A kind with no place is
        // named in `missing` (comma separated) and absent from the list. A kind is:
        //   p1 broken field: the reference factor between 0.3 and 0.7;
        //   p2 edge: between 0.35 and 0.65, and a place 0.6 degrees to the west or east with a factor that differs by more than 0.4;
        //   p3 closed layer: between 0.02 and 0.5, and the places 0.4 degrees round it all between 0.02 and 0.6;
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
                        bool closedLayer = true;
                        for (int k = 0; k < 8 && closedLayer; k++)
                        {
                            double a = k * Math.PI / 4.0, around = RefAt(c, lat + 0.4 * Math.Cos(a), lon + 0.4 * Math.Sin(a), elevationDeg);
                            if (around < 0.02 || around > 0.6) closedLayer = false;
                        }
                        if (closedLayer)
                        {
                            best[2] = distance;
                            found[2] = At(c, "p3 closed layer", lat, lon, elevationDeg);
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

        // The sun's transmittance at a place by the pack's rays alone.
        internal static double RayTransmission(RtCloudPack pack, double lat, double lon, double elevationDeg)
        {
            double x, y, z, sx, sy, sz;
            Position(pack.BodyRadiusM, lat, lon, out x, out y, out z);
            SunTowards(lat, lon, elevationDeg, SunAzimuthDeg, out sx, out sy, out sz);
            return RtCloudRayMath.LightTransmission(pack, x, y, z, sx, sy, sz);
        }

        // How far a cloud pattern moves in `ageS` seconds in the wind (east and north m/s) at a place, as the shift of latitude and longitude in degrees.
        internal static void DriftDegrees(double bodyRadiusM, double lat, double windEastMs, double windNorthMs, double ageS, out double dLatDeg, out double dLonDeg)
        {
            double cos = Math.Max(0.05, Math.Cos(lat * Math.PI / 180.0));
            dLatDeg = windNorthMs * ageS / bodyRadiusM * 180.0 / Math.PI;
            dLonDeg = windEastMs * ageS / (bodyRadiusM * cos) * 180.0 / Math.PI;
        }

        // The pack that stands against the one baked now over the lattice, in the transmittance of the sun's light: the age of the standing pack (its sky is `ageS`
        // old; the weather's patterns move with the wind) shows as a shift of the pattern, so the standing pack at a place is the fresh pack at the place the pattern has
        // moved to, which is the place plus the wind's drift. Returns p90 |T_standing - T_fresh| (raw), p90 |T_standing - T_fresh(place + drift)| (advected: the
        // pack's age taken out by the wind), and p90 |T_fresh - T_fresh(place + drift)| (the change the drift alone makes: what the age is expected to cost).
        internal static void StaleScan(RtCloudPack standing, RtCloudPack fresh, double lat0, double lon0, double halfSpanDeg, double stepDeg, double elevationDeg,
                                       double windEastMs, double windNorthMs, double ageS, out double raw, out double advected, out double expected, out int places)
        {
            int n = (int)Math.Round(halfSpanDeg / stepDeg);
            var rawList = new List<double>();
            var advectedList = new List<double>();
            var expectedList = new List<double>();
            for (int i = -n; i <= n; i++)
                for (int j = -n; j <= n; j++)
                {
                    double lat = lat0 + i * stepDeg, lon = lon0 + j * stepDeg;
                    if (Math.Abs(lat) > 85.0) continue;
                    double dLat, dLon;
                    DriftDegrees(fresh.BodyRadiusM, lat, windEastMs, windNorthMs, ageS, out dLat, out dLon);
                    double s = RayTransmission(standing, lat, lon, elevationDeg), f = RayTransmission(fresh, lat, lon, elevationDeg);
                    double moved = RayTransmission(fresh, lat + dLat, lon + dLon, elevationDeg);
                    bool mid = (s > 0.02 && s < 0.98) || (f > 0.02 && f < 0.98) || (moved > 0.02 && moved < 0.98);
                    if (!mid) continue;
                    rawList.Add(Math.Abs(s - f));
                    advectedList.Add(Math.Abs(s - moved));
                    expectedList.Add(Math.Abs(f - moved));
                }
            places = rawList.Count;
            raw = Percentile90(rawList);
            advected = Percentile90(advectedList);
            expected = Percentile90(expectedList);
        }

        private static double Percentile90(List<double> values)
        {
            if (values.Count == 0) return 0.0;
            values.Sort();
            return values[(int)(values.Count * 0.9)];
        }

        // What both sides read at the places of the lattice where they differ most (the `count` worst): one line each, the place, T of the field and of the ray, and per
        // layer the optical depth along the sun ray (strength applied) by the field's march and by the pack's taps, the map values there, and the pack's own texel
        // there (coverage, tau_full, the base and top it holds as altitudes in m). Where a game start fails the scan, these lines say which layer and which number
        // differ: a column depth that is capped, a base and top that are not the field's, a map that is not the stack's.
        internal static List<string> WorstPlaces(ICloudLayerSource source, LayerTypeTable[] tables, RtCloudPack pack, double lat0, double lon0, double halfSpanDeg,
                                                 double stepDeg, double elevationDeg, int count)
        {
            var reference = new CloudShadowField(source, tables) { StepsPerSpan = 64, DiscSamples = 1 };
            int n = (int)Math.Round(halfSpanDeg / stepDeg);
            var all = new List<double[]>();
            for (int i = -n; i <= n; i++)
                for (int j = -n; j <= n; j++)
                {
                    double lat = lat0 + i * stepDeg, lon = lon0 + j * stepDeg;
                    if (Math.Abs(lat) > 85.0) continue;
                    double x, y, z, sx, sy, sz;
                    Position(source.BodyRadiusM, lat, lon, out x, out y, out z);
                    SunTowards(lat, lon, elevationDeg, SunAzimuthDeg, out sx, out sy, out sz);
                    double tf = reference.DirectTransmission(x, y, z, sx, sy, sz), tr = RtCloudRayMath.LightTransmission(pack, x, y, z, sx, sy, sz);
                    all.Add(new[] { Math.Abs(tf - tr), lat, lon, tf, tr });
                }
            all.Sort((a, b) => b[0].CompareTo(a[0]));
            var lines = new List<string>();
            for (int k = 0; k < count && k < all.Count; k++)
            {
                double[] w = all[k];
                double x, y, z, sx, sy, sz;
                Position(source.BodyRadiusM, w[1], w[2], out x, out y, out z);
                SunTowards(w[1], w[2], elevationDeg, SunAzimuthDeg, out sx, out sy, out sz);
                string line = "lat " + w[1].ToString("0.0") + " lon " + w[2].ToString("0.0") + " T field " + w[3].ToString("0.000") + " ray " + w[4].ToString("0.000");
                for (int l = 0; l < pack.LayerCount; l++)
                {
                    CloudLayerDef def = source.LayerAt(pack.Entries[l].StackIndex);
                    double field = reference.LayerOpticalDepth(pack.Entries[l].StackIndex, x, y, z, sx, sy, sz, double.MaxValue) * def.ShadowStrength;
                    double rays = RtCloudRayMath.LayerTau(pack, l, x, y, z, sx, sy, sz, double.MaxValue) * def.ShadowStrength;
                    float cover, type;
                    source.SampleMaps(pack.Entries[l].StackIndex, w[1], w[2], out cover, out type);
                    double packCover, tauFull, baseW, topW;
                    pack.Sample(l, w[1], w[2] - pack.Entries[l].MapShiftDeg, out packCover, out tauFull, out baseW, out topW);
                    double inner = pack.Entries[l].InnerRadiusM - pack.BodyRadiusM, thickness = pack.Entries[l].ThicknessM;
                    string band = tauFull > 1e-6 ? (inner + baseW / tauFull * thickness).ToString("0") + "-" + (inner + topW / tauFull * thickness).ToString("0") : "none";
                    lines.Add(line + " | " + pack.Entries[l].Name + " tau field " + field.ToString("0.00") + " pack " + rays.ToString("0.00") + " map " + cover.ToString("0.00") + "/" + type.ToString("0.00")
                              + " texel cover " + packCover.ToString("0.00") + " tau_full " + tauFull.ToString("0.0") + " band m " + band);
                }
            }
            return lines;
        }

        // The absolute difference of the ray's transmittance against the reference's over the lattice (see RtAgreementScan for the places that count).
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
            var ratios = new List<double>();
            for (int i = -n; i <= n; i++)
                for (int j = -n; j <= n; j++)
                {
                    double lat = lat0 + i * stepDeg, lon = lon0 + j * stepDeg;
                    if (Math.Abs(lat) > 85.0) continue;
                    double x, y, z, sx, sy, sz;
                    Position(source.BodyRadiusM, lat, lon, out x, out y, out z);
                    SunTowards(lat, lon, elevationDeg, SunAzimuthDeg, out sx, out sy, out sz);
                    double reference = c.Reference.DirectTransmission(x, y, z, sx, sy, sz);
                    double ray = RtCloudRayMath.LightTransmission(pack, x, y, z, sx, sy, sz);
                    bool referenceMid = reference > 0.02 && reference < 0.98, rayMid = ray > 0.02 && ray < 0.98;
                    if (!referenceMid && !rayMid) continue;
                    diffs.Add(Math.Abs(ray - reference));
                    if (reference > 0.1 && reference < 0.9) ratios.Add(Math.Log(Math.Max(1e-6, ray)) / Math.Log(reference));
                }
            diffs.Sort();
            ratios.Sort();
            var scan = new RtAgreementScan { Places = diffs.Count, RatioPlaces = ratios.Count };
            if (diffs.Count > 0)
            {
                scan.Median = diffs[diffs.Count / 2];
                scan.P90 = diffs[(int)(diffs.Count * 0.9)];
                scan.Worst = diffs[diffs.Count - 1];
            }
            if (ratios.Count > 0) scan.TauRatioMedian = ratios[ratios.Count / 2];
            return scan;
        }
    }
}
