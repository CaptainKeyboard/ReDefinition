using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AtmoSim.Core;

namespace AtmoSim.Runner
{
    // Criteria 6 (cost), 7 (determinism) and 8 (UT rewind and jump). These runs do not look at any climate output.
    public static class Timing
    {
        public static string F(double v) { return v.ToString("R", System.Globalization.CultureInfo.InvariantCulture); }

        public struct Q { public double Lat, Lon, Alt, Ut; }

        // A fixed set of query points from a seeded stream: random latitude (area weighted), longitude, altitude, time in [0, 3 years of Kerbin].
        public static Q[] Queries(int n, ulong seed, double utMax)
        {
            var r = new Rng(seed);
            var q = new Q[n];
            for (int i = 0; i < n; i++)
            {
                q[i].Lat = Math.Asin(2.0 * r.Uniform() - 1.0);
                q[i].Lon = (2.0 * r.Uniform() - 1.0) * Math.PI;
                q[i].Alt = r.Uniform() * 12000.0;
                q[i].Ut = r.Uniform() * utMax;
            }
            return q;
        }

        // ---- criterion 6 ---------------------------------------------------------------------------------------------------

        public static int Cost(string[] args)
        {
            string outFile = args.Length > 1 ? args[1] : null;
            var r = new Dictionary<string, object>();
            object tiered = AppContext.GetData("System.Runtime.TieredCompilation");
            r["jit_tiered_compilation_setting"] = tiered == null ? "default (on)" : tiered.ToString();
            var g = new WeatherGenerator(BodyParams.KerbinLike(), 1);
            Weather w;
            // warm-up (JIT tier-up, thread-local frame, grid): 1.5 s of the benchmark pattern, so a run with tiered compilation measures steady-state code too
            {
                var wq = Queries(4096, 9, 3.0e7); var wu = Stopwatch.StartNew(); long wk = 0;
                while (wu.ElapsedMilliseconds < 1500) { var c = wq[wk % wq.Length]; g.Sample(c.Lat, c.Lon, c.Alt, 1.0e6 + (wk / 256) * 3600.0, out w); wk++; }
            }

            // (a) map-like pattern: 256 consecutive points per UT, UT changes each batch
            int n = 40000;
            var tick = new long[n];
            double toUs = 1e6 / Stopwatch.Frequency;
            var q = Queries(n, 5, 3.0e7);
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < n; i++)
            {
                double ut = 1.0e6 + (i / 256) * 3600.0;
                long t0 = Stopwatch.GetTimestamp();
                g.Sample(q[i].Lat, q[i].Lon, q[i].Alt, ut, out w);
                tick[i] = Stopwatch.GetTimestamp() - t0;
            }
            long alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
            Array.Sort(tick);
            r["sample_median_us_map_pattern"] = tick[n / 2] * toUs;
            r["sample_p95_us_map_pattern"] = tick[(int)(0.95 * n)] * toUs;
            r["sample_allocated_bytes_40000_calls"] = alloc;
            // (b) every call at a new UT (worst case, includes the system set-up of the frame)
            for (int i = 0; i < n; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                g.Sample(q[i].Lat, q[i].Lon, q[i].Alt, q[i].Ut, out w);
                tick[i] = Stopwatch.GetTimestamp() - t0;
            }
            Array.Sort(tick);
            r["sample_median_us_new_ut_each_call"] = tick[n / 2] * toUs;
            r["sample_p95_us_new_ut_each_call"] = tick[(int)(0.95 * n)] * toUs;
            // timer overhead
            for (int i = 0; i < n; i++) { long t0 = Stopwatch.GetTimestamp(); tick[i] = Stopwatch.GetTimestamp() - t0; }
            Array.Sort(tick);
            r["timer_overhead_median_us"] = tick[n / 2] * toUs;

            // (c) 512x256 map update, all fields
            var cov = new float[512 * 256]; var pr = new float[512 * 256]; var lv = new float[512 * 256]; var tk = new float[512 * 256]; var wd = new float[512 * 256]; var ce = new float[512 * 256];
            var ms = new double[7];
            for (int i = 0; i < ms.Length; i++)
            {
                var sw = Stopwatch.StartNew();
                SubGrid.Map(g, 2.0e6 + i * 21600.0, 512, 256, cov, pr, lv, tk, wd, ce);
                ms[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(ms); r["map_512x256_median_ms"] = ms[3]; r["map_512x256_min_ms"] = ms[0];

            // (d) 32x32x24 window
            var dens = new float[32 * 32 * 24];
            var wm = new double[15];
            for (int i = 0; i < wm.Length; i++)
            {
                var sw = Stopwatch.StartNew();
                SubGrid.Window(g, 0.8, 0.3, 2.0e6 + i * 600.0, 32, 32, 24, 500.0, 250.0, 0.0, dens);
                wm[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(wm); r["window_32x32x24_median_ms"] = wm[7];

            // (e) 24 hourly forecast steps of one site
            var fm = new double[300];
            for (int i = 0; i < fm.Length; i++)
            {
                var sw = Stopwatch.StartNew();
                for (int h = 0; h < 24; h++) g.Sample(0.9, 0.4, 0.0, 3.0e6 + i * 5000.0 + h * 3600.0, out w);
                fm[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(fm); r["forecast_24_steps_median_ms"] = fm[150];

            // (f) surface layer: one site, warm cache, consecutive 6 h steps
            var site = new SurfaceSite(g, 1.1, 0.3);
            var sm = new double[200];
            double ut0 = 5.0e6;
            site.Evaluate(ut0);
            for (int i = 0; i < sm.Length; i++)
            {
                var sw = Stopwatch.StartNew();
                site.Evaluate(ut0 + (i + 1) * 21600.0);
                sm[i] = sw.Elapsed.TotalMilliseconds * 1000.0;
            }
            Array.Sort(sm); r["surface_site_median_us_with_new_step"] = sm[100];
            // sea site (ice integration included)
            var seaLat = FindSea(g, 1.0);
            var sea = new SurfaceSite(g, seaLat[0], seaLat[1]);
            sea.Evaluate(ut0);
            for (int i = 0; i < sm.Length; i++)
            {
                var sw = Stopwatch.StartNew();
                sea.Evaluate(ut0 + (i + 1) * 21600.0);
                sm[i] = sw.Elapsed.TotalMilliseconds * 1000.0;
            }
            Array.Sort(sm); r["surface_sea_site_median_us_with_new_step"] = sm[100];
            // repeat evaluation inside the same step (all caches warm): the cost without any window sampling
            for (int i = 0; i < sm.Length; i++)
            {
                var sw = Stopwatch.StartNew();
                sea.Evaluate(ut0 + 200 * 21600.0 + (i % 20) * 900.0);
                sm[i] = sw.Elapsed.TotalMilliseconds * 1000.0;
            }
            Array.Sort(sm); r["surface_sea_site_median_us_warm_step"] = sm[100];

            // (g) 256x128 cover map
            if (args.Length > 2 && args[2] == "covermap")
            {
                int nx = 256, ny = 128;
                var sites = new SurfaceSite[nx * ny];
                var swb = Stopwatch.StartNew();
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                        sites[j * nx + i] = new SurfaceSite(g, (90.0 - (j + 0.5) * 180.0 / ny) * Math.PI / 180.0, (-180.0 + (i + 0.5) * 360.0 / nx) * Math.PI / 180.0);
                r["covermap_build_s"] = swb.Elapsed.TotalSeconds;
                double u0 = 6.0e6;
                var snow = new float[nx * ny]; var ice = new float[nx * ny];
                double first;
                var swf = Stopwatch.StartNew();
                for (int k = 0; k < sites.Length; k++) { var s = sites[k].Evaluate(u0); snow[k] = (float)s.SnowDepthM; ice[k] = (float)s.IceThicknessM; }
                first = swf.Elapsed.TotalMilliseconds;
                r["covermap_first_update_ms"] = first;
                var ups = new double[4];
                for (int u = 0; u < ups.Length; u++)
                {
                    var swu = Stopwatch.StartNew();
                    for (int k = 0; k < sites.Length; k++) { var s = sites[k].Evaluate(u0 + (u + 1) * 21600.0); snow[k] = (float)s.SnowDepthM; ice[k] = (float)s.IceThicknessM; }
                    ups[u] = swu.Elapsed.TotalMilliseconds;
                }
                Array.Sort(ups); r["covermap_next_step_update_median_ms"] = ups[2];
                long smp = 0; foreach (var s in sites) smp += s.SamplesTaken;
                r["covermap_samples_taken_total"] = smp;
            }

            string json = Json.Write(r);
            Console.Write(json);
            if (outFile != null) File.WriteAllText(outFile, json);
            return 0;
        }

        public static double[] FindSea(WeatherGenerator g, double latRad)
        {
            for (int i = 0; i < 360; i++)
            {
                double lon = (-180.0 + i) * Math.PI / 180.0;
                if (g.Grid.OceanAt(latRad, lon) > 0.95) return new[] { latRad, lon };
            }
            return new[] { latRad, 0.0 };
        }

        // ---- criteria 7 and 8 ---------------------------------------------------------------------------------------------

        private static ulong HashQueries(WeatherGenerator g, Q[] q, int[] order, double utShift)
        {
            var hs = new ulong[q.Length];
            Weather w;
            for (int k = 0; k < q.Length; k++)
            {
                int i = order == null ? k : order[k];
                g.Sample(q[i].Lat, q[i].Lon, q[i].Alt, q[i].Ut + utShift, out w);
                hs[i] = WeatherGenerator.HashOf(ref w, (ulong)i);
            }
            ulong h = 0xCBF29CE484222325UL;
            for (int i = 0; i < hs.Length; i++) h = Hash.Mix(h ^ hs[i]);
            return h;
        }

        private static ulong HashMap(WeatherGenerator g, int nx, int ny, double ut, int chunkRows)
        {
            ulong h = 0xCBF29CE484222325UL;
            Weather w;
            for (int j0 = 0; j0 < ny; j0 += chunkRows)
                for (int j = j0; j < Math.Min(ny, j0 + chunkRows); j++)
                    for (int i = 0; i < nx; i++)
                    {
                        g.Sample((90.0 - (j + 0.5) * 180.0 / ny) * Math.PI / 180.0, (-180.0 + (i + 0.5) * 360.0 / nx) * Math.PI / 180.0, 0.0, ut, out w);
                        h = WeatherGenerator.HashOf(ref w, h);
                    }
            return h;
        }

        public static int Determinism(string[] args)
        {
            string outFile = args.Length > 1 ? args[1] : null;
            var r = new Dictionary<string, object>();
            var bp = BodyParams.KerbinLike();
            var g1 = new WeatherGenerator(bp, 1);
            var q = Queries(20000, 77, 3.0e7);
            ulong h1 = HashQueries(g1, q, null, 0), h2 = HashQueries(g1, q, null, 0);
            var g2 = new WeatherGenerator(bp.Clone(), 1);
            ulong h3 = HashQueries(g2, q, null, 0);
            ulong[] th = new ulong[4];
            var threads = new Thread[4];
            for (int t = 0; t < 4; t++)
            {
                int tt = t;
                threads[t] = new Thread(() => { th[tt] = HashQueries(g1, q, null, 0); });
                threads[t].Start();
            }
            foreach (var t in threads) t.Join();
            var order = new int[q.Length]; for (int i = 0; i < order.Length; i++) order[i] = i;
            var rr = new Rng(99);
            for (int i = order.Length - 1; i > 0; i--) { int k = (int)(rr.Uniform() * (i + 1)); int tmp = order[i]; order[i] = order[k]; order[k] = tmp; }
            ulong h4 = HashQueries(g1, q, order, 0);
            r["hash_run1"] = h1.ToString("X16"); r["hash_run2"] = h2.ToString("X16"); r["hash_second_instance"] = h3.ToString("X16");
            r["hash_shuffled_order"] = h4.ToString("X16");
            for (int t = 0; t < 4; t++) r["hash_thread" + t] = th[t].ToString("X16");
            bool same = h1 == h2 && h1 == h3 && h1 == h4 && h1 == th[0] && h1 == th[1] && h1 == th[2] && h1 == th[3];
            r["identical_runs_threads_order_instances"] = same;
            ulong m1 = HashMap(g1, 128, 64, 4.0e6, 1), m2 = HashMap(g1, 128, 64, 4.0e6, 7), m3 = HashMap(g1, 128, 64, 4.0e6, 64);
            r["map_hash_chunk1"] = m1.ToString("X16"); r["map_hash_chunk7"] = m2.ToString("X16"); r["map_hash_chunk64"] = m3.ToString("X16");
            bool chunks = m1 == m2 && m1 == m3;
            r["identical_chunkings"] = chunks;
            // another seed differs
            var gB = new WeatherGenerator(bp, 2);
            ulong hb = HashQueries(gB, q, null, 0);
            r["seed2_differs"] = hb != h1;

            // criterion 8: rewind and jumps
            ulong a = HashQueries(g1, q, null, 0);
            ulong back2 = HashQueries(g1, q, null, -172800.0);
            ulong fwd = HashQueries(g1, q, null, 864000.0);
            ulong again = HashQueries(g1, q, null, 0);
            // the value at UT - 2 days evaluated directly equals the one reached after a jump forward and back
            ulong direct = HashQueries(new WeatherGenerator(bp, 1), q, null, -172800.0);
            r["rewind_back2d_equals_direct"] = back2 == direct;
            r["rewind_return_equals_first"] = a == again;
            r["jump_forward_differs"] = fwd != a;
            bool pass = same && chunks && back2 == direct && a == again;
            r["pass"] = pass;
            string json = Json.Write(r);
            Console.Write(json);
            if (outFile != null) File.WriteAllText(outFile, json);
            return pass ? 0 : 1;
        }
    }
}
