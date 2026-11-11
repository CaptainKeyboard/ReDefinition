using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AtmoSim.Core;

namespace AtmoSim.Runner
{
    // Read-only diagnostics of the tuning rounds (not criteria): they sample the generator at the sites of a criterion and print the
    // distribution of the intermediate quantities, so a lever is chosen from numbers.
    public static class Diagnostics
    {
        private static string F(double v) { return v.ToString("G4", CultureInfo.InvariantCulture); }

        // diag-levels [seed]: at the criterion 4 sites (Kerbin-like, 45 to 70 degrees, cold season, 6 h) the split of the precipitating samples
        // into stratiform and convective rate, the distribution of the stratiform driver X, and the level shares of each part.
        public static int Levels(string[] args)
        {
            ulong seed = args.Length > 1 ? ulong.Parse(args[1], CultureInfo.InvariantCulture) : 1UL;
            var bp = Study.ByName("kerbin");
            var g = new WeatherGenerator(bp, seed);
            int nx = 256, ny = 128;
            double year = bp.YearS; Weather w;
            double fref = g.D.FrontRefKPer100Km;
            var xs = new List<float>(); var hs = new List<float>(); var us = new List<float>(); var fs = new List<float>();
            var hts = new List<float>(); var hrel = new List<float>(); var tt = new List<float>();
            long n = 0, prec = 0, convOnly = 0, stratOnly = 0, both = 0;
            long[] lvAll = new long[6], lvStrat = new long[6], lvConv = new long[6];
            // log2 histogram of the rate (mm/h) from 2^-8 to 2^4
            long[] hist = new long[14];
            long xPos = 0, xAbove = 0;
            for (int hemi = 0; hemi < 2; hemi++)
            {
                double phase0 = hemi == 0 ? 0.0 : 0.5;
                double t0 = (phase0 - 0.125) * year, t1 = (phase0 + 0.125) * year;
                for (double ut = t0; ut < t1; ut += 21600.0)
                    for (int j = 0; j < ny; j++)
                    {
                        double latD = 90.0 - (j + 0.5) * 180.0 / ny;
                        if (hemi == 0 ? (latD < 45.0 || latD > 70.0) : (latD > -45.0 || latD < -70.0)) continue;
                        double lat = latD * Math.PI / 180.0;
                        for (int i = 0; i < nx; i++)
                        {
                            g.Sample(lat, 2.0 * Math.PI * (i + 0.5) / nx, 0.0, ut, out w);
                            n++;
                            double frontN = Math.Min(1.0, w.FrontKPer100Km / fref);
                            double x = w.Humidity * (WeatherGenerator.UpliftGain * w.Uplift + WeatherGenerator.FrontGain * frontN);
                            if ((n % 7) == 0)
                            {
                                xs.Add((float)x); hs.Add((float)w.Humidity); us.Add((float)w.Uplift); fs.Add((float)frontN);
                                double ht = 1.0 / (1.0 + Math.Exp(-0.07 * (w.TempMeanK - bp.SurfaceMeanK)));
                                hts.Add((float)ht); hrel.Add((float)Math.Min(1.0, w.Humidity / ht)); tt.Add((float)w.TempMeanK);
                            }
                            if (x > 0) xPos++;
                            if (x > bp.StratThreshold) xAbove++;
                            if (w.PrecipMmH < 0.05) continue;
                            prec++;
                            double conv = w.ConvectiveShare * w.PrecipMmH, strat = w.PrecipMmH - conv;
                            bool hasC = conv > 1e-9, hasS = strat > 1e-9;
                            if (hasC && hasS) both++; else if (hasC) convOnly++; else stratOnly++;
                            lvAll[(int)g.LevelOf(w.PrecipMmH, w.Type, w.WindSpeed)]++;
                            lvStrat[(int)g.LevelOf(strat, w.Type, 0.0)]++;
                            lvConv[(int)g.LevelOf(conv, w.Type, 0.0)]++;
                            int b = (int)Math.Floor(Math.Log(w.PrecipMmH, 2.0)) + 8; if (b < 0) b = 0; if (b > 13) b = 13;
                            hist[b]++;
                        }
                    }
            }
            Console.WriteLine("seed " + seed + " mode " + (Study.ReconDir == null ? "S" : "R") + ": samples " + n + ", precipitating (>= 0.05 mm/h) " + F((double)prec / n));
            Console.WriteLine("precipitating split: stratiform only " + F((double)stratOnly / prec) + ", convective only " + F((double)convOnly / prec) + ", both " + F((double)both / prec));
            Console.WriteLine("levels (none, flurries, light, moderate, heavy, blizzard) of the precipitating samples (share of all samples):");
            Console.WriteLine("  total " + string.Join(" ", Array.ConvertAll(lvAll, v => F((double)v / n))));
            Console.WriteLine("  by the stratiform part " + string.Join(" ", Array.ConvertAll(lvStrat, v => F((double)v / n))));
            Console.WriteLine("  by the convective part " + string.Join(" ", Array.ConvertAll(lvConv, v => F((double)v / n))));
            var sb = new StringBuilder("rate histogram (log2 bins from 2^-8 mm/h, share of all samples): ");
            for (int b = 0; b < hist.Length; b++) sb.Append("2^" + (b - 8) + ":" + F((double)hist[b] / n) + " ");
            Console.WriteLine(sb.ToString());
            Console.WriteLine("X driver (humidity x (1.1 uplift + 0.7 front)): share X > 0 " + F((double)xPos / n) + ", share X > threshold " + F((double)xAbove / n));
            xs.Sort(); hs.Sort(); us.Sort(); fs.Sort(); hts.Sort(); hrel.Sort(); tt.Sort();
            Console.WriteLine("  temperature factor h_T: " + Pct(hts));
            Console.WriteLine("  moisture source part H / h_T: " + Pct(hrel));
            Console.WriteLine("  mean surface temperature (K): " + Pct(tt));
            Console.WriteLine("  percentiles 10/25/50/75/90/95/99 of X: " + Pct(xs));
            Console.WriteLine("  humidity: " + Pct(hs));
            Console.WriteLine("  uplift: " + Pct(us));
            Console.WriteLine("  front (F/Fref capped at 1): " + Pct(fs));
            return 0;
        }

        // diag-micro: cost per call of the elementary functions and of the grid lookup (nanoseconds), to see where Sample spends its time.
        public static int Micro(string[] args)
        {
            const int N = 4000000;
            double acc = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int rep = 0; rep < 2; rep++)
            {
                sw.Restart(); for (int i = 0; i < N; i++) acc += DMath.Exp(-0.0000013 * i);
                if (rep == 1) Console.WriteLine("Exp " + F(sw.Elapsed.TotalMilliseconds * 1e6 / N) + " ns");
                sw.Restart(); for (int i = 0; i < N; i++) acc += DMath.Sin(0.0000021 * i);
                if (rep == 1) Console.WriteLine("Sin " + F(sw.Elapsed.TotalMilliseconds * 1e6 / N) + " ns");
                sw.Restart(); for (int i = 0; i < N; i++) acc += DMath.Acos(-0.9 + 0.0000004 * i);
                if (rep == 1) Console.WriteLine("Acos " + F(sw.Elapsed.TotalMilliseconds * 1e6 / N) + " ns");
                sw.Restart(); for (int i = 0; i < N; i++) acc += DMath.Tanh(0.0000013 * i - 2.0);
                if (rep == 1) Console.WriteLine("Tanh " + F(sw.Elapsed.TotalMilliseconds * 1e6 / N) + " ns");
                sw.Restart(); for (int i = 0; i < N; i++) acc += Hash.Draw(Hash.Of(7UL, i, 3, 5), 2);
                if (rep == 1) Console.WriteLine("Hash.Of + Draw " + F(sw.Elapsed.TotalMilliseconds * 1e6 / N) + " ns");
                var g = new WeatherGenerator(Study.ByName("kerbin"), 1);
                double o, m;
                sw.Restart(); for (int i = 0; i < N; i++) { g.Grid.Lookup(0.0000003 * i - 1.2, 0.0000007 * i - 3.0, out o, out m); acc += o + m; }
                if (rep == 1) Console.WriteLine("Grid.Lookup " + F(sw.Elapsed.TotalMilliseconds * 1e6 / N) + " ns");
                Weather w;
                sw.Restart(); for (int i = 0; i < N / 8; i++) { g.Sample(0.0000003 * i - 0.6, 0.0000007 * i - 3.0, 0.0, 1.0e6, out w); acc += w.TempK; }
                if (rep == 1) Console.WriteLine("Sample (same UT, sweeping) " + F(sw.Elapsed.TotalMilliseconds * 1e6 / (N / 8)) + " ns");
            }
            // the map-pattern benchmark of criterion 6 with parts of the generator switched off
            for (int variant = 0; variant < 4; variant++)
            {
                var bp = BodyParams.KerbinLike();
                if (variant == 1) bp.CellsEnabled = false;
                if (variant == 2) bp.WavesEnabled = false;
                if (variant == 3) { bp.CellsEnabled = false; bp.WavesEnabled = false; }
                var g = new WeatherGenerator(bp, 1);
                Weather w;
                var q = Timing.Queries(40000, 5, 3.0e7);
                for (int i = 0; i < 20000; i++) g.Sample(0.8, 0.01 * i, 0.0, 1000.0, out w);
                var tick = new long[q.Length];
                for (int rep = 0; rep < 3; rep++)
                {
                    for (int i = 0; i < q.Length; i++)
                    {
                        double ut = 1.0e6 + (i / 256) * 3600.0;
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        g.Sample(q[i].Lat, q[i].Lon, q[i].Alt, ut, out w);
                        tick[i] = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    }
                }
                Array.Sort(tick);
                double toUs = 1e6 / System.Diagnostics.Stopwatch.Frequency;
                Console.WriteLine("benchmark pattern, variant " + (variant == 0 ? "full" : variant == 1 ? "no cells" : variant == 2 ? "no waves" : "no cells no waves") + ": median " + F(tick[q.Length / 2] * toUs) + " us, mean " + F(Mean(tick) * toUs) + " us, p95 " + F(tick[(int)(0.95 * q.Length)] * toUs) + " us");
            }
            Console.WriteLine(acc);
            return 0;
        }

        // diag-surfacehash: a hash of the surface states (snow, ice, wet, dust) of many sites at many times, to prove an optimisation of SurfaceLayer.cs bit-identical.
        public static int SurfaceHash(string[] args)
        {
            ulong h = 0x1234567UL;
            foreach (string set in new[] { "kerbin", "duna", "cold" })
            {
                var bp = Study.ByName(set);
                var g = new WeatherGenerator(bp, 1);
                int sites = 0;
                for (int k = 0; k < 40; k++)
                {
                    double lat = Math.Asin(2.0 * ((k * 0.6180339887) % 1.0) - 1.0);
                    double lon = (((k * 0.7548776662) % 1.0) * 2.0 - 1.0) * Math.PI;
                    var s = new SurfaceSite(g, lat, lon);
                    sites++;
                    for (int m = 0; m < 30; m++)
                    {
                        double ut = 1.0e6 + m * 0.9e6 + (m % 3) * 7000.0 + k * 1234.5;
                        for (int rep = 0; rep < 2; rep++)
                        {
                            var st = s.Evaluate(ut + rep * 900.0);
                            h = Hash.Fold(h, st.SnowSweMm); h = Hash.Fold(h, st.SnowDepthM); h = Hash.Fold(h, st.IceThicknessM); h = Hash.Fold(h, st.IceFraction);
                            h = Hash.Fold(h, st.MixedLayerK); h = Hash.Fold(h, st.Wet01); h = Hash.Fold(h, st.DustDepositMm); h = Hash.Fold(h, st.DustLifting ? 1.0 : 0.0);
                        }
                    }
                }
                Console.WriteLine(set + ": " + sites + " sites");
            }
            Console.WriteLine("surface hash " + h.ToString("X16"));
            return 0;
        }

        // diag-climate [set]: the climatological surface temperature (longitude mean) by latitude and year phase against the annual-mean profile.
        public static int Climate(string[] args)
        {
            var bp = Study.ByName(args.Length > 1 ? args[1] : "kerbin");
            var g = new WeatherGenerator(bp, 1);
            Console.WriteLine("lat   annual-model/profile  phase0 (N midwinter)  0.25  0.5  0.75   land-mean winter  sea-mean winter");
            foreach (double latD in new[] { 0.0, 15.0, 30.0, 40.0, 45.0, 50.0, 55.0, 60.0, 70.0, 80.0 })
            {
                double lat = latD * Math.PI / 180.0;
                var line = new StringBuilder(latD.ToString("F0", CultureInfo.InvariantCulture).PadLeft(3) + "   " + F(g.D.AnnualK(lat, Math.Sin(lat))).PadLeft(8));
                double land0 = 0, sea0 = 0; int nl = 0, ns = 0;
                for (int ph = 0; ph < 4; ph++)
                {
                    double ut = ph * 0.25 * bp.YearS, s = 0;
                    for (int i = 0; i < 72; i++)
                    {
                        double lon = 2.0 * Math.PI * (i + 0.5) / 72;
                        double t = g.ClimateTemperature(lat, lon, ut);
                        s += t;
                        if (ph == 0) { if (g.Grid.OceanAt(lat, lon) >= 0.5) { sea0 += t; ns++; } else { land0 += t; nl++; } }
                    }
                    line.Append("   " + F(s / 72).PadLeft(8));
                }
                line.Append("   " + (nl > 0 ? F(land0 / nl) : "-") + "   " + (ns > 0 ? F(sea0 / ns) : "-"));
                Console.WriteLine(line.ToString());
            }
            return 0;
        }

        private static double Mean(long[] a) { double s = 0; foreach (long v in a) s += v; return s / a.Length; }

        private static string Pct(List<float> v)
        {
            var sb = new StringBuilder();
            foreach (double p in new[] { 0.10, 0.25, 0.50, 0.75, 0.90, 0.95, 0.99 }) sb.Append(F(v[(int)(p * (v.Count - 1))]) + " ");
            return sb.ToString();
        }
    }
}
