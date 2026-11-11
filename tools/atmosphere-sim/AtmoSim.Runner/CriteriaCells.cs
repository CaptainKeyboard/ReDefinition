using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AtmoSim.Core;

namespace AtmoSim.Runner
{
    // Criterion 11 (convective cells) as the header defines it, measured on the generator's field:
    //  * size and lifetime from 1 km rasters of 150 km patches followed at 10-minute steps in the lattice frame (the frame that drifts with the steering wind of the lattice rows);
    //  * density per unstable area from the same patches;
    //  * the front association from 2 km rasters of the whole belt 35 to 70 degrees at snapshots, against the accepted front-band cells of the D2 detector.
    public static class CriteriaCells
    {
        private sealed class CellTrack { public int First, Last; public double Peak, Area37, Cx, Cy; public List<int> Pixels; public bool Instab; }

        private static double Rad(double deg) { return deg * Math.PI / 180.0; }

        public static int Run(string[] args)
        {
            string dir = args[1]; string mode = Criteria.ModeName;
            var rep = new Report(11, "cells_" + mode);
            var res = new Dictionary<ulong, double[]>(); // seed -> {diamMedian, diamP5, diamP95, lifeMedianH, lifeP5, lifeP95, nCells, truncated, density, frontDensity, share, enrichment, unstableAreaKm2}
            var tasks = new List<Task>();
            for (ulong s = 1; s <= 3; s++)
            {
                ulong seed = s;
                tasks.Add(Task.Run(() => { var v = Measure(seed); lock (res) res[seed] = v; }));
            }
            Task.WaitAll(tasks.ToArray());
            for (ulong s = 1; s <= 3; s++)
            {
                var v = res[s]; string sd = s == 1 ? "A" : s == 2 ? "B" : "C";
                bool eval = v[6] >= 20;
                rep.Add("footprint diameter at the life maximum, median (km) of " + Criteria.Fmt(v[6]) + " cells (5 % / 95 %: " + Criteria.Fmt(v[1]) + " / " + Criteria.Fmt(v[2]) + "; " + Criteria.Fmt(v[7]) + " margin-truncated cells excluded)", v[0], "5 .. 25", eval && v[0] >= 5.0 && v[0] <= 25.0, true, mode, sd);
                rep.Add("lifetime, median (h) (5 % / 95 %: " + Criteria.Fmt(v[4]) + " / " + Criteria.Fmt(v[5]) + ")", v[3], "1 .. 3", eval && v[3] >= 1.0 && v[3] <= 3.0, true, mode, sd);
                rep.Add("density per 10 000 km2 of area where the instability is at least 0.5 (" + Criteria.Fmt(v[12]) + " km2 of patch area, cells present per 10-minute step)", v[8], "0.2 .. 8", !double.IsNaN(v[8]) && v[8] >= 0.2 && v[8] <= 8.0, true, mode, sd);
                rep.Add("cells (strength >= 0.37) per 100 km of front length, within 50 km of an accepted front-band cell", v[9], "0.3 .. 10", !double.IsNaN(v[9]) && v[9] >= 0.3 && v[9] <= 10.0, true, mode, sd);
                rep.Add("share of cell centres within 100 km of an accepted front-band cell (enrichment over uniform points " + Criteria.Fmt(v[11]) + ")", v[10], ">= 0.6", !double.IsNaN(v[10]) && v[10] >= 0.6, true, mode, sd);
            }
            rep.Save(dir);
            return 0;
        }

        // Steering speed of a lattice row and the angular shift of the lattice frame at a time (the formulas of the generator's cell lattice).
        private static double Shift(WeatherGenerator g, double lat, double ut, out double rowLat)
        {
            double a = g.Body.RadiusM, rowH = g.D.CellLatticeM / a;
            long j = (long)Math.Floor(lat / rowH);
            double latC = (j + 0.5) * rowH, latAbs = Math.Abs(latC), cosC = Math.Cos(latC);
            double idx = latAbs * (180.0 / Math.PI) * 2.0; int i0 = (int)idx; if (i0 > 179) i0 = 179;
            double tt = idx - i0;
            double steer = g.D.SteerMs[i0] * (1.0 - tt) + g.D.SteerMs[i0 + 1] * tt;
            rowLat = latC;
            return DMath.WrapPi(steer * ut / (a * cosC));
        }

        private static double[] Measure(ulong seed)
        {
            var bp = Study.ByName("kerbin"); var g = new WeatherGenerator(bp, seed);
            double a = bp.RadiusM;
            var diam = new List<double>(); var life = new List<double>();
            double trunc = 0, cellSteps = 0, unstableKm2Steps = 0, totalPatchKm2 = 0;
            // ---- patches
            double[] pLat = { 45, 55, 65 };
            object lk = new object();
            var tasks = new List<Task>();
            for (int h = 0; h < 2; h++)
                foreach (double pl in pLat)
                {
                    int hh = h; double plat = hh == 0 ? pl : -pl;
                    tasks.Add(Task.Run(() =>
                    {
                        var d = new List<double>(); var l = new List<double>(); double tr = 0, cs = 0, uk = 0, tk = 0;
                        PatchRun(g, bp, plat, Rad((hh * 130 + (int)pl * 3) % 360 - 180.0), d, l, ref tr, ref cs, ref uk, ref tk);
                        lock (lk) { diam.AddRange(d); life.AddRange(l); trunc += tr; cellSteps += cs; unstableKm2Steps += uk; totalPatchKm2 += tk; }
                    }));
                }
            // ---- belt snapshots
            double fCells = 0, fLen = 0, near50 = 0, centres = 0, within100 = 0, uniform = 0, uniformIn = 0;
            tasks.Add(Task.Run(() =>
            {
                double c0 = 0, fl = 0, n50 = 0, ct = 0, w100 = 0, u = 0, ui = 0;
                Snapshots(g, bp, ref c0, ref fl, ref n50, ref ct, ref w100, ref u, ref ui);
                lock (lk) { fCells = c0; fLen = fl; near50 = n50; centres = ct; within100 = w100; uniform = u; uniformIn = ui; }
            }));
            Task.WaitAll(tasks.ToArray());
            diam.Sort(); life.Sort();
            Func<List<double>, double, double> pct = (v, p) => v.Count == 0 ? double.NaN : v[(int)(p * (v.Count - 1))];
            double dens = unstableKm2Steps > 0 ? cellSteps / (unstableKm2Steps / 1.0e4) : double.NaN;
            double fd = fLen > 0 ? near50 / (fLen / 100.0) : double.NaN;
            double share = centres > 0 ? within100 / centres : double.NaN;
            double enr = uniform > 0 && uniformIn > 0 ? share / (uniformIn / uniform) : double.NaN;
            return new[] { pct(diam, 0.5), pct(diam, 0.05), pct(diam, 0.95), pct(life, 0.5), pct(life, 0.05), pct(life, 0.95), diam.Count, trunc, dens, fd, share, enr, unstableKm2Steps / Math.Max(1, 1) };
        }

        private static void PatchRun(WeatherGenerator g, BodyParams bp, double latDeg, double lon0, List<double> diam, List<double> life, ref double truncated, ref double cellSteps, ref double unstableKm2Steps, ref double totalKm2)
        {
            const int N = 150; const double px = 1000.0; // 1 km
            double a = bp.RadiusM, lat0 = Rad(latDeg);
            var w = new Weather();
            var cur = new float[N * N]; var inst = new float[N * N];
            var curLab = new int[N * N]; var prevLab = new int[N * N];
            double bucketS = WeatherGenerator.CellHourBucketS;
            for (int win = 0; win < 20; win++)
            {
                double t0 = Math.Floor((win * 3.0 * 86400.0 + 4000.0) / bucketS) * bucketS;
                var tracks = new List<CellTrack>(); var active = new Dictionary<int, CellTrack>();
                Array.Clear(prevLab, 0, prevLab.Length);
                for (int step = 0; step <= 36; step++)
                {
                    double ut = t0 + step * 600.0;
                    for (int y = 0; y < N; y++)
                    {
                        double lat = lat0 + (y - N / 2 + 0.5) * px / a;
                        double rowLat; double sh = Shift(g, lat, ut, out rowLat);
                        double cosL = Math.Cos(lat);
                        for (int x = 0; x < N; x++)
                        {
                            double lonL = lon0 + (x - N / 2 + 0.5) * px / (a * cosL);
                            g.Sample(lat, lonL + sh, 0.0, ut, out w);
                            cur[y * N + x] = (float)w.CellStrength; inst[y * N + x] = (float)w.Instability;
                        }
                    }
                    // unstable area and totals of this step
                    double un = 0; for (int i = 0; i < N * N; i++) if (inst[i] >= 0.5) un += 1.0;
                    totalKm2 += N * N; unstableKm2Steps += un;
                    int nlab = Label(cur, N, 0.05f, curLab);
                    // link to the previous step by the largest pixel overlap
                    var next = new Dictionary<int, CellTrack>();
                    var comp = new Dictionary<int, CellTrack>();
                    var overlap = new Dictionary<long, int>();
                    for (int i = 0; i < N * N; i++) if (curLab[i] > 0 && prevLab[i] > 0) { long key = ((long)curLab[i] << 32) | (uint)prevLab[i]; int c; overlap.TryGetValue(key, out c); overlap[key] = c + 1; }
                    var bestPrev = new Dictionary<int, KeyValuePair<int, int>>();
                    foreach (var kv in overlap)
                    {
                        int cl = (int)(kv.Key >> 32), pl = (int)(kv.Key & 0xFFFFFFFF);
                        KeyValuePair<int, int> b;
                        if (!bestPrev.TryGetValue(cl, out b) || kv.Value > b.Value) bestPrev[cl] = new KeyValuePair<int, int>(pl, kv.Value);
                    }
                    var claimed = new HashSet<int>();
                    for (int cl = 1; cl <= nlab; cl++)
                    {
                        CellTrack tr = null; KeyValuePair<int, int> b;
                        if (bestPrev.TryGetValue(cl, out b) && active.TryGetValue(b.Key, out tr) && !claimed.Contains(b.Key)) claimed.Add(b.Key); else tr = null;
                        if (tr == null) { tr = new CellTrack { First = step, Peak = 0, Area37 = 0, Pixels = null }; tracks.Add(tr); }
                        tr.Last = step; next[cl] = tr; comp[cl] = tr;
                    }
                    // per component: peak, area above 0.37 and the centre pixel at this step
                    var peak = new float[nlab + 1]; var a37 = new int[nlab + 1]; var cx = new double[nlab + 1]; var cy = new double[nlab + 1]; var cn = new int[nlab + 1]; var instN = new int[nlab + 1];
                    for (int i = 0; i < N * N; i++)
                    {
                        int cl = curLab[i]; if (cl <= 0) continue;
                        if (cur[i] > peak[cl]) peak[cl] = cur[i];
                        if (cur[i] >= 0.37f) a37[cl]++;
                        cx[cl] += i % N; cy[cl] += i / N; cn[cl]++;
                        if (inst[i] >= 0.5f) instN[cl]++;
                    }
                    for (int cl = 1; cl <= nlab; cl++)
                    {
                        var tr = comp[cl];
                        if (peak[cl] > tr.Peak) { tr.Peak = peak[cl]; tr.Area37 = a37[cl]; tr.Instab = instN[cl] * 2 >= cn[cl]; }
                    }
                    // cells present at this step in unstable air (centre pixel of the component unstable): counted for the density
                    for (int cl = 1; cl <= nlab; cl++) if (instN[cl] * 2 >= cn[cl]) cellSteps += 1.0;
                    active = next;
                    var t = prevLab; prevLab = curLab; curLab = t;
                }
                foreach (var tr in tracks)
                {
                    if (tr.First == 0 || tr.Last == 36) continue; // touches the window ends (the bucket boundaries): not a complete cell
                    if (tr.Peak < 0.95) { truncated += 1.0; continue; }
                    diam.Add(2.0 * Math.Sqrt(tr.Area37 / Math.PI));
                    life.Add((tr.Last - tr.First + 1) * 600.0 / 3600.0);
                }
            }
        }

        // Connected components (8-neighbourhood) of the pixels at or above a threshold; labels 1..n, 0 elsewhere.
        private static int Label(float[] f, int n, float thr, int[] lab)
        {
            Array.Clear(lab, 0, lab.Length);
            int cnt = 0; var stack = new Stack<int>();
            for (int i0 = 0; i0 < n * n; i0++)
            {
                if (lab[i0] != 0 || f[i0] < thr) continue;
                cnt++; lab[i0] = cnt; stack.Push(i0);
                while (stack.Count > 0)
                {
                    int c = stack.Pop(); int y = c / n, x = c % n;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int yy = y + dy, xx = x + dx; if (yy < 0 || yy >= n || xx < 0 || xx >= n) continue;
                            int cc = yy * n + xx; if (lab[cc] != 0 || f[cc] < thr) continue;
                            lab[cc] = cnt; stack.Push(cc);
                        }
                }
            }
            return cnt;
        }

        // Belt snapshots: cells (strength >= 0.37) of the 2 km raster of the belt 35 to 70 degrees against the accepted front-band cells of D2.
        private static void Snapshots(WeatherGenerator g, BodyParams bp, ref double cellsAll, ref double frontLenKm, ref double near50, ref double centres, ref double within100, ref double uniformN, ref double uniformIn)
        {
            double a = bp.RadiusM, px = 2000.0;
            double dlat = px / a, dlon0 = px / (a * Math.Cos(Rad(52.5)));
            int nr = (int)Math.Ceiling((Rad(70) - Rad(35)) / dlat), nc = (int)Math.Ceiling(2.0 * Math.PI / dlon0);
            var twin = bp.Clone(); twin.WavesEnabled = false; var gOff = new WeatherGenerator(twin, g.Seed);
            var mg = new MetricGrid(Study.EvalLon, Study.EvalLat, a);
            int n2 = Study.EvalLon * Study.EvalLat;
            var p = new double[n2]; var t = new double[n2]; var c = new double[n2]; var tO = new double[n2]; var cO = new double[n2];
            var f = new float[nr * nc]; var lab = new int[nr * nc]; var stack = new Stack<int>();
            var rnd = new Random(5);
            Weather w;
            for (int snap = 0; snap < 24; snap++)
            {
                double ut = Math.Floor((5.0 + snap * 2.5) * 86400.0 / WeatherGenerator.CellHourBucketS) * WeatherGenerator.CellHourBucketS + 5400.0;
                Study.Fields(g, ut, Study.EvalLon, Study.EvalLat, p, t, c, null);
                Study.FieldsTC(gOff, ut, Study.EvalLon, Study.EvalLat, tO, cO);
                var accepted = new List<int>();
                var bands = Detect.BandsFromAnomaly(mg, Detect.Diff(t, tO), Detect.Diff(c, cO), Detect.CloudThreshold(c), 20 * mg.CellM, accepted);
                var fc = new List<double[]>();
                foreach (int cell in accepted) fc.Add(new[] { mg.LatRad[cell / mg.NLon], mg.LonRad[cell % mg.NLon] });
                foreach (var b in bands) frontLenKm += b.ExtentKm;
                for (int h = 0; h < 2; h++)
                {
                    int hs = h == 0 ? 1 : -1;
                    for (int j = 0; j < nr; j++)
                    {
                        double lat = hs * (Rad(35) + (j + 0.5) * dlat);
                        double dlon = 2.0 * Math.PI / nc;
                        for (int i = 0; i < nc; i++) { g.Sample(lat, -Math.PI + (i + 0.5) * dlon, 0.0, ut, out w); f[j * nc + i] = (float)w.CellStrength; }
                    }
                    // components with wrap-around in longitude
                    Array.Clear(lab, 0, lab.Length);
                    int cnt = 0;
                    for (int i0 = 0; i0 < nr * nc; i0++)
                    {
                        if (lab[i0] != 0 || f[i0] < 0.37f) continue;
                        cnt++; lab[i0] = cnt; stack.Push(i0);
                        double sl = 0, so = 0; int m = 0; int x0 = i0 % nc;
                        while (stack.Count > 0)
                        {
                            int cc = stack.Pop(); int y = cc / nc, x = cc % nc;
                            double lat = hs * (Rad(35) + (y + 0.5) * dlat); int dx0 = x - x0; if (dx0 > nc / 2) dx0 -= nc; if (dx0 < -nc / 2) dx0 += nc;
                            sl += lat; so += (x0 + dx0 + 0.5) * (2.0 * Math.PI / nc) - Math.PI; m++;
                            for (int dy = -1; dy <= 1; dy++)
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int yy = y + dy, xx = (x + dx + nc) % nc; if (yy < 0 || yy >= nr) continue;
                                    int q = yy * nc + xx; if (lab[q] != 0 || f[q] < 0.37f) continue;
                                    lab[q] = cnt; stack.Push(q);
                                }
                        }
                        if (m < 3) continue;
                        double clat = sl / m, clon = so / m;
                        cellsAll += 1.0; centres += 1.0;
                        double best = double.MaxValue;
                        foreach (var q in fc) { double d = MetricGrid.Dist(a, clat, clon, q[0], q[1]); if (d < best) best = d; }
                        if (best <= 100000.0) within100 += 1.0;
                        if (best <= 50000.0) near50 += 1.0;
                    }
                }
                // uniform comparison points in the belt
                for (int k = 0; k < 400; k++)
                {
                    double lat = Math.Asin(Math.Sin(Rad(35)) + rnd.NextDouble() * (Math.Sin(Rad(70)) - Math.Sin(Rad(35)))) * (rnd.Next(2) == 0 ? 1 : -1), lon = (rnd.NextDouble() * 2 - 1) * Math.PI;
                    double best = double.MaxValue; foreach (var q in fc) { double d = MetricGrid.Dist(a, lat, lon, q[0], q[1]); if (d < best) best = d; }
                    uniformN += 1.0; if (best <= 100000.0) uniformIn += 1.0;
                }
            }
        }
    }
}
