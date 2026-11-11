using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AtmoSim.Core;

namespace AtmoSim.Runner
{
    // Tuning round 3: where do the front bands of the band detector get lost, per hemisphere?
    public static class Round3Diag
    {
        private static double D(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }

        // diag-systems <body> <seed> <startDays> <stepHours> <count>: the active systems of both hemispheres per time step (centre, amplitude, front angle, spiral radius).
        public static int Systems(string[] args)
        {
            var bp = Study.ByName(args[1]);
            var g = new WeatherGenerator(bp, ulong.Parse(args[2], CultureInfo.InvariantCulture));
            double start = D(args[3]) * 86400.0, step = D(args[4]) * 3600.0; int count = int.Parse(args[5], CultureInfo.InvariantCulture);
            for (int f = 0; f < count; f++)
            {
                double ut = start + f * step;
                for (int h = 0; h < 2; h++)
                    for (int s = 0; s < WeatherGenerator.MaxSlots; s++)
                    {
                        double lat, lon, amp, mat, rsp, ang;
                        if (!g.SystemInfo(h, s, ut, out lat, out lon, out amp, out mat, out rsp, out ang)) continue;
                        Console.WriteLine("day " + (ut / 86400.0).ToString("F2") + " " + (h == 0 ? "N" : "S") + " slot " + s + " lat " + (lat * 180.0 / Math.PI).ToString("F1") + " lon " + (lon * 180.0 / Math.PI).ToString("F1")
                            + " amp " + amp.ToString("F0") + " Pa angle " + ang.ToString("F0") + " deg mature " + mat + " rsp " + rsp.ToString("F0") + " km");
                    }
            }
            return 0;
        }

        // diag-detect <body> <seed> <days> <frameHours>: per hemisphere, for every active system slot, whether a D1 minimum lies within 250 km of its centre in each frame; prints per slot generation
        // the active frames, the detected frames, the number of detection runs and the longest run (hours), and the totals.
        public static int DetectFlicker(string[] args)
        {
            var bp = Study.ByName(args[1]);
            ulong seed = ulong.Parse(args[2], CultureInfo.InvariantCulture);
            double days = D(args[3]), frameS = D(args[4]) * 3600.0;
            var g = new WeatherGenerator(bp, seed);
            int nx = Study.EvalLon, ny = Study.EvalLat, n = nx * ny;
            var mg = new MetricGrid(nx, ny, bp.RadiusM);
            var p = new double[n]; var t = new double[n]; var c = new double[n];
            double ringKm = Math.Max(3.0 * 2.0 * Math.PI * bp.RadiusM / nx / 1000.0, 0.5 * g.D.SystemRadiusM / 1000.0);
            int frames = (int)(days * 86400.0 / frameS);
            var act = new double[3 * 32];
            var runs = new Dictionary<string, List<bool>>();
            for (int f = 0; f < frames; f++)
            {
                double ut = f * frameS;
                Study.Fields(g, ut, nx, ny, p, t, c, null);
                var mins = Detect.Minima(mg, p, bp.SurfacePressurePa, ringKm, true);
                for (int h = 0; h < 2; h++)
                    for (int s = 0; s < WeatherGenerator.MaxSlots; s++)
                    {
                        double lat, lon, amp, mat, rsp, ang;
                        if (!g.SystemInfo(h, s, ut, out lat, out lon, out amp, out mat, out rsp, out ang)) continue;
                        double life, off; g.SlotTiming(h, s, out life, out off);
                        long gen = (long)Math.Floor((ut - off) / life);
                        string key = h + ":" + s + ":" + gen;
                        bool hit = false;
                        foreach (var m in mins) if (MetricGrid.Dist(bp.RadiusM, lat, lon, m.Lat, m.Lon) < 250000.0) { hit = true; break; }
                        List<bool> l; if (!runs.TryGetValue(key, out l)) { l = new List<bool>(); runs[key] = l; }
                        l.Add(hit);
                    }
            }
            int totalA = 0, totalD = 0, gens = 0, fullGens = 0; var lens = new List<double>();
            foreach (var kv in runs)
            {
                var l = kv.Value; int a = l.Count, d = 0, nr = 0, best = 0, cur = 0;
                for (int i = 0; i < l.Count; i++) { if (l[i]) { d++; cur++; if (cur > best) best = cur; if (i == 0 || !l[i - 1]) nr++; } else cur = 0; }
                if (a * frameS < 2.0 * 86400.0) continue; // generations cut by the run boundary
                gens++; totalA += a; totalD += d; lens.Add(best * frameS / 3600.0);
                if (gens <= 40) Console.WriteLine(kv.Key + " active " + (a * frameS / 3600.0).ToString("F0") + " h detected " + (d * frameS / 3600.0).ToString("F0") + " h runs " + nr + " longest " + (best * frameS / 3600.0).ToString("F0") + " h");
            }
            lens.Sort();
            Console.WriteLine("generations " + gens + " active frames " + totalA + " detected " + totalD + " (" + (100.0 * totalD / Math.Max(1, totalA)).ToString("F0") + " %) median longest run " + (lens.Count > 0 ? lens[lens.Count / 2] : 0) + " h");
            return 0;
        }

        // patch <body> <seed> <utDays> <latC> <lonC> <halfLatDeg> <halfLonDeg> <nx> <ny> <dir>: map fields of a window (same files as `frames`, one frame), for the close-ups.
        public static int Patch(string[] args)
        {
            var bp = Study.ByName(args[1]);
            var g = new WeatherGenerator(bp, ulong.Parse(args[2], CultureInfo.InvariantCulture));
            double ut = D(args[3]) * 86400.0, latC = D(args[4]), lonC = D(args[5]), hLat = D(args[6]), hLon = D(args[7]);
            int nx = int.Parse(args[8], CultureInfo.InvariantCulture), ny = int.Parse(args[9], CultureInfo.InvariantCulture);
            string dir = args[10];
            Directory.CreateDirectory(dir);
            string[] names = { "cover", "precip", "level", "front", "pa", "type", "wind", "temp" };
            var res = new float[names.Length][];
            for (int k = 0; k < names.Length; k++) res[k] = new float[nx * ny];
            var oc = new float[nx * ny];
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    double lat = (latC + hLat - (j + 0.5) * 2.0 * hLat / ny) * Math.PI / 180.0, lon = DMath.WrapPi((lonC - hLon + (i + 0.5) * 2.0 * hLon / nx) * Math.PI / 180.0);
                    Weather w; g.Sample(lat, lon, 0.0, ut, out w);
                    int k = j * nx + i;
                    res[0][k] = (float)w.Coverage; res[1][k] = (float)w.PrecipMmH; res[2][k] = (float)g.Level01(w.PrecipMmH); res[3][k] = (float)w.FrontKPer100Km;
                    res[4][k] = (float)w.PressureAnomPa; res[5][k] = (float)(int)w.Type; res[6][k] = (float)w.WindSpeed; res[7][k] = (float)w.TempK;
                    oc[k] = (float)g.Grid.OceanAt(lat, lon);
                }
            for (int q = 0; q < names.Length; q++) using (var w = new BinaryWriter(File.Create(Path.Combine(dir, "frames_" + names[q] + ".f32")))) foreach (float v in res[q]) w.Write(v);
            using (var w = new BinaryWriter(File.Create(Path.Combine(dir, "frames_ocean.f32")))) foreach (float v in oc) w.Write(v);
            File.WriteAllText(Path.Combine(dir, "frames_meta.json"), Json.Write(new Dictionary<string, object> {
                { "body", bp.Name }, { "nlon", nx }, { "nlat", ny }, { "frames", 1 }, { "frame_s", 0.0 }, { "fields", names }, { "times_s", new List<double> { ut } }, { "radius_km", bp.RadiusM / 1000.0 },
                { "window", new List<double> { latC - hLat, latC + hLat, lonC - hLon, lonC + hLon } } }));
            return 0;
        }

        // diag-bands <body> <seed> <days> <frameHours> <outDir>: per hemisphere over the frames of days 3 to days-3: flagged cells, components by outcome
        // (accepted, fewer than 6 cells, too short, too wide, aspect below 3), the 95th percentile of the wave-borne temperature gradient (K per 100 km) in the belt,
        // and for the last frames an overlay file (0 none, 1 flagged and rejected, 2 accepted band cells) next to the cloud cover.
        public static int Bands(string[] args)
        {
            var bp = Study.ByName(args[1]);
            ulong seed = ulong.Parse(args[2], CultureInfo.InvariantCulture);
            double days = D(args[3]), frameS = D(args[4]) * 3600.0;
            string dir = args[5];
            Directory.CreateDirectory(dir);
            var g = new WeatherGenerator(bp, seed);
            var twin = bp.Clone(); twin.WavesEnabled = false;
            var gOff = new WeatherGenerator(twin, seed);
            int nx = Study.EvalLon, ny = Study.EvalLat, n = nx * ny;
            var mg = new MetricGrid(nx, ny, bp.RadiusM);
            var p = new double[n]; var t = new double[n]; var c = new double[n]; var tOff = new double[n]; var cOff = new double[n];
            int frames = (int)(days * 86400.0 / frameS), edge = (int)Math.Round(3.0 * 86400.0 / frameS);
            var outcome = new long[2, 5]; var cellsByOutcome = new long[2, 5]; var flaggedCells = new long[2];
            var gradSamples = new List<double>[] { new List<double>(), new List<double>() };
            var overlay = new List<float[]>(); var cover = new List<float[]>(); var overlayFrames = new List<int>();
            int curFrame = 0; float[] cur = null;
            Detect.ComponentSink = (hemi, reason, cells) =>
            {
                if (curFrame < edge || curFrame >= frames - edge) return;
                outcome[hemi, reason]++; cellsByOutcome[hemi, reason] += cells.Count; flaggedCells[hemi] += cells.Count;
                if (cur != null) foreach (int cc in cells) cur[cc] = reason == 0 ? 2f : Math.Max(cur[cc], 1f);
            };
            int keepEvery = Math.Max(1, (frames - 2 * edge) / 24);
            for (int f = 0; f < frames; f++)
            {
                curFrame = f;
                double ut = f * frameS;
                Study.Fields(g, ut, nx, ny, p, t, c, null);
                Study.FieldsTC(gOff, ut, nx, ny, tOff, cOff);
                double[] ta = Detect.Diff(t, tOff), ca = Detect.Diff(c, cOff);
                bool keep = f >= edge && f < frames - edge && (f - edge) % keepEvery == 0;
                cur = keep ? new float[n] : null;
                Detect.BandsFromAnomaly(mg, ta, ca, Detect.CloudThreshold(c), 20 * mg.CellM);
                if (f >= edge && f < frames - edge)
                {
                    for (int j = 1; j < ny - 1; j++)
                    {
                        if (Math.Abs(mg.LatDeg[j]) < Detect.BeltLo || Math.Abs(mg.LatDeg[j]) > Detect.BeltHi) continue;
                        int h = mg.LatDeg[j] >= 0 ? 0 : 1;
                        for (int i = 0; i < nx; i += 2)
                        {
                            int ie = (i + 1) % nx, iw = (i - 1 + nx) % nx;
                            double dx = 2.0 * mg.Dx[j], dy = mg.A * (mg.LatRad[j - 1] - mg.LatRad[j + 1]);
                            double tx = (ta[j * nx + ie] - ta[j * nx + iw]) / dx, ty = (ta[(j - 1) * nx + i] - ta[(j + 1) * nx + i]) / dy;
                            gradSamples[h].Add(Math.Sqrt(tx * tx + ty * ty) * 1e5);
                        }
                    }
                }
                if (keep)
                {
                    overlay.Add(cur); var cv = new float[n];
                    for (int k = 0; k < n; k++) cv[k] = (float)(1.0 - Math.Exp(-3.0 * c[k]));
                    cover.Add(cv); overlayFrames.Add(f);
                }
            }
            Detect.ComponentSink = null;
            string[] nm = { "accepted", "under6cells", "tooShort", "tooWide", "aspectBelow3" };
            int used = frames - 2 * edge;
            var res = new Dictionary<string, object>();
            for (int h = 0; h < 2; h++)
            {
                string hn = h == 0 ? "north" : "south";
                var d = new Dictionary<string, object>();
                for (int r = 0; r < 5; r++) { d[nm[r] + "_per_frame"] = (double)outcome[h, r] / used; d[nm[r] + "_cells_per_frame"] = (double)cellsByOutcome[h, r] / used; }
                gradSamples[h].Sort();
                var gs = gradSamples[h];
                d["grad_p50"] = gs.Count > 0 ? gs[gs.Count / 2] : 0; d["grad_p95"] = gs.Count > 0 ? gs[(int)(0.95 * (gs.Count - 1))] : 0; d["grad_p99"] = gs.Count > 0 ? gs[(int)(0.99 * (gs.Count - 1))] : 0;
                res[hn] = d;
                Console.WriteLine(hn + ": accepted " + d[nm[0] + "_per_frame"] + " under6 " + d[nm[1] + "_per_frame"] + " short " + d[nm[2] + "_per_frame"] + " wide " + d[nm[3] + "_per_frame"]
                    + " aspect " + d[nm[4] + "_per_frame"] + " | gradient p50/p95/p99 " + d["grad_p50"] + " / " + d["grad_p95"] + " / " + d["grad_p99"]);
            }
            File.WriteAllText(Path.Combine(dir, "bands_diag.json"), Json.Write(res));
            using (var w = new BinaryWriter(File.Create(Path.Combine(dir, "bandmask.f32")))) foreach (var o in overlay) foreach (float v in o) w.Write(v);
            using (var w = new BinaryWriter(File.Create(Path.Combine(dir, "cover.f32")))) foreach (var o in cover) foreach (float v in o) w.Write(v);
            File.WriteAllText(Path.Combine(dir, "bandmask_meta.json"), Json.Write(new Dictionary<string, object> { { "nlon", nx }, { "nlat", ny }, { "frames", overlay.Count }, { "frame_index", overlayFrames }, { "frame_s", frameS } }));
            return 0;
        }
    }
}
