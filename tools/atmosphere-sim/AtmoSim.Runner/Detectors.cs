using System;
using System.Collections.Generic;
using AtmoSim.Core;

namespace AtmoSim.Runner
{
    // The metric grid of an atmosphere: cell sizes in metres, area weights, great-circle distances.
    public sealed class MetricGrid
    {
        public readonly int NLon, NLat;
        public readonly double A;
        public readonly double[] LatRad, LonRad, LatDeg, Dx, Dy, W;
        public readonly double WSum;

        // A regular grid, row 0 north, cell centres: latitude 90 - (j + 0.5) 180/NLat, longitude (i + 0.5) 360/NLon (0 to 2 pi).
        public MetricGrid(int nlon, int nlat, double radiusM)
        {
            NLon = nlon; NLat = nlat; A = radiusM;
            LatRad = new double[NLat];
            for (int j = 0; j < NLat; j++) LatRad[j] = (90.0 - (j + 0.5) * 180.0 / NLat) * Math.PI / 180.0;
            LonRad = new double[NLon];
            for (int i = 0; i < NLon; i++) LonRad[i] = 2.0 * Math.PI * (i + 0.5) / NLon;
            LatDeg = new double[NLat]; Dx = new double[NLat]; Dy = new double[NLat]; W = new double[NLat];
            for (int j = 0; j < NLat; j++)
            {
                LatDeg[j] = LatRad[j] * 180.0 / Math.PI;
                Dx[j] = A * Math.Cos(LatRad[j]) * 2.0 * Math.PI / NLon;
                int a = Math.Max(0, j - 1), b = Math.Min(NLat - 1, j + 1);
                Dy[j] = A * Math.Abs(LatRad[a] - LatRad[b]) / Math.Max(1, b - a);
                W[j] = Math.Cos(LatRad[j]); WSum += W[j] * NLon;
            }
        }

        // The size of one cell at the equator in metres.
        public double CellM { get { return A * 2.0 * Math.PI / NLon; } }

        public int NearestRow(double latRad)
        {
            int best = 0; double bd = 1e9;
            for (int j = 0; j < NLat; j++) { double d = Math.Abs(LatRad[j] - latRad); if (d < bd) { bd = d; best = j; } }
            return best;
        }

        public static double Dist(double a, double lat1, double lon1, double lat2, double lon2)
        {
            double sl = Math.Sin(0.5 * (lat2 - lat1)), so = Math.Sin(0.5 * (lon2 - lon1));
            double h = sl * sl + Math.Cos(lat1) * Math.Cos(lat2) * so * so;
            return 2.0 * a * Math.Asin(Math.Min(1.0, Math.Sqrt(h)));
        }

        public double DistCells(int j1, int i1, int j2, int i2) { return Dist(A, LatRad[j1], LonRad[i1], LatRad[j2], LonRad[i2]); }
    }

    public struct Minimum { public int J, I; public double Value, Lat, Lon, CloudRatio; }

    public sealed class Band
    {
        public double Lat, Lon, ExtentKm, Aspect, AxisDeg;
        public int Cells;
        public string Arm; // thermal, cwp, both
        public int Hemisphere;
    }

    // Detectors D1 (pressure minima) and D2 (front bands) of the header (section H2), as written there.
    public static class Detect
    {
        public const double BeltLo = 35.0, BeltHi = 70.0;
        public const double MaxBandWidthM = 200000.0;

        // D1. p is the surface pressure anomaly (Pa) on the grid; ps the surface pressure; ringKm the ring radius R.
        public static List<Minimum> Minima(MetricGrid g, double[] p, double ps, double ringKm, bool suppress)
        {
            int nl = g.NLon, nt = g.NLat;
            var q = new double[nl * nt];
            for (int j = 0; j < nt; j++)
            {
                double m = 0; for (int i = 0; i < nl; i++) m += p[j * nl + i]; m /= nl;
                for (int i = 0; i < nl; i++) q[j * nl + i] = p[j * nl + i] - m;
            }
            // box smoothing of radius half the ring radius (at least one cell): white noise is averaged out before the minima are looked for
            var sm = new double[nl * nt];
            var tmp = new double[nl * nt];
            double rs = 0.5 * ringKm * 1000.0;
            int rj = Math.Max(1, (int)Math.Round(rs / g.Dy[nt / 2]));
            for (int j = 0; j < nt; j++)
            {
                int ri = Math.Min(nl / 2 - 1, Math.Max(1, (int)Math.Round(rs / g.Dx[j])));
                for (int i = 0; i < nl; i++)
                {
                    double s = 0;
                    for (int d = -ri; d <= ri; d++) s += q[j * nl + (i + d + nl) % nl];
                    tmp[j * nl + i] = s / (2 * ri + 1);
                }
            }
            for (int j = 0; j < nt; j++)
                for (int i = 0; i < nl; i++)
                {
                    double s = 0; int n = 0;
                    for (int d = -rj; d <= rj; d++) { int jj = j + d; if (jj < 0 || jj >= nt) continue; s += tmp[jj * nl + i]; n++; }
                    sm[j * nl + i] = s / n;
                }
            var list = new List<Minimum>();
            double ringM = ringKm * 1000.0, need = 0.004 * ps;
            for (int j = 1; j < nt - 1; j++)
            {
                double lat = g.LatDeg[j];
                if (Math.Abs(lat) < BeltLo || Math.Abs(lat) > BeltHi) continue;
                for (int i = 0; i < nl; i++)
                {
                    double v = sm[j * nl + i];
                    bool low = true;
                    for (int dj = -1; dj <= 1 && low; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            if (dj == 0 && di == 0) continue;
                            if (sm[(j + dj) * nl + (i + di + nl) % nl] <= v) { low = false; break; }
                        }
                    if (!low) continue;
                    double ring = 0;
                    for (int k = 0; k < 16; k++)
                    {
                        double th = 2.0 * Math.PI * k / 16.0;
                        double la = g.LatRad[j] + ringM / g.A * Math.Sin(th);
                        la = Math.Max(-1.55, Math.Min(1.55, la));
                        double lo = g.LonRad[i] + ringM / (g.A * Math.Max(0.05, Math.Cos(g.LatRad[j]))) * Math.Cos(th);
                        int jr = g.NearestRow(la);
                        int ir = (int)Math.Round(lo / (2.0 * Math.PI) * nl); ir = ((ir % nl) + nl) % nl;
                        ring += sm[jr * nl + ir];
                    }
                    ring /= 16.0;
                    if (ring - v >= need) list.Add(new Minimum { J = j, I = i, Value = v, Lat = g.LatRad[j], Lon = g.LonRad[i] });
                }
            }
            if (!suppress) return list;
            list.Sort((x, y) => x.Value.CompareTo(y.Value));
            var kept = new List<Minimum>();
            foreach (var m in list)
            {
                bool near = false;
                foreach (var k in kept) if (MetricGrid.Dist(g.A, m.Lat, m.Lon, k.Lat, k.Lon) < ringM) { near = true; break; }
                if (!near) kept.Add(m);
            }
            return kept;
        }

        // D2. t is the temperature at sigma 0.8, cwp the column cloud-water path.
        public static double[] ZonalAnomaly(MetricGrid g, double[] f)
        {
            int nl = g.NLon, nt = g.NLat;
            var r = new double[f.Length];
            for (int j = 0; j < nt; j++)
            {
                double m = 0; for (int i = 0; i < nl; i++) m += f[j * nl + i]; m /= nl;
                for (int i = 0; i < nl; i++) r[j * nl + i] = f[j * nl + i] - m;
            }
            return r;
        }

        // 0.6 of the 95th percentile of the raw cloud-water field: the threshold of the cloud arm.
        public static double CloudThreshold(double[] cwp)
        {
            var sorted = (double[])cwp.Clone(); Array.Sort(sorted);
            return 0.6 * sorted[(int)(0.95 * (sorted.Length - 1))];
        }

        // D2 input (header H4, amended): the wave-borne anomaly, field minus the no-wave field of the same UT (tRef, cwpRef). Without a
        // reference field (planted tests, the sheets of a generator without a waves-off twin) the zonal mean is the reference.
        public static List<Band> Bands(MetricGrid g, double[] t, double[] cwp, double minExtentM, double[] tRef = null, double[] cwpRef = null)
        {
            double[] ta, ca;
            if (tRef != null) { ta = Diff(t, tRef); ca = Diff(cwp, cwpRef); }
            else { ta = ZonalAnomaly(g, t); ca = ZonalAnomaly(g, cwp); }
            return BandsFromAnomaly(g, ta, ca, CloudThreshold(cwp), minExtentM);
        }

        public static double[] Diff(double[] a, double[] b)
        {
            var r = new double[a.Length];
            for (int i = 0; i < r.Length; i++) r[i] = a[i] - b[i];
            return r;
        }

        // The band detector on anomaly fields: thermal arm 3 K per 100 km, cloud arm cwpThr per 100 km.
        public static List<Band> BandsFromAnomaly(MetricGrid g, double[] t, double[] cwp, double cwpThr, double minExtentM, List<int> acceptedCells = null)
        {
            int nl = g.NLon, nt = g.NLat;
            var flagT = new bool[nl * nt]; var flagC = new bool[nl * nt];
            for (int j = 1; j < nt - 1; j++)
            {
                if (Math.Abs(g.LatDeg[j]) < BeltLo || Math.Abs(g.LatDeg[j]) > BeltHi) continue;
                for (int i = 0; i < nl; i++)
                {
                    int ie = (i + 1) % nl, iw = (i - 1 + nl) % nl;
                    double dx = 2.0 * g.Dx[j], dy = g.A * (g.LatRad[j - 1] - g.LatRad[j + 1]);
                    double tx = (t[j * nl + ie] - t[j * nl + iw]) / dx, ty = (t[(j - 1) * nl + i] - t[(j + 1) * nl + i]) / dy;
                    double gt = Math.Sqrt(tx * tx + ty * ty) * 1e5; // K per 100 km
                    double cx = (cwp[j * nl + ie] - cwp[j * nl + iw]) / dx, cy = (cwp[(j - 1) * nl + i] - cwp[(j + 1) * nl + i]) / dy;
                    double gc = Math.Sqrt(cx * cx + cy * cy) * 1e5;
                    flagT[j * nl + i] = gt >= 3.0;
                    flagC[j * nl + i] = gc >= cwpThr;
                }
            }
            var seen = new bool[nl * nt];
            var bands = new List<Band>();
            var stack = new Stack<int>();
            for (int c0 = 0; c0 < nl * nt; c0++)
            {
                if (seen[c0] || !(flagT[c0] || flagC[c0])) continue;
                var cells = new List<int>();
                seen[c0] = true; stack.Push(c0);
                while (stack.Count > 0)
                {
                    int c = stack.Pop(); cells.Add(c);
                    int j = c / nl, i = c % nl;
                    for (int dj = -1; dj <= 1; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            int jj = j + dj; if (jj < 0 || jj >= nt) continue;
                            int cc = jj * nl + (i + di + nl) % nl;
                            if (seen[cc] || !(flagT[cc] || flagC[cc])) continue;
                            seen[cc] = true; stack.Push(cc);
                        }
                }
                if (cells.Count < 6) continue;
                // extent: largest great-circle distance over a deterministic subsample
                int stride = Math.Max(1, cells.Count / 120);
                double ext = 0;
                for (int a = 0; a < cells.Count; a += stride)
                    for (int b = a + stride; b < cells.Count; b += stride)
                        ext = Math.Max(ext, g.DistCells(cells[a] / nl, cells[a] % nl, cells[b] / nl, cells[b] % nl));
                if (ext < minExtentM) continue;
                // a band is thin: the mean width (area over extent) stays below MaxBandWidthM, which rejects blobs of flagged cells
                double area = 0; foreach (int c in cells) area += g.Dx[c / nl] * g.Dy[c / nl];
                if (area / ext > MaxBandWidthM) continue;
                // second moments in local km around the first cell's longitude
                int j0 = cells[0] / nl, i0 = cells[0] % nl;
                double mx = 0, my = 0; var xs = new double[cells.Count]; var ys = new double[cells.Count];
                for (int a = 0; a < cells.Count; a++)
                {
                    int j = cells[a] / nl, i = cells[a] % nl;
                    int di = i - i0; if (di > nl / 2) di -= nl; if (di < -nl / 2) di += nl;
                    xs[a] = di * g.Dx[j] / 1000.0; ys[a] = g.A * g.LatRad[j] / 1000.0;
                    mx += xs[a]; my += ys[a];
                }
                mx /= cells.Count; my /= cells.Count;
                double sxx = 0, syy = 0, sxy = 0;
                for (int a = 0; a < cells.Count; a++) { double x = xs[a] - mx, y = ys[a] - my; sxx += x * x; syy += y * y; sxy += x * y; }
                sxx /= cells.Count; syy /= cells.Count; sxy /= cells.Count;
                double tr = sxx + syy, det = sxx * syy - sxy * sxy;
                double disc = Math.Sqrt(Math.Max(0, tr * tr / 4 - det));
                double l1 = tr / 2 + disc, l2 = Math.Max(1e-9, tr / 2 - disc);
                double aspect = Math.Sqrt(l1 / l2);
                if (aspect < 3.0) continue;
                if (acceptedCells != null) acceptedCells.AddRange(cells);
                int nT = 0, nC = 0; foreach (int c in cells) { if (flagT[c]) nT++; if (flagC[c]) nC++; }
                double fT = (double)nT / cells.Count, fC = (double)nC / cells.Count;
                double cyLat = my * 1000.0 / g.A;
                double cxLon = g.LonRad[i0] + mx * 1000.0 / (g.A * Math.Max(0.05, Math.Cos(cyLat)));
                bands.Add(new Band
                {
                    Lat = cyLat, Lon = cxLon, ExtentKm = ext / 1000.0, Aspect = aspect, Cells = cells.Count,
                    AxisDeg = 0.5 * Math.Atan2(2 * sxy, sxx - syy) * 180.0 / Math.PI,
                    Arm = (fT >= 0.5 && fC >= 0.5) ? "both" : (fT >= fC ? "thermal" : "cwp"), Hemisphere = cyLat >= 0 ? 0 : 1
                });
            }
            return bands;
        }
    }

    // Greedy nearest-neighbour tracking of points over frames.
    public sealed class Track
    {
        public List<int> Frames = new List<int>();
        public List<double> Lat = new List<double>(), Lon = new List<double>();
        public int Hemisphere;
        public string Arm;
        public int Last { get { return Frames[Frames.Count - 1]; } }
        public int First { get { return Frames[0]; } }
    }

    public sealed class Tracker
    {
        private readonly double radiusM, a;
        private readonly int maxGap;
        public readonly List<Track> Tracks = new List<Track>();
        private readonly List<Track> active = new List<Track>();
        public long Swaps;

        public Tracker(double earthRadius, double radiusMeters, int maxMissing) { a = earthRadius; radiusM = radiusMeters; maxGap = maxMissing; }

        public void Add(int frame, List<double[]> pts, List<string> arms)
        {
            // pts: {lat, lon, hemisphere}
            active.RemoveAll(t => frame - t.Last > maxGap + 1);
            var pairs = new List<(double d, int ti, int pi)>();
            for (int ti = 0; ti < active.Count; ti++)
                for (int pi = 0; pi < pts.Count; pi++)
                {
                    if ((int)pts[pi][2] != active[ti].Hemisphere) continue;
                    double d = MetricGrid.Dist(a, active[ti].Lat[active[ti].Lat.Count - 1], active[ti].Lon[active[ti].Lon.Count - 1], pts[pi][0], pts[pi][1]);
                    if (d <= radiusM) pairs.Add((d, ti, pi));
                }
            pairs.Sort((x, y) => x.d.CompareTo(y.d));
            var usedT = new bool[active.Count]; var usedP = new bool[pts.Count];
            foreach (var pr in pairs)
            {
                if (usedT[pr.ti] || usedP[pr.pi]) continue;
                usedT[pr.ti] = true; usedP[pr.pi] = true;
                // swap check: another active track whose last position is closer to this detection
                for (int o = 0; o < active.Count; o++)
                {
                    if (o == pr.ti || active[o].Hemisphere != active[pr.ti].Hemisphere) continue;
                    double d2 = MetricGrid.Dist(a, active[o].Lat[active[o].Lat.Count - 1], active[o].Lon[active[o].Lon.Count - 1], pts[pr.pi][0], pts[pr.pi][1]);
                    if (d2 < pr.d) { Swaps++; break; }
                }
                var t = active[pr.ti];
                t.Frames.Add(frame); t.Lat.Add(pts[pr.pi][0]); t.Lon.Add(pts[pr.pi][1]);
            }
            for (int pi = 0; pi < pts.Count; pi++)
            {
                if (usedP[pi]) continue;
                var t = new Track { Hemisphere = (int)pts[pi][2], Arm = arms != null ? arms[pi] : null };
                t.Frames.Add(frame); t.Lat.Add(pts[pi][0]); t.Lon.Add(pts[pi][1]);
                Tracks.Add(t); active.Add(t);
            }
        }

        // Tracks that last at least minFrames frame steps (Last - First).
        public List<Track> Persistent(int minFrameSpan)
        {
            var r = new List<Track>();
            foreach (var t in Tracks) if (t.Last - t.First >= minFrameSpan) r.Add(t);
            return r;
        }

        // Mean number of simultaneously active persistent tracks per frame, per hemisphere, over the frames [f0, f1).
        public double[] MeanActive(int minFrameSpan, int f0, int f1)
        {
            var cnt = new double[2];
            foreach (var t in Persistent(minFrameSpan))
            {
                int a0 = Math.Max(t.First, f0), a1 = Math.Min(t.Last, f1 - 1);
                if (a1 >= a0) cnt[t.Hemisphere] += a1 - a0 + 1;
            }
            double n = Math.Max(1, f1 - f0);
            cnt[0] /= n; cnt[1] /= n;
            return cnt;
        }

        // Tracker numbers: lifetime, east drift (m/s), straightness of the persistent tracks of one hemisphere (or both: -1).
        // Tracks that start before excludeBefore or end at or after excludeAfter (frame indices) are left out (header H4: edge tracks of a run);
        // jetMs > 0 adds the per-track shares: east drift within 30 % of the jet speed, and eastward.
        public Dictionary<string, object> Numbers(int minFrameSpan, double frameSeconds, int hemi, int excludeBefore = 0, int excludeAfter = int.MaxValue, double jetMs = 0.0)
        {
            var life = new List<double>(); var drift = new List<double>(); var straight = new List<double>();
            foreach (var t in Persistent(minFrameSpan))
            {
                if (hemi >= 0 && t.Hemisphere != hemi) continue;
                if (t.First < excludeBefore || t.Last >= excludeAfter) continue;
                double dur = (t.Last - t.First) * frameSeconds;
                double path = 0;
                for (int k = 1; k < t.Lat.Count; k++) path += MetricGrid.Dist(a, t.Lat[k - 1], t.Lon[k - 1], t.Lat[k], t.Lon[k]);
                // unwrapped displacement: the sum of the per-step east and north displacements (a track may circle the body)
                double east = 0, north = 0;
                for (int k = 1; k < t.Lat.Count; k++)
                {
                    double dl = t.Lon[k] - t.Lon[k - 1];
                    while (dl > Math.PI) dl -= 2 * Math.PI; while (dl < -Math.PI) dl += 2 * Math.PI;
                    east += a * Math.Cos(0.5 * (t.Lat[k] + t.Lat[k - 1])) * dl;
                    north += a * (t.Lat[k] - t.Lat[k - 1]);
                }
                double net = Math.Sqrt(east * east + north * north);
                life.Add(dur); drift.Add(east / Math.Max(1.0, dur)); straight.Add(path > 0 ? net / path : 1.0);
            }
            var r = new Dictionary<string, object>();
            r["count"] = life.Count;
            r["lifetime_h_mean"] = Mean(life) / 3600.0; r["lifetime_h_median"] = Median(life) / 3600.0; r["lifetime_h_max"] = Max(life) / 3600.0;
            r["east_drift_ms_mean"] = Mean(drift); r["east_drift_ms_median"] = Median(drift);
            r["straightness_mean"] = Mean(straight); r["straightness_median"] = Median(straight);
            r["swaps"] = Swaps;
            if (jetMs > 0.0)
            {
                int within = 0, east = 0;
                foreach (double d in drift) { if (Math.Abs(d - jetMs) <= 0.3 * jetMs) within++; if (d > 0.0) east++; }
                r["share_drift_within_30pct_of_jet"] = drift.Count > 0 ? (double)within / drift.Count : double.NaN;
                r["share_eastward"] = drift.Count > 0 ? (double)east / drift.Count : double.NaN;
            }
            return r;
        }

        public static double Mean(List<double> x) { if (x.Count == 0) return double.NaN; double s = 0; foreach (double v in x) s += v; return s / x.Count; }
        public static double Max(List<double> x) { if (x.Count == 0) return double.NaN; double m = double.MinValue; foreach (double v in x) m = Math.Max(m, v); return m; }
        public static double Median(List<double> x) { if (x.Count == 0) return double.NaN; var c = new List<double>(x); c.Sort(); return c[c.Count / 2]; }
    }

    // Everything the two detectors and their controls need from one set of frames.
    public sealed class FrontsAndLows
    {
        private readonly MetricGrid g;
        private readonly double ps, ringKm, frameSeconds, rotationS;
        public readonly Tracker D1, D1NoSuppress, D2, C1a, C2a, C1b, C2b;
        public readonly List<List<Minimum>> MinimaByFrame = new List<List<Minimum>>();
        public readonly List<List<Band>> BandsByFrame = new List<List<Band>>();
        // A track counts as persistent when it spans at least this many seconds (header H2: one day).
        public double PersistS = 86400.0;
        public int Frames;
        public long Minima, MinimaRaw, MinimaControlShuffle, MinimaControlNoise;
        private readonly Random rnd = new Random(12345);
        public readonly long[] ArmCount = new long[3];

        private readonly double minExtentM, trackRadiusM;
        private double cloudRatioSum; private long cloudRatioN;
        // R_system of the body (set by the caller; the default is twice the ring radius): the front-to-system association distance is 1.5 of it.
        public double SystemRadiusM;

        // trackSpeedMs: the largest drift speed the trackers follow (about 1.5 times the jet speed); minExtentCells: length of a front band.
        public FrontsAndLows(int nlon, int nlat, double radiusM, double ps, double frameSeconds, double rotationS, double ringKm, double trackSpeedMs, int minExtentCells)
        {
            g = new MetricGrid(nlon, nlat, radiusM); this.ps = ps; this.frameSeconds = frameSeconds; this.rotationS = rotationS;
            this.ringKm = ringKm;
            minExtentM = minExtentCells * g.CellM;
            double r1 = trackSpeedMs * frameSeconds;
            trackRadiusM = r1; SystemRadiusM = 2.0 * ringKm * 1000.0;
            D1 = new Tracker(radiusM, r1, 1);
            D1NoSuppress = new Tracker(radiusM, r1, 1);
            D2 = new Tracker(radiusM, r1, 1);
            C1a = new Tracker(radiusM, r1, 1); C2a = new Tracker(radiusM, r1, 1);
            C1b = new Tracker(radiusM, r1, 1); C2b = new Tracker(radiusM, r1, 1);
        }

        public double RingKm { get { return ringKm; } }

        private static List<double[]> Pts(List<Minimum> m)
        {
            var r = new List<double[]>();
            foreach (var x in m) r.Add(new[] { x.Lat, x.Lon, x.Lat >= 0 ? 0.0 : 1.0 });
            return r;
        }

        private static List<double[]> Pts(List<Band> b, List<string> arms)
        {
            var r = new List<double[]>();
            foreach (var x in b) { r.Add(new[] { x.Lat, x.Lon, (double)x.Hemisphere }); arms.Add(x.Arm); }
            return r;
        }

        // One frame. p: pressure anomaly (Pa), t: surface air temperature (K), cwp: cloud water proxy; all on the metric grid.
        // tRef, cwpRef: the no-wave fields of the same UT; D2 reads t - tRef and cwp - cwpRef (header H4, amended). Null: zonal-mean reference.
        public void Frame(double[] p, double[] t, double[] cwp, bool collect, double[] tRef = null, double[] cwpRef = null)
        {
            int n2 = p.Length;
            int f = Frames++;
            var mins = Detect.Minima(g, p, ps, ringKm, true);
            var minsRaw = Detect.Minima(g, p, ps, ringKm, false);
            double cwThr = Detect.CloudThreshold(cwp);
            // REPORT (G-14): the cloud water within R_system of each minimum against the zonal mean of its row, what a player would see of the system
            for (int q = 0; q < mins.Count; q++)
            {
                var m = mins[q];
                int nl = g.NLon; double rowMean = 0; for (int i = 0; i < nl; i++) rowMean += cwp[m.J * nl + i]; rowMean /= nl;
                int rj = (int)Math.Ceiling(SystemRadiusM / g.Dy[m.J]), ri = (int)Math.Ceiling(SystemRadiusM / g.Dx[m.J]);
                double s = 0; int cnt = 0;
                for (int dj = -rj; dj <= rj; dj++)
                {
                    int jj = m.J + dj; if (jj < 0 || jj >= g.NLat) continue;
                    for (int di = -ri; di <= ri; di++)
                    {
                        int ii = ((m.I + di) % nl + nl) % nl;
                        if (MetricGrid.Dist(g.A, m.Lat, m.Lon, g.LatRad[jj], g.LonRad[ii]) > SystemRadiusM) continue;
                        s += cwp[jj * nl + ii]; cnt++;
                    }
                }
                m.CloudRatio = cnt > 0 && rowMean > 1e-9 ? s / cnt / rowMean : double.NaN;
                mins[q] = m;
                if (!double.IsNaN(m.CloudRatio)) { cloudRatioSum += m.CloudRatio; cloudRatioN++; }
            }
            double[] ta = tRef != null ? Detect.Diff(t, tRef) : Detect.ZonalAnomaly(g, t);
            double[] ca = tRef != null ? Detect.Diff(cwp, cwpRef) : Detect.ZonalAnomaly(g, cwp);
            var bands = Detect.BandsFromAnomaly(g, ta, ca, cwThr, minExtentM);
            if (collect) { MinimaByFrame.Add(mins); BandsByFrame.Add(bands); }
            D1.Add(f, Pts(mins), null); D1NoSuppress.Add(f, Pts(minsRaw), null);
            var arms = new List<string>();
            D2.Add(f, Pts(bands, arms), arms);
            // Negative control (a): every field spatially shuffled with one permutation. (b): white noise with the field's mean and standard deviation.
            var perm = new int[n2]; for (int i = 0; i < n2; i++) perm[i] = i;
            for (int i = n2 - 1; i > 0; i--) { int k = rnd.Next(i + 1); int tmp = perm[i]; perm[i] = perm[k]; perm[k] = tmp; }
            var ps1 = new double[n2]; var t1 = new double[n2]; var c1 = new double[n2];
            for (int i = 0; i < n2; i++) { ps1[i] = p[perm[i]]; t1[i] = ta[perm[i]]; c1[i] = ca[perm[i]]; }
            var m1 = Detect.Minima(g, ps1, ps, ringKm, true);
            var b1 = Detect.BandsFromAnomaly(g, t1, c1, cwThr, minExtentM);
            C1a.Add(f, Pts(m1), null); var ar1 = new List<string>(); C2a.Add(f, Pts(b1, ar1), ar1);
            var ps2 = Noise(p); var t2 = Noise(ta); var c2 = Noise(ca);
            var m2 = Detect.Minima(g, ps2, ps, ringKm, true);
            var b2 = Detect.BandsFromAnomaly(g, t2, c2, cwThr, minExtentM);
            C1b.Add(f, Pts(m2), null); var ar2 = new List<string>(); C2b.Add(f, Pts(b2, ar2), ar2);
        }

        private double[] Noise(double[] x)
        {
            double m = 0, v = 0; foreach (double a in x) m += a; m /= x.Length;
            foreach (double a in x) v += (a - m) * (a - m); double sd = Math.Sqrt(v / x.Length);
            var r = new double[x.Length];
            for (int i = 0; i < x.Length; i++)
            {
                double u1 = Math.Max(1e-12, rnd.NextDouble()), u2 = rnd.NextDouble();
                r[i] = m + sd * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            }
            return r;
        }

        // Summary over frames [f0, f1) as the header defines it. Day = one rotation period of the body.
        // totalFrames > 0 and edgeFrames: tracks touching the first or last edgeFrames frames of the run are left out of the lifetime/drift statistics;
        // jetN, jetS: the mean jet speed of the hemispheres over the run (m/s), for the per-track shares and the front association.
        public Dictionary<string, object> Summary(int f0, int f1, int totalFrames = -1, int edgeFrames = 0, double jetN = 0.0, double jetS = 0.0)
        {
            int persist = (int)Math.Round(PersistS / frameSeconds);
            int exB = totalFrames > 0 ? edgeFrames : 0, exA = totalFrames > 0 ? totalFrames - edgeFrames : int.MaxValue;
            var r = new Dictionary<string, object>();
            var act = D1.MeanActive(persist, f0, f1); var actRaw = D1NoSuppress.MeanActive(persist, f0, f1);
            r["d1_persistent_active_mean_per_hemisphere_north"] = act[0]; r["d1_persistent_active_mean_per_hemisphere_south"] = act[1];
            r["d1_persistent_active_mean_no_suppression_north"] = actRaw[0]; r["d1_persistent_active_mean_no_suppression_south"] = actRaw[1];
            r["d1_ring_radius_km"] = ringKm;
            r["d1_tracker_north"] = D1.Numbers(persist, frameSeconds, 0, exB, exA, jetN); r["d1_tracker_south"] = D1.Numbers(persist, frameSeconds, 1, exB, exA, jetS);
            double durDays = (f1 - f0) * frameSeconds / rotationS;
            var ev = EventCounts(D2, persist, f0, f1);
            r["d2_events_north"] = ev[0]; r["d2_events_south"] = ev[1];
            r["d2_events_per_10_body_days_north"] = ev[0] / durDays * 10.0; r["d2_events_per_10_body_days_south"] = ev[1] / durDays * 10.0;
            r["d2_events_per_10_earth_days_north"] = ev[0] / (durDays * rotationS / 86400.0) * 10.0; r["d2_events_per_10_earth_days_south"] = ev[1] / (durDays * rotationS / 86400.0) * 10.0;
            r["d2_duration_body_days"] = durDays;
            var arm = new Dictionary<string, object>();
            int th = 0, cw = 0, bo = 0;
            foreach (var t in D2.Persistent(persist)) { if (t.First < f0 || t.First >= f1) continue; if (t.Arm == "thermal") th++; else if (t.Arm == "cwp") cw++; else bo++; }
            arm["thermal"] = th; arm["cwp"] = cw; arm["both"] = bo;
            r["d2_event_arms"] = arm;
            r["d2_tracker_north"] = D2.Numbers(persist, frameSeconds, 0, exB, exA, jetN); r["d2_tracker_south"] = D2.Numbers(persist, frameSeconds, 1, exB, exA, jetS);
            r["d2_free_tracks_with_system_north"] = WithSystem(persist, f0, f1, 0, jetN); r["d2_free_tracks_with_system_south"] = WithSystem(persist, f0, f1, 1, jetS);
            if (MinimaByFrame.Count > 0) { r["d2_events_assigned_north"] = Events(f0, f1, 0, jetN); r["d2_events_assigned_south"] = Events(f0, f1, 1, jetS); }
            var ca = C1a.MeanActive(persist, f0, f1); var cb = C1b.MeanActive(persist, f0, f1);
            r["control_shuffle_d1_per_hemisphere"] = Math.Max(ca[0], ca[1]); r["control_noise_d1_per_hemisphere"] = Math.Max(cb[0], cb[1]);
            var ea = EventCounts(C2a, persist, f0, f1); var eb = EventCounts(C2b, persist, f0, f1);
            r["control_shuffle_d2_events_per_10_body_days"] = Math.Max(ea[0], ea[1]) / durDays * 10.0; r["control_noise_d2_events_per_10_body_days"] = Math.Max(eb[0], eb[1]) / durDays * 10.0;
            r["control_shuffle_d2_events_total"] = ea[0] + ea[1]; r["control_noise_d2_events_total"] = eb[0] + eb[1];
            r["real_d2_events_total"] = ev[0] + ev[1];
            // time-order shuffle of the real minima (an extra, not a control for D2)
            var order = new List<int>(); for (int f = f0; f < f1 && f < MinimaByFrame.Count; f++) order.Add(f);
            var rr = new Random(777);
            for (int i = order.Count - 1; i > 0; i--) { int k = rr.Next(i + 1); int tmp = order[i]; order[i] = order[k]; order[k] = tmp; }
            if (MinimaByFrame.Count > 0)
            {
                // same tracker radius as the real D1 tracker
                var tt = new Tracker(g.A, trackRadiusM, 1);
                for (int k = 0; k < order.Count; k++) tt.Add(k, Pts(MinimaByFrame[order[k]]), null);
                var sh = tt.MeanActive(persist, 0, order.Count);
                r["time_shuffled_d1_per_hemisphere_north"] = sh[0]; r["time_shuffled_d1_per_hemisphere_south"] = sh[1];
            }
            r["cloud_water_ratio_at_minima_mean"] = cloudRatioN > 0 ? cloudRatioSum / cloudRatioN : double.NaN;
            r["frames"] = f1 - f0; r["frame_seconds"] = frameSeconds;
            return r;
        }

        // Association of the persistent front tracks of one hemisphere with the persistent D1 tracks (header H5 criterion 2): a front frame is
        // associated when its centroid lies within 1.5 R_system of a persistent D1 track position of the same frame; a front track moves with
        // its system when at least half of its frames are associated and the median east-velocity difference to the associated D1 track
        // (over consecutive frames associated to the same D1 track) is at most 0.3 of the jet speed.
        private Dictionary<string, object> WithSystem(int persist, int f0, int f1, int hemi, double jetMs)
        {
            var d1 = new Dictionary<int, List<(int id, double lat, double lon)>>();
            int idc = 0;
            foreach (var t in D1.Persistent(persist))
            {
                if (t.Hemisphere != hemi) { idc++; continue; }
                for (int k = 0; k < t.Frames.Count; k++)
                {
                    List<(int, double, double)> l;
                    if (!d1.TryGetValue(t.Frames[k], out l)) { l = new List<(int, double, double)>(); d1[t.Frames[k]] = l; }
                    l.Add((idc, t.Lat[k], t.Lon[k]));
                }
                idc++;
            }
            double reach = 1.5 * SystemRadiusM;
            int tracks = 0, moving = 0, orphanFrames = 0, frontFrames = 0;
            var rel = new List<double>();
            foreach (var t in D2.Persistent(persist))
            {
                if (t.Hemisphere != hemi || t.First < f0 || t.First >= f1) continue;
                tracks++;
                int assoc = 0; var ids = new int[t.Frames.Count];
                for (int k = 0; k < t.Frames.Count; k++)
                {
                    ids[k] = -1; double best = reach;
                    List<(int id, double lat, double lon)> l;
                    if (d1.TryGetValue(t.Frames[k], out l))
                        foreach (var m in l) { double d = MetricGrid.Dist(g.A, t.Lat[k], t.Lon[k], m.lat, m.lon); if (d <= best) { best = d; ids[k] = m.id; } }
                    frontFrames++;
                    if (ids[k] >= 0) assoc++; else orphanFrames++;
                }
                var mine = new List<double>();
                // positions of the associated D1 track by frame, for the velocity difference
                for (int k = 1; k < t.Frames.Count; k++)
                {
                    if (ids[k] < 0 || ids[k] != ids[k - 1] || t.Frames[k] - t.Frames[k - 1] != 1) continue;
                    var a0 = D1PosOf(ids[k], t.Frames[k - 1], persist); var a1 = D1PosOf(ids[k], t.Frames[k], persist);
                    if (a0 == null || a1 == null) continue;
                    double dtS = frameSeconds;
                    double vf = EastStep(t.Lat[k - 1], t.Lon[k - 1], t.Lat[k], t.Lon[k]) / dtS;
                    double vd = EastStep(a0[0], a0[1], a1[0], a1[1]) / dtS;
                    mine.Add(Math.Abs(vf - vd));
                }
                double med = Tracker.Median(mine);
                bool ok = assoc * 2 >= t.Frames.Count && !double.IsNaN(med) && med <= 0.3 * Math.Max(jetMs, 1.0);
                if (ok) moving++;
                if (!double.IsNaN(med)) rel.Add(med);
            }
            return new Dictionary<string, object>
            {
                { "front_tracks", tracks }, { "moving_with_system", moving },
                { "share_moving_with_system", tracks > 0 ? (double)moving / tracks : double.NaN },
                { "median_relative_east_speed_ms", Tracker.Median(rel) },
                { "orphan_front_frame_share", frontFrames > 0 ? (double)orphanFrames / frontFrames : double.NaN }
            };
        }

        // The front EVENTS of the gate (header H4 and H5 criterion 2, amended): every band of a frame is assigned to the nearest persistent D1 track
        // position of its hemisphere within 1.5 R_system (a band with none is an orphan: reported, never counted). An event is a run (at most one
        // frame missing) of frames in which a band is assigned to the same D1 track; it is persistent when it spans at least PersistS. It moves with
        // its system when the standard deviation of the band-centroid offset from the D1 position over the event is at most 0.75 R_system. The event's
        // east drift is the D1 track's unwrapped east displacement over the event plus the net change of the offset, divided by the duration.
        public Dictionary<string, object> Events(int f0, int f1, int hemi, double jetMs)
        {
            int persist = (int)Math.Round(PersistS / frameSeconds);
            var d1 = D1.Persistent(persist);
            double reach = 1.5 * SystemRadiusM;
            // per D1 track: frame -> (nearest assigned band offset east, north in metres, band-to-D1 distance)
            var perTrack = new Dictionary<int, SortedDictionary<int, double[]>>();
            int bandsTotal = 0, orphans = 0;
            for (int f = f0; f < f1 && f < BandsByFrame.Count; f++)
            {
                foreach (var b in BandsByFrame[f])
                {
                    if (b.Hemisphere != hemi) continue;
                    bandsTotal++;
                    int bestId = -1; double bestD = reach; double bl = 0, bo = 0;
                    for (int id = 0; id < d1.Count; id++)
                    {
                        var t = d1[id]; if (t.Hemisphere != hemi) continue;
                        int k = t.Frames.IndexOf(f); if (k < 0) continue;
                        double d = MetricGrid.Dist(g.A, b.Lat, b.Lon, t.Lat[k], t.Lon[k]);
                        if (d <= bestD) { bestD = d; bestId = id; bl = t.Lat[k]; bo = t.Lon[k]; }
                    }
                    if (bestId < 0) { orphans++; continue; }
                    SortedDictionary<int, double[]> m;
                    if (!perTrack.TryGetValue(bestId, out m)) { m = new SortedDictionary<int, double[]>(); perTrack[bestId] = m; }
                    double dl = b.Lon - bo; while (dl > Math.PI) dl -= 2 * Math.PI; while (dl < -Math.PI) dl += 2 * Math.PI;
                    double oe = g.A * Math.Cos(0.5 * (b.Lat + bl)) * dl, on = g.A * (b.Lat - bl);
                    double[] old;
                    if (!m.TryGetValue(f, out old) || bestD < old[2]) m[f] = new[] { oe, on, bestD };
                }
            }
            int events = 0, withSystem = 0;
            var drifts = new List<double>(); var spreads = new List<double>(); var spans = new List<double>();
            foreach (var kv in perTrack)
            {
                var t = d1[kv.Key];
                var fr = new List<int>(kv.Value.Keys);
                int s = 0;
                while (s < fr.Count)
                {
                    int e = s;
                    while (e + 1 < fr.Count && fr[e + 1] - fr[e] <= 2) e++;
                    double spanS = (fr[e] - fr[s]) * frameSeconds;
                    if (fr[s] >= f0 && fr[s] < f1 && spanS >= PersistS - 1e-6)
                    {
                        events++;
                        double me = 0, mn = 0; int n = e - s + 1;
                        for (int q = s; q <= e; q++) { me += kv.Value[fr[q]][0]; mn += kv.Value[fr[q]][1]; }
                        me /= n; mn /= n;
                        double v = 0;
                        for (int q = s; q <= e; q++) { double x = kv.Value[fr[q]][0] - me, y = kv.Value[fr[q]][1] - mn; v += x * x + y * y; }
                        double sd = Math.Sqrt(v / n);
                        double east = 0;
                        int k0 = t.Frames.IndexOf(fr[s]), k1 = t.Frames.IndexOf(fr[e]);
                        for (int k = k0 + 1; k <= k1; k++) east += EastStep(t.Lat[k - 1], t.Lon[k - 1], t.Lat[k], t.Lon[k]);
                        east += kv.Value[fr[e]][0] - kv.Value[fr[s]][0];
                        drifts.Add(east / Math.Max(1.0, spanS)); spreads.Add(sd); spans.Add(spanS / 3600.0);
                        if (sd <= 0.75 * SystemRadiusM) withSystem++;
                    }
                    s = e + 1;
                }
            }
            return new Dictionary<string, object>
            {
                { "events", events }, { "with_system", withSystem },
                { "share_with_system", events > 0 ? (double)withSystem / events : double.NaN },
                { "east_drift_ms_median", Tracker.Median(drifts) }, { "offset_spread_km_median", Tracker.Median(spreads) / 1000.0 },
                { "duration_h_median", Tracker.Median(spans) },
                { "bands_total", bandsTotal }, { "orphan_band_share", bandsTotal > 0 ? (double)orphans / bandsTotal : double.NaN }
            };
        }

        private double[] D1PosOf(int id, int frame, int persist)
        {
            int c = 0;
            foreach (var t in D1.Persistent(persist))
            {
                if (c++ != id) continue;
                int k = t.Frames.IndexOf(frame);
                return k < 0 ? null : new[] { t.Lat[k], t.Lon[k] };
            }
            return null;
        }

        private double EastStep(double lat0, double lon0, double lat1, double lon1)
        {
            double dl = lon1 - lon0;
            while (dl > Math.PI) dl -= 2 * Math.PI; while (dl < -Math.PI) dl += 2 * Math.PI;
            return g.A * Math.Cos(0.5 * (lat0 + lat1)) * dl;
        }

        private static double[] EventCounts(Tracker t, int persist, int f0, int f1)
        {
            var c = new double[2];
            foreach (var x in t.Persistent(persist)) if (x.First >= f0 && x.First < f1) c[x.Hemisphere]++;
            return c;
        }
    }
}
