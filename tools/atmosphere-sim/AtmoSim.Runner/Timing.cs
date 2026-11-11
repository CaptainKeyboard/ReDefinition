using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AtmoSim.Core;

namespace AtmoSim.Runner
{
    // Criterion 5 (cost), 6 (determinism and rewind), 8 (forecast) and 13 (bounded catch-up). These runs do not look at any climate output.
    public static class Timing
    {
        public static SimConfig Grid(int nlon, int levels, double stepS, ulong seed)
        {
            return new SimConfig { NLon = nlon, NLat = nlon / 2, Levels = levels, StepSeconds = stepS, Seed = seed };
        }

        // Step cost: median of 200 steps after 24 warm-up steps, single thread; resident memory and checkpoint size.
        public static int Cost(string[] args)
        {
            var o = new List<string>();
            foreach (var g in new[] { new[] { 128, 16 }, new[] { 96, 16 }, new[] { 64, 12 } })
            {
                var cfg = Grid(g[0], g[1], 3600.0, 1);
                var sim = new Atmosphere(BodyParams.KerbinLike(), cfg);
                for (int i = 0; i < 24; i++) sim.Step();
                var ms = new double[200];
                var sw = new Stopwatch();
                for (int i = 0; i < 200; i++) { sw.Restart(); sim.Step(); ms[i] = sw.Elapsed.TotalMilliseconds; }
                Array.Sort(ms);
                double med = ms[100], p95 = ms[190];
                var st = new MemoryStream(); sim.SaveCheckpoint(st);
                string line = "{\"grid\":\"" + g[0] + "x" + g[0] / 2 + "x" + g[1] + "\",\"median_ms\":" + F(med) + ",\"p95_ms\":" + F(p95)
                    + ",\"resident_bytes\":" + sim.ResidentBytes() + ",\"checkpoint_bytes\":" + st.Length + ",\"resident_two_trajectories_bytes\":" + 2 * sim.ResidentBytes() + "}";
                Console.WriteLine(line); o.Add(line);
            }
            if (args.Length > 1) File.WriteAllText(args[1], "[" + string.Join(",\n", o) + "]\n");
            return 0;
        }

        public static string F(double v) { return v.ToString("R", System.Globalization.CultureInfo.InvariantCulture); }

        // Criterion 6: hashes across runs, threads and chunkings; checkpoint at day 10 + advance to day 20; restore at arbitrary earlier times via the
        // epoch pipeline; catch-up of one year from scratch and from a checkpoint.
        public static int Determinism(string[] args)
        {
            int nlon = int.Parse(args[1]), levels = int.Parse(args[2]);
            string outFile = args[3];
            var bp = BodyParams.KerbinLike();
            var cfg = Grid(nlon, levels, 3600.0, 1);
            long day = (long)(bp.RotationPeriodS / cfg.StepSeconds);
            var r = new Dictionary<string, object>();

            // Two runs and two threads (30 days).
            long n30 = 30 * day;
            ulong h1 = Straight(bp, cfg, n30, null);
            ulong h2 = Straight(bp, cfg, n30, null);
            ulong h3 = 0, h4 = 0;
            var t1 = new Thread(() => { h3 = Straight(bp, cfg, n30, null); });
            var t2 = new Thread(() => { h4 = Straight(bp, cfg, n30, null); });
            t1.Start(); t2.Start(); t1.Join(); t2.Join();
            r["hash_run1"] = h1.ToString("X16"); r["hash_run2"] = h2.ToString("X16"); r["hash_thread1"] = h3.ToString("X16"); r["hash_thread2"] = h4.ToString("X16");
            // Chunkings of the advance loop.
            ulong hc1 = Straight(bp, cfg, n30, new[] { 1 }), hc7 = Straight(bp, cfg, n30, new[] { 7 }), hc24 = Straight(bp, cfg, n30, new[] { 24, 5, 1 });
            r["hash_chunk1"] = hc1.ToString("X16"); r["hash_chunk7"] = hc7.ToString("X16"); r["hash_chunk_mixed"] = hc24.ToString("X16");
            bool same = h1 == h2 && h1 == h3 && h1 == h4 && h1 == hc1 && h1 == hc7 && h1 == hc24;
            r["identical_runs_threads_chunkings"] = same;

            // Checkpoint at day 10 plus advance to day 20 equals the straight run.
            var a = new Atmosphere(bp, cfg);
            for (long i = 0; i < 10 * day; i++) a.Step();
            var ms = new MemoryStream(); a.SaveCheckpoint(ms);
            var b = new Atmosphere(bp, cfg); ms.Position = 0; b.LoadCheckpoint(ms);
            for (long i = 0; i < 10 * day; i++) { a.Step(); b.Step(); }
            r["checkpoint_day10_to_day20_equal"] = a.StateHash() == b.StateHash();
            r["hash_day20"] = a.StateHash().ToString("X16");

            // Catch-up of 1 year from scratch and from a checkpoint at 0.9 year.
            long yearSteps = (long)(bp.YearS / cfg.StepSeconds);
            var sw = Stopwatch.StartNew();
            var c = new Atmosphere(bp, cfg);
            long ck = (long)(0.9 * yearSteps);
            MemoryStream cks = null;
            for (long i = 0; i < yearSteps; i++) { c.Step(); if (i + 1 == ck) { cks = new MemoryStream(); c.SaveCheckpoint(cks); } }
            double scratchS = sw.Elapsed.TotalSeconds;
            sw.Restart();
            var d = new Atmosphere(bp, cfg); cks.Position = 0; d.LoadCheckpoint(cks);
            for (long i = ck; i < yearSteps; i++) d.Step();
            double fromCkS = sw.Elapsed.TotalSeconds;
            r["catchup_year_from_scratch_s"] = scratchS; r["catchup_year_from_checkpoint_0p9_s"] = fromCkS;
            r["catchup_from_checkpoint_equals_straight"] = c.StateHash() == d.StateHash();
            r["checkpoint_bytes"] = cks.Length;
            File.WriteAllText(outFile, Json.Write(r));
            Console.WriteLine(Json.Write(r));
            return same ? 0 : 1;
        }

        private static ulong Straight(BodyParams bp, SimConfig cfg, long steps, int[] chunks)
        {
            var s = new Atmosphere(bp, cfg);
            long done = 0; int ci = 0;
            while (done < steps)
            {
                int n = chunks == null ? (int)(steps - done) : chunks[ci++ % chunks.Length];
                if (n > steps - done) n = (int)(steps - done);
                for (int i = 0; i < n; i++) s.Step();
                done += n;
            }
            return s.StateHash();
        }

        // Criterion 8: clone + 24 steps equals the continuation; cost; the same across an epoch boundary through the epoch pipeline.
        public static int Forecast(string[] args)
        {
            int nlon = int.Parse(args[1]), levels = int.Parse(args[2]);
            double stepCostS = double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) / 1000.0;
            string outFile = args[4];
            var bp = BodyParams.KerbinLike();
            var cfg = Grid(nlon, levels, 3600.0, 1);
            var r = new Dictionary<string, object>();
            var s = new Atmosphere(bp, cfg);
            for (int i = 0; i < 100; i++) s.Step();
            var sw = Stopwatch.StartNew();
            var clone = s.Clone();
            for (int i = 0; i < 24; i++) clone.Step();
            double cloneS = sw.Elapsed.TotalSeconds;
            sw.Restart();
            for (int i = 0; i < 24; i++) s.Step();
            double rollS = sw.Elapsed.TotalSeconds;
            r["clone_plus_24_steps_s"] = cloneS; r["rolling_24_steps_s"] = rollS; r["clone_equals_continuation"] = clone.StateHash() == s.StateHash();

            // Across an epoch boundary: a fresh epoch pipeline asked at ut + 24 h equals the rolling pipeline asked step by step through the crossfade.
            var d = EpochSetup.Derive(bp, cfg, stepCostS);
            double boundary = d.EpochSteps * cfg.StepSeconds;
            double t0 = boundary - 6 * cfg.StepSeconds;
            var rolling = new EpochField(bp, cfg, d.EpochSteps, d.SpinUp, d.Crossfade);
            Atmosphere nw, old; double w;
            for (int i = 0; i <= 30; i++) rolling.StateAt(t0 + i * cfg.StepSeconds, out nw, out old, out w);
            var fresh = new EpochField(bp, cfg, d.EpochSteps, d.SpinUp, d.Crossfade);
            fresh.StateAt(t0 + 30 * cfg.StepSeconds, out nw, out old, out w);
            ulong hFresh = nw.StateHash();
            rolling.StateAt(t0 + 30 * cfg.StepSeconds, out Atmosphere nw2, out Atmosphere old2, out double w2);
            r["epoch_steps"] = d.EpochSteps; r["epoch_forecast_across_boundary_equal"] = hFresh == nw2.StateHash() && w == w2;
            r["epoch_forecast_older_equal"] = (old == null && old2 == null) || (old != null && old2 != null && old.StateHash() == old2.StateHash());
            File.WriteAllText(outFile, Json.Write(r));
            Console.WriteLine(Json.Write(r));
            return 0;
        }
    }

    public struct EpochSetup
    {
        public long EpochSteps, SpinUp, Crossfade;
        // H-1: spin-up 20 body days, crossfade 4 body days; epoch = floor(20 s / stepCost) - 2 spinUp - 2 crossfade.
        public static EpochSetup Derive(BodyParams bp, SimConfig cfg, double stepCostS)
        {
            var e = new EpochSetup();
            e.SpinUp = (long)(20.0 * bp.RotationPeriodS / cfg.StepSeconds);
            e.Crossfade = (long)(4.0 * bp.RotationPeriodS / cfg.StepSeconds);
            e.EpochSteps = EpochField.DeriveEpochSteps(stepCostS, 20.0, e.SpinUp, e.Crossfade);
            return e;
        }
    }
}
