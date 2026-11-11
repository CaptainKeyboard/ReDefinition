using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using AtmoSim.Core;

namespace AtmoSim.Runner
{
    // Criterion 13 (bounded catch-up and crossfade smoothness) and the across-epoch continuity of criteria 9 and 10.
    // epoch <nlon> <levels> <stepCostMs> <outFile> [measured=6]
    public static class EpochStudy
    {
        public static int Run(string[] args)
        {
            int nlon = int.Parse(args[1]), levels = int.Parse(args[2]);
            double stepCostS = double.Parse(args[3], CultureInfo.InvariantCulture) / 1000.0;
            string outFile = args[4];
            int measured = args.Length > 5 ? int.Parse(args[5]) : 6;
            var bp = BodyParams.KerbinLike();
            var cfg = Timing.Grid(nlon, levels, 3600.0, 1);
            var d = EpochSetup.Derive(bp, cfg, stepCostS);
            double E = d.EpochSteps * cfg.StepSeconds, X = d.Crossfade * cfg.StepSeconds, U = d.SpinUp * cfg.StepSeconds;
            var r = new Dictionary<string, object>();
            r["step_cost_s"] = stepCostS; r["epoch_steps"] = d.EpochSteps; r["spin_up_steps"] = d.SpinUp; r["crossfade_steps"] = d.Crossfade;
            r["epoch_days_earth"] = E / 86400.0; r["epoch_body_days"] = E / bp.RotationPeriodS; r["epoch_as_year_fraction"] = E / bp.YearS;

            // Analytic catch-up: steps taken for 50 arbitrary UTs (deterministic), worst case and the worst possible UT.
            var rng = new Random(2024);
            long worst = 0; var uts = new List<double>(); var stepsList = new List<long>();
            for (int k = 0; k < 50; k++)
            {
                double ut = rng.NextDouble() * 30.0 * bp.YearS;
                long st = StepsFor(ut, E, X, U, cfg.StepSeconds, d);
                uts.Add(ut); stepsList.Add(st); worst = Math.Max(worst, st);
            }
            double worstUt = Math.Floor(30 * bp.YearS / E) * E + X; // end of a crossfade window: the longest path
            long worstPossible = StepsFor(worstUt, E, X, U, cfg.StepSeconds, d);
            r["steps_50_uts_max"] = worst; r["catchup_50_uts_max_s_at_step_cost"] = worst * stepCostS;
            r["steps_worst_possible"] = worstPossible; r["catchup_worst_possible_s_at_step_cost"] = worstPossible * stepCostS;

            // Measured catch-up for some UTs including the worst possible one.
            var meas = new List<object>();
            var pick = new List<double>();
            pick.Add(worstUt); pick.Add(Math.Floor(5 * bp.YearS / E) * E + 0.5 * E);
            for (int k = 0; k < measured - 2; k++) pick.Add(uts[k * 7 % uts.Count]);
            double worstWall = 0;
            foreach (double ut in pick)
            {
                var ef = new EpochField(bp, cfg, d.EpochSteps, d.SpinUp, d.Crossfade);
                var sw = Stopwatch.StartNew();
                Atmosphere a, b; double w;
                ef.StateAt(Math.Floor(ut / cfg.StepSeconds) * cfg.StepSeconds, out a, out b, out w);
                double wall = sw.Elapsed.TotalSeconds; worstWall = Math.Max(worstWall, wall);
                meas.Add(new Dictionary<string, object> { { "ut_s", ut }, { "steps_taken", ef.StepsTaken }, { "wall_s", wall }, { "resident_bytes", ef.ResidentBytes() } });
            }
            r["measured_catchups"] = meas; r["measured_worst_wall_s"] = worstWall;

            // Rewind: ten arbitrary earlier times asked in random order against a fresh pipeline each.
            var ef2 = new EpochField(bp, cfg, d.EpochSteps, d.SpinUp, d.Crossfade);
            var rt = new List<double>(); var rk = new Random(9);
            double baseUt = 3.0 * bp.YearS;
            for (int k = 0; k < 10; k++) rt.Add(Math.Floor((baseUt + rk.NextDouble() * 3.0 * E) / cfg.StepSeconds) * cfg.StepSeconds);
            int equal = 0; var hashes = new List<string>();
            foreach (double ut in rt)
            {
                Atmosphere a, b; double w;
                ef2.StateAt(ut, out a, out b, out w);
                ulong h1 = a.StateHash(); ulong hb = b != null ? b.StateHash() : 0;
                var ef3 = new EpochField(bp, cfg, d.EpochSteps, d.SpinUp, d.Crossfade);
                Atmosphere a3, b3; double w3;
                ef3.StateAt(ut, out a3, out b3, out w3);
                if (a3.StateHash() == h1 && (b3 == null ? 0UL : b3.StateHash()) == hb && w3 == w) equal++;
                hashes.Add(h1.ToString("X16"));
            }
            r["rewind_queries"] = rt.Count; r["rewind_equal_to_fresh"] = equal; r["rewind_hashes"] = hashes;

            // Crossfade smoothness and across-epoch continuity at two boundaries: NH early winter and NH summer of year 3.
            var cont = new List<object>();
            foreach (double ph in new[] { 0.12, 0.55 })
            {
                double target = (2.0 + ph) * bp.YearS;
                long eIdx = (long)Math.Round(target / E);
                double boundary = eIdx * E;
                cont.Add(Boundary(bp, cfg, d, boundary, E, X, ph));
            }
            r["boundaries"] = cont;
            File.WriteAllText(outFile, Json.Write(r));
            Console.WriteLine(Json.Write(r));
            return 0;
        }

        // Steps taken by a fresh pipeline to answer one query: the newer trajectory (spin-up + the time since its epoch start) and, inside the crossfade,
        // the older one (spin-up + a full epoch + the time into the crossfade).
        private static long StepsFor(double ut, double E, double X, double U, double dt, EpochSetup d)
        {
            long e = (long)Math.Floor(ut / E);
            double since = ut - e * E;
            long newer = d.SpinUp + (long)Math.Floor(since / dt);
            long total = newer;
            if (since < X && e > 0) total += d.SpinUp + (long)Math.Floor((since + E) / dt);
            return total;
        }

        private static Dictionary<string, object> Boundary(BodyParams bp, SimConfig cfg, EpochSetup d, double boundary, double E, double X, double phase)
        {
            var ef = new EpochField(bp, cfg, d.EpochSteps, d.SpinUp, d.Crossfade);
            var covHist = new List<double[]>(); var preHist = new List<double[]>();
            var outside = new List<double>(); var inside = new List<double>(); var outsideP = new List<double>(); var insideP = new List<double>();
            double dt = cfg.StepSeconds;
            double t0 = boundary - 24 * dt, t1 = boundary + X + 24 * dt;
            int n2 = cfg.NLon * (cfg.NLon / 2);
            double[] prevCov = null, prevLev = null;
            double[] covStart = null, covEnd = null; double[] iceStartEdge = null, iceEndEdge = null;
            var blendCwp = new double[n2]; var blendP = new double[n2]; var blendSf = new double[n2]; var blendSnow = new double[n2]; var blendIce = new double[n2];
            MetricGrid g = null; bool[] land = null;
            for (double ut = t0; ut <= t1 + 1e-6; ut += dt)
            {
                Atmosphere a, b; double w;
                ef.StateAt(ut, out a, out b, out w);
                if (g == null) { g = new MetricGrid(a); land = new bool[n2]; for (int c = 0; c < n2; c++) land[c] = a.OceanFrac[c] < 0.5; }
                EpochField.Blend(a.CloudPath, b != null ? b.CloudPath : null, w, blendCwp);
                EpochField.Blend(a.PrecipMmH, b != null ? b.PrecipMmH : null, w, blendP);
                EpochField.Blend(a.SnowFall, b != null ? b.SnowFall : null, w, blendSf);
                EpochField.Blend(a.SnowW, b != null ? b.SnowW : null, w, blendSnow);
                EpochField.Blend(a.IceH, b != null ? b.IceH : null, w, blendIce);
                var cov = new double[n2]; var lev = new double[n2];
                for (int c = 0; c < n2; c++)
                {
                    cov[c] = Math.Min(1.0, blendCwp[c] / 0.3);
                    lev[c] = Study.PrecipLevel(blendP[c], blendP[c] > 0 && blendSf[c] / blendP[c] >= 0.5);
                }
                if (prevCov != null)
                {
                    bool inWin = ut > boundary + 1e-6 && ut <= boundary + X + 1e-6;
                    for (int c = 0; c < n2; c++)
                    {
                        double dc = Math.Abs(cov[c] - prevCov[c]) / dt, dp = Math.Abs(lev[c] - prevLev[c]) / dt;
                        if (inWin) { inside.Add(dc); insideP.Add(dp); } else { outside.Add(dc); outsideP.Add(dp); }
                    }
                }
                prevCov = cov; prevLev = lev;
                if (Math.Abs(ut - boundary) < 1e-6) { covStart = SnowCover(g, land, blendSnow, cfg.NLon); iceStartEdge = IceEdge(g, land, blendIce, cfg.NLon); }
                if (Math.Abs(ut - (boundary + X)) < 1e-6) { covEnd = SnowCover(g, land, blendSnow, cfg.NLon); iceEndEdge = IceEdge(g, land, blendIce, cfg.NLon); }
            }
            var r = new Dictionary<string, object>();
            r["boundary_ut_s"] = boundary; r["year_phase_target"] = phase; r["actual_phase"] = boundary / bp.YearS - Math.Floor(boundary / bp.YearS);
            inside.Sort(); outside.Sort(); insideP.Sort(); outsideP.Sort();
            r["coverage_p99_per_s_inside"] = P99(inside); r["coverage_p99_per_s_outside"] = P99(outside);
            r["precip_level_p99_per_s_inside"] = P99(insideP); r["precip_level_p99_per_s_outside"] = P99(outsideP);
            r["cover_bands_start_0_30_45_55_70_85_N"] = covStart; r["cover_bands_end_0_30_45_55_70_85_N"] = covEnd;
            if (covStart != null && covEnd != null)
            {
                double mx = 0; for (int i = 0; i < covStart.Length; i++) { double dj = Math.Abs(covEnd[i] - covStart[i]); if (!double.IsNaN(dj)) mx = Math.Max(mx, dj); }
                r["cover_max_jump"] = mx;
            }
            r["ice_edge_start_N_S_deg"] = iceStartEdge; r["ice_edge_end_N_S_deg"] = iceEndEdge;
            if (iceStartEdge != null && iceEndEdge != null) r["ice_edge_max_jump_deg"] = Math.Max(Math.Abs(iceStartEdge[0] - iceEndEdge[0]), Math.Abs(iceStartEdge[1] - iceEndEdge[1]));
            return r;
        }

        private static double P99(List<double> sorted) { return sorted.Count == 0 ? double.NaN : sorted[(int)(0.99 * (sorted.Count - 1))]; }

        private static double[] SnowCover(MetricGrid g, bool[] land, double[] snow, int nl)
        {
            var bands = Study.SnowBandLat; var r = new double[bands.Length];
            for (int b = 0; b < bands.Length; b++)
            {
                int n = 0, cv = 0;
                for (int j = 0; j < g.NLat; j++)
                {
                    bool inb = b == 0 ? Math.Abs(g.LatDeg[j]) <= 2.5 : Math.Abs(g.LatDeg[j] - bands[b]) <= 2.5;
                    if (!inb) continue;
                    for (int i = 0; i < nl; i++) { int c = j * nl + i; if (!land[c]) continue; n++; if (snow[c] >= 0.02) cv++; }
                }
                r[b] = n > 0 ? (double)cv / n : double.NaN;
            }
            return r;
        }

        // Ice edge latitude (lowest latitude with zonal-mean ice fraction of the ocean cells >= 0.15) for north and south; 90 when none.
        private static double[] IceEdge(MetricGrid g, bool[] land, double[] ice, int nl)
        {
            var r = new[] { 90.0, 90.0 };
            for (int j = 0; j < g.NLat; j++)
            {
                int oc = 0; double s = 0;
                for (int i = 0; i < nl; i++) { int c = j * nl + i; if (land[c]) continue; oc++; s += 1.0 - Math.Exp(-ice[c] / 0.15); }
                if (oc == 0 || s / oc < 0.15) continue;
                int h = g.LatDeg[j] >= 0 ? 0 : 1;
                r[h] = Math.Min(r[h], Math.Abs(g.LatDeg[j]));
            }
            return r;
        }
    }
}
