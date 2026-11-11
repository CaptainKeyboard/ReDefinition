using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AtmoSim.Core;

namespace AtmoSim.Runner
{
    // Read-only diagnostics of tuning round 1, second part: the ground signal, the salinity direction, the block seam.
    public static class Diagnostics2
    {
        private static string F(double v) { return v.ToString("G4", CultureInfo.InvariantCulture); }

        private static double Pc(List<double> v, double p)
        {
            if (v.Count == 0) return double.NaN;
            var a = v.ToArray(); Array.Sort(a);
            return a[(int)(p * (a.Length - 1))];
        }

        // diag-ground [seed]: hourly series over 20 days at several longitudes and latitudes: the hourly change of the 10 m wind, the number and
        // duration of precipitation events (hours with at least 0.05 mm/h, one dry hour allowed inside), the level shares of the rainy hours.
        public static int Ground(string[] args)
        {
            ulong seed = args.Length > 1 ? ulong.Parse(args[1], CultureInfo.InvariantCulture) : 1UL;
            var bp = Study.ByName("kerbin");
            var g = new WeatherGenerator(bp, seed);
            Weather w;
            Console.WriteLine("seed " + seed + " mode " + (Study.ReconDir == null ? "S" : "R") + ", 20 days from day 20, hourly, 8 longitudes per latitude");
            foreach (double latD in new[] { 45.0, 55.0, 70.0, 0.0, -50.0 })
            {
                var dw = new List<double>(); var ev = new List<double>(); var lv = new long[6]; long hours = 0, rainy = 0; var wv = new List<double>();
                for (int k = 0; k < 8; k++)
                {
                    double lon = -Math.PI + (k + 0.5) * 2.0 * Math.PI / 8;
                    double prev = double.NaN; int run = 0, gap = 0;
                    for (int h = 0; h < 480; h++)
                    {
                        double ut = 20.0 * 86400.0 + h * 3600.0;
                        g.Sample(latD * Math.PI / 180.0, lon, 0.0, ut, out w);
                        if (!double.IsNaN(prev)) dw.Add(Math.Abs(w.WindSpeed - prev));
                        prev = w.WindSpeed; wv.Add(w.WindSpeed);
                        hours++;
                        bool wet = w.PrecipMmH >= 0.05;
                        if (wet) { rainy++; lv[(int)g.LevelOf(w.PrecipMmH, w.Type, w.WindSpeed)]++; }
                        if (wet) { run += 1 + gap; gap = 0; }
                        else if (run > 0) { gap++; if (gap > 1) { ev.Add(run); run = 0; gap = 0; } }
                    }
                    if (run > 0) ev.Add(run);
                }
                Console.WriteLine("lat " + latD + ": wind change per hour median " + F(Pc(dw, 0.5)) + " p95 " + F(Pc(dw, 0.95)) + " m/s; wind p50/p95 " + F(Pc(wv, 0.5)) + "/" + F(Pc(wv, 0.95))
                    + "; rainy hours " + F((double)rainy / hours) + ", events " + ev.Count + ", duration median " + F(Pc(ev, 0.5)) + " h, p90 " + F(Pc(ev, 0.9)) + " h; level hours (flurries, light, moderate, heavy, blizzard) "
                    + lv[1] + " " + lv[2] + " " + lv[3] + " " + lv[4] + " " + lv[5]);
            }
            return 0;
        }

        // diag-salinity: the site of the largest ice-thickness difference between 35 psu and 0 psu, step by step over the year (thickness, mixed layer temperature).
        public static int Salinity(string[] args)
        {
            double latD = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 55.0;
            var b35 = Study.ByName("kerbin"); b35.SalinityPsu = 35.0; var b0 = Study.ByName("kerbin"); b0.SalinityPsu = 0.0;
            var g35 = new WeatherGenerator(b35, 1); var g0 = new WeatherGenerator(b0, 1);
            double lat = latD * Math.PI / 180.0;
            double worst = -1e9; double wlon = 0, wph = 0;
            for (double lonD = -180.0 + 7.5; lonD < 180.0; lonD += 15.0)
            {
                double lon = lonD * Math.PI / 180.0;
                if (g35.Grid.OceanAt(lat, lon) < 0.5) continue;
                var s35 = new SurfaceSite(g35, lat, lon); var s0 = new SurfaceSite(g0, lat, lon);
                for (int k = 0; k < 48; k++)
                {
                    double ut = (2.0 + k / 48.0) * b35.YearS;
                    double d = s35.Evaluate(ut).IceThicknessM - s0.Evaluate(ut).IceThicknessM;
                    if (d > worst) { worst = d; wlon = lon; wph = k / 48.0; }
                }
            }
            Console.WriteLine("worst difference " + F(worst) + " m at lon " + F(wlon * 180 / Math.PI) + ", year phase " + F(wph));
            var a = new SurfaceSite(g35, lat, wlon); var c = new SurfaceSite(g0, lat, wlon);
            Console.WriteLine("phase  clim T   35psu: h   tml     0psu: h   tml");
            for (int k = 0; k < 48; k += 1)
            {
                double ut = (2.0 + k / 48.0) * b35.YearS;
                var s1 = a.Evaluate(ut); var s2 = c.Evaluate(ut);
                Console.WriteLine((k / 48.0).ToString("F3", CultureInfo.InvariantCulture) + "  " + F(g35.ClimateTemperature(lat, wlon, ut)).PadLeft(7) + "   " + F(s1.IceThicknessM).PadLeft(7) + " " + F(s1.MixedLayerK).PadLeft(7) + "   " + F(s2.IceThicknessM).PadLeft(7) + " " + F(s2.MixedLayerK).PadLeft(7));
            }
            return 0;
        }

        // diag-snow [lat]: at a latitude, the land texels by terrain-height bucket with the share of snow cover (>= 2.5 mm) in each season and the mean climatological sea-level temperature.
        public static int Snow(string[] args)
        {
            double band = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 55.0;
            var bp = Study.ByName("kerbin"); var g = new WeatherGenerator(bp, 1);
            double lat = band * Math.PI / 180.0;
            double[] edges = { 0, 500, 1000, 1500, 2000, 3000, 4000, 1e9 };
            var n = new int[edges.Length - 1]; var hit = new int[4, edges.Length - 1];
            double[] ph = { 0.0, 0.25, 0.5, 0.75 };
            for (double lonD = -179.375; lonD < 180.0; lonD += 1.25)
            {
                double lon = lonD * Math.PI / 180.0;
                if (g.Grid.OceanAt(lat, lon) >= 0.5) continue;
                double ter = g.Grid.TerrainAt(lat, lon);
                int b = 0; while (ter >= edges[b + 1]) b++;
                n[b]++;
                var s = new SurfaceSite(g, lat, lon);
                for (int p = 0; p < 4; p++)
                    for (int k = -2; k <= 2; k++)
                        if (s.Evaluate((2.0 + ph[p]) * bp.YearS + k * 2.0 * 86400.0).SnowSweMm >= 2.5) hit[p, b]++;
            }
            Console.WriteLine("lat " + band + ": terrain bucket (m): land texels; snow share winter/spring/summer/autumn");
            for (int b = 0; b < n.Length; b++)
                Console.WriteLine("  " + edges[b] + "-" + (edges[b + 1] > 1e8 ? "up" : edges[b + 1].ToString()) + ": " + n[b] + "; " + F(n[b] > 0 ? hit[0, b] / (5.0 * n[b]) : 0) + " " + F(n[b] > 0 ? hit[1, b] / (5.0 * n[b]) : 0)
                        + " " + F(n[b] > 0 ? hit[2, b] / (5.0 * n[b]) : 0) + " " + F(n[b] > 0 ? hit[3, b] / (5.0 * n[b]) : 0));
            return 0;
        }

        // diag-wet: the dry pairs of the wet-state check (no rain in the previous 5 days at the 6-hour instants) with a wet value above 0.05: where, when, how wet and the largest rate in the layer's own window.
        public static int Wet(string[] args)
        {
            var bp = Study.ByName("kerbin"); var g = new WeatherGenerator(bp, 1);
            var rnd = new Random(21); Weather w;
            int dry = 0, bad = 0;
            for (int n = 0; n < 150; n++)
            {
                double la = Math.Asin(2 * rnd.NextDouble() - 1), lo = (rnd.NextDouble() * 2 - 1) * Math.PI;
                var s = new SurfaceSite(g, la, lo);
                double terr = g.Grid.TerrainAt(la, lo);
                for (int k = 0; k < 40; k++)
                {
                    double ut = (2.0 + k * 0.027) * bp.YearS;
                    bool anyRain = false;
                    for (int q = 0; q <= 20 && !anyRain; q++) { g.Sample(la, lo, terr, ut - q * 21600.0, out w); if (w.PrecipMmH > 0.0) anyRain = true; }
                    double wet = s.Evaluate(ut).Wet01;
                    if (anyRain) continue;
                    dry++;
                    if (wet > 0.05)
                    {
                        bad++;
                        double mx = 0; long step = (long)Math.Floor(ut / 21600.0);
                        for (int q = 0; q < 24; q++) { g.Sample(la, lo, terr, (step - q) * 21600.0, out w); mx = Math.Max(mx, w.PrecipMmH); }
                        Console.WriteLine("lat " + F(la * 180 / Math.PI) + " lon " + F(lo * 180 / Math.PI) + " T " + F(g.ClimateTemperature(la, lo, ut)) + " wet " + F(wet) + " max rate in the previous 6 days on the step grid " + F(mx));
                    }
                }
            }
            Console.WriteLine("dry pairs " + dry + ", wet > 0.05 in " + bad);
            return 0;
        }

        // diag-cellsites: where the cells (strength >= 0.37) of the belt 35 to 70 degrees sit: counts by latitude band and by the analytic front strength at the cell, and by the stratiform uplift.
        public static int CellSites(string[] args)
        {
            var bp = Study.ByName("kerbin"); var g = new WeatherGenerator(bp, 1);
            Weather w;
            double fref = g.D.FrontRefKPer100Km;
            var latB = new int[4]; var frB = new int[4]; var upB = new int[4]; int n = 0;
            for (int s = 0; s < 8; s++)
            {
                double ut = 3.0e6 + s * 1.1e5 + 5400.0;
                for (double latD = 35.0; latD < 70.0; latD += 0.04)
                {
                    double lat = latD * Math.PI / 180.0;
                    double dlon = 0.05 / Math.Cos(lat);
                    for (double lon = 0.0; lon < 2.0 * Math.PI; lon += dlon * Math.PI / 180.0)
                    {
                        g.Sample(lat, lon, 0.0, ut, out w);
                        if (w.CellStrength < 0.37) continue;
                        n++;
                        latB[latD < 40 ? 0 : latD < 50 ? 1 : latD < 60 ? 2 : 3]++;
                        double fn = Math.Min(1.0, w.FrontKPer100Km / fref);
                        frB[fn < 0.2 ? 0 : fn < 0.6 ? 1 : fn < 0.9 ? 2 : 3]++;
                        upB[w.Uplift < 0.1 ? 0 : w.Uplift < 0.4 ? 1 : w.Uplift < 0.8 ? 2 : 3]++;
                    }
                }
            }
            Console.WriteLine("cell texels (strength >= 0.37, 0.05 deg raster): " + n);
            Console.WriteLine("by latitude 35-40/40-50/50-60/60-70: " + string.Join(" ", Array.ConvertAll(latB, v => F((double)v / n))));
            Console.WriteLine("by front strength <0.2 / 0.2-0.6 / 0.6-0.9 / >0.9: " + string.Join(" ", Array.ConvertAll(frB, v => F((double)v / n))));
            Console.WriteLine("by uplift <0.1 / 0.1-0.4 / 0.4-0.8 / >0.8: " + string.Join(" ", Array.ConvertAll(upB, v => F((double)v / n))));
            return 0;
        }

        // diag-cellfar: for 6 snapshots of criterion 11: cells (strength >= 0.37 on a 2 km raster, component >= 3 texels) farther than 100 km from every accepted front-band cell,
        // with the analytic front strength and the nearest accepted band cell, and the number of accepted cells and bands per snapshot.
        public static int CellFar(string[] args)
        {
            var bp = Study.ByName("kerbin"); var g = new WeatherGenerator(bp, 1);
            double a = bp.RadiusM;
            var twin = bp.Clone(); twin.WavesEnabled = false; var gOff = new WeatherGenerator(twin, g.Seed);
            var mg = new MetricGrid(Study.EvalLon, Study.EvalLat, a);
            int n2 = Study.EvalLon * Study.EvalLat;
            var p = new double[n2]; var t = new double[n2]; var c = new double[n2]; var tO = new double[n2]; var cO = new double[n2];
            Weather w; double fref = g.D.FrontRefKPer100Km;
            int farN = 0, nearN = 0;
            for (int snap = 0; snap < 6; snap++)
            {
                double ut = Math.Floor((5.0 + snap * 2.5) * 86400.0 / WeatherGenerator.CellHourBucketS) * WeatherGenerator.CellHourBucketS + 5400.0;
                Study.Fields(g, ut, Study.EvalLon, Study.EvalLat, p, t, c, null);
                Study.FieldsTC(gOff, ut, Study.EvalLon, Study.EvalLat, tO, cO);
                var accepted = new List<int>();
                var bands = Detect.BandsFromAnomaly(mg, Detect.Diff(t, tO), Detect.Diff(c, cO), Detect.CloudThreshold(c), 20 * mg.CellM, accepted);
                Console.WriteLine("snapshot " + snap + ": bands " + bands.Count + ", accepted cells " + accepted.Count);
                // scan the belt of the north on a 3 km raster for cell texels, take one per 60 km of coarse bucket to keep the print short
                var seen = new HashSet<long>();
                for (double latD = 35.0; latD < 70.0; latD += 0.03)
                {
                    double lat = latD * Math.PI / 180.0;
                    double step = 0.03 * Math.PI / 180.0 / Math.Cos(lat);
                    for (double lon = 0.0; lon < 2.0 * Math.PI; lon += step)
                    {
                        g.Sample(lat, lon, 0.0, ut, out w);
                        if (w.CellStrength < 0.37) continue;
                        long key = ((long)(latD / 0.4) << 20) + (long)(lon / (0.4 * Math.PI / 180.0 / Math.Cos(lat)));
                        if (!seen.Add(key)) continue;
                        double best = double.MaxValue;
                        foreach (int cell in accepted) { double d = MetricGrid.Dist(a, lat, lon, mg.LatRad[cell / mg.NLon], mg.LonRad[cell % mg.NLon]); if (d < best) best = d; }
                        if (best <= 100000.0) { nearN++; continue; }
                        farN++;
                        if (farN <= 25) Console.WriteLine("  far cell at lat " + F(latD) + " lon " + F(lon * 180 / Math.PI) + ": nearest accepted band cell " + F(best / 1000.0) + " km, front " + F(w.FrontKPer100Km / fref) + ", uplift " + F(w.Uplift));
                    }
                }
            }
            Console.WriteLine("coarse cell buckets near an accepted band " + nearN + ", far " + farN);
            return 0;
        }

        // diag-jump <seed> <day> <latDeg> <lonDeg0> <lonDeg1> [steps]: along a parallel the fields of the generator in small steps; prints the steps where a field changes by more than
        // 5 times its median step change (a seam). Finds the field and the longitude of a discontinuity.
        public static int Jump(string[] args)
        {
            ulong seed = ulong.Parse(args[1], CultureInfo.InvariantCulture);
            double day = double.Parse(args[2], CultureInfo.InvariantCulture), latD = double.Parse(args[3], CultureInfo.InvariantCulture);
            double l0 = double.Parse(args[4], CultureInfo.InvariantCulture), l1 = double.Parse(args[5], CultureInfo.InvariantCulture);
            int n = args.Length > 6 ? int.Parse(args[6], CultureInfo.InvariantCulture) : 2000;
            var bp = Study.ByName("kerbin"); var g = new WeatherGenerator(bp, seed);
            string[] names = { "TempK", "PressureAnomPa", "Humidity", "Uplift", "Front", "CloudWater", "Coverage", "PrecipMmH", "WindSpeed", "Instability" };
            var prev = new double[names.Length]; var steps = new List<double>[names.Length];
            for (int k = 0; k < names.Length; k++) steps[k] = new List<double>();
            var vals = new List<double[]>(); var lons = new List<double>();
            Weather w;
            for (int i = 0; i <= n; i++)
            {
                double lon = (l0 + (l1 - l0) * i / n) * Math.PI / 180.0;
                g.Sample(latD * Math.PI / 180.0, lon, 0.0, day * 86400.0, out w);
                vals.Add(new[] { w.TempK, w.PressureAnomPa, w.Humidity, w.Uplift, w.FrontKPer100Km, w.CloudWater, w.Coverage, w.PrecipMmH, w.WindSpeed, w.Instability });
                lons.Add(l0 + (l1 - l0) * i / n);
            }
            for (int k = 0; k < names.Length; k++)
                for (int i = 1; i < vals.Count; i++) steps[k].Add(Math.Abs(vals[i][k] - vals[i - 1][k]));
            for (int k = 0; k < names.Length; k++)
            {
                double med = Pc(steps[k], 0.5) + 1e-12;
                for (int i = 0; i < steps[k].Count; i++)
                    if (steps[k][i] > 8.0 * med && steps[k][i] > 1e-6) Console.WriteLine(names[k] + ": jump of " + F(steps[k][i]) + " (median step " + F(med) + ") at lon " + F(lons[i]) + " -> " + F(lons[i + 1]));
            }
            return 0;
        }

        // diag-slots <seed> <day>: the active systems of both hemispheres (latitude, longitude, amplitude) at a time.
        public static int Slots(string[] args)
        {
            ulong seed = ulong.Parse(args[1], CultureInfo.InvariantCulture); double day = double.Parse(args[2], CultureInfo.InvariantCulture);
            var g = new WeatherGenerator(Study.ByName("kerbin"), seed);
            var buf = new double[3 * 16];
            for (int h = 0; h < 2; h++)
            {
                int n = g.ActiveSystems(h, day * 86400.0, buf);
                for (int i = 0; i < n; i++) Console.WriteLine("hemisphere " + h + ": lat " + F(buf[3 * i] * 180 / Math.PI) + " lon " + F(buf[3 * i + 1] * 180 / Math.PI) + " amp " + F(buf[3 * i + 2]));
            }
            Console.WriteLine("system radius " + F(g.D.SystemRadiusM / 1000.0) + " km, slots " + g.D.SlotsPerHemisphere);
            if (args.Length > 4)
            {
                // per-slot pressure on both sides of a longitude: diag-slots <seed> <day> <hemi> <latDeg> <lonDeg>
                int h = int.Parse(args[3], CultureInfo.InvariantCulture); double latD = double.Parse(args[4], CultureInfo.InvariantCulture), lonD = double.Parse(args[5], CultureInfo.InvariantCulture);
                foreach (double dl in new[] { -0.05, 0.05 })
                    for (int j = 0; j < WeatherGenerator.MaxSlots; j++)
                    {
                        bool inside; double x, y, lx, ly;
                        double p = g.DebugSlotPressure(h, j, latD * Math.PI / 180.0, (lonD + dl) * Math.PI / 180.0, day * 86400.0, out inside, out x, out y, out lx, out ly);
                        if (lx > 0) Console.WriteLine("lon " + F(lonD + dl) + " slot " + j + ": p " + F(p) + " inside " + inside + " x " + F(x) + " y " + F(y) + " Lx " + F(lx) + " Ly " + F(ly));
                    }
            }
            return 0;
        }

        // diag-contin: the continuity check of criterion 10 (f) with the places of the jumps: site, quarter boundary, time before/after, the thickness on both sides.
        public static int Contin(string[] args)
        {
            var bp = Study.ByName("kerbin"); var g = new WeatherGenerator(bp, 1);
            double year = bp.YearS, quarter = year / 4.0;
            foreach (double lat in new[] { 55.0, 60.0, 65.0, 70.0, 75.0 })
            {
                SurfaceSite s = null; double lonUsed = 0;
                for (double lon = -175.0; lon < 180.0 && s == null; lon += 10.0)
                    if (g.Grid.OceanAt(lat * Math.PI / 180.0, lon * Math.PI / 180.0) >= 0.5) { s = new SurfaceSite(g, lat * Math.PI / 180.0, lon * Math.PI / 180.0); lonUsed = lon; }
                if (s == null) continue;
                for (int n = 4; n < 16; n++)
                {
                    double b = n * quarter;
                    double prev = s.Evaluate(b - 86400.0).IceThicknessM;
                    for (double t = b - 86400.0 + 600.0; t <= b + 86400.0; t += 600.0)
                    {
                        double cur = s.Evaluate(t).IceThicknessM;
                        if (Math.Abs(cur - prev) > 0.03)
                        {
                            Console.WriteLine("lat " + lat + " lon " + lonUsed + " boundary " + n + " at " + F((t - b) / 3600.0) + " h: " + F(prev) + " -> " + F(cur));
                            foreach (double dt in new[] { -1200.0, -600.0, 0.0, 600.0, 1200.0 })
                            {
                                double hA, hB, wA; s.DebugIce(t + dt, out hA, out hB, out wA);
                                Console.WriteLine("   t" + (dt >= 0 ? "+" : "") + dt + ": hA " + F(hA) + " hB " + F(hB) + " wA " + F(wA) + " h " + F(s.Evaluate(t + dt).IceThicknessM));
                            }
                        }
                        prev = cur;
                    }
                }
            }
            return 0;
        }

        // diag-seam [seed]: the cover map of 512 x 256 texels at two times 30 minutes apart; a lattice edge shows as a row or column where the mean absolute difference between
        // neighbouring texels of the DIFFERENCE image is far above the rest. Prints the strongest rows and columns of the neighbour difference of the difference image.
        public static int Seam(string[] args)
        {
            ulong seed = args.Length > 1 ? ulong.Parse(args[1], CultureInfo.InvariantCulture) : 1UL;
            double day = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 4.0;
            var bp = Study.ByName("kerbin");
            var g = new WeatherGenerator(bp, seed);
            int nx = 512, ny = 256; Weather w;
            var d = new double[nx * ny];
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    double lat = (90.0 - (j + 0.5) * 180.0 / ny) * Math.PI / 180.0, lon = -Math.PI + (i + 0.5) * 2.0 * Math.PI / nx;
                    g.Sample(lat, lon, 0.0, day * 86400.0, out w); double c0 = w.Coverage;
                    g.Sample(lat, lon, 0.0, day * 86400.0 + 1800.0, out w);
                    d[j * nx + i] = w.Coverage - c0;
                }
            var cols = new double[nx]; var rows = new double[ny];
            for (int j = 1; j < ny - 1; j++)
                for (int i = 1; i < nx - 1; i++)
                {
                    cols[i] += Math.Abs(d[j * nx + i] - d[j * nx + i - 1]);
                    rows[j] += Math.Abs(d[j * nx + i] - d[(j - 1) * nx + i]);
                }
            var cl = new List<double>(cols); var rl = new List<double>(rows);
            double cm = Pc(cl, 0.5), rm = Pc(rl, 0.5);
            var sb = new StringBuilder("seed " + seed + " day " + day + ": median column edge sum " + F(cm) + ", median row edge sum " + F(rm) + "; columns above 2.5 x median: ");
            for (int i = 1; i < nx - 1; i++) if (cols[i] > 2.5 * cm) sb.Append(i + " (" + F(cols[i] / cm) + ") ");
            sb.Append("; rows above 2.5 x median: ");
            for (int j = 1; j < ny - 1; j++) if (rows[j] > 2.5 * rm) sb.Append(j + " (" + F(rows[j] / rm) + ") ");
            Console.WriteLine(sb.ToString());
            return 0;
        }
    }
}
