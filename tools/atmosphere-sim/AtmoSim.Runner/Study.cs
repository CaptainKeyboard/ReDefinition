using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using AtmoSim.Core;

namespace AtmoSim.Runner
{
    // One criterion run: steps a body for some years and accumulates the raw numbers the criteria need. The criteria themselves are applied by Evaluate.
    public static class Study
    {
        public static readonly double[] SnowBandLat = { 0, 30, 45, 55, 70, 85 };

        public static double PrecipLevel(double rate, bool snow)
        {
            double[] r = snow ? new[] { 0.02, 0.10, 0.30, 0.80, 2.0, 5.0 } : new[] { 0.02, 0.5, 2.5, 7.6, 25.0, 60.0 };
            double[] l = { 0.04, 0.20, 0.40, 0.60, 0.80, 1.0 };
            if (rate <= r[0]) return 0.0;
            if (rate >= r[5]) return 1.0;
            for (int i = 0; i < 5; i++)
                if (rate < r[i + 1]) return l[i] + (l[i + 1] - l[i]) * (Math.Log10(rate) - Math.Log10(r[i])) / (Math.Log10(r[i + 1]) - Math.Log10(r[i]));
            return 1.0;
        }

        // H3: 0 none, 1 flurries, 2 light, 3 moderate, 4 heavy, 5 blizzard class.
        public static int Class(double rate, double snowFrac, double wind10)
        {
            if (rate < 0.02) return 0;
            if (snowFrac >= 0.5)
            {
                if (rate < 0.10) return 1; if (rate < 0.30) return 2; if (rate < 0.80) return 3;
                if (rate < 2.0) return 4;
                return wind10 >= 15.6 ? 5 : 4;
            }
            if (rate < 0.5) return 1; if (rate < 2.5) return 2; if (rate < 7.6) return 3; if (rate < 25.0) return 4;
            return 5;
        }

        public static string Arg(string[] args, string key, string def)
        {
            foreach (string a in args) if (a.StartsWith(key + "=")) return a.Substring(key.Length + 1);
            return def;
        }

        // run <name> <body> <nlon> <levels> <years> <seed> <stepS> <outroot> [coriolis=off] [ocean=off] [eddy=x] [detect=1] [dump=1] [wave=1] [conv=1]
        public static int Run(string[] args)
        {
            string name = args[1], bodyName = args[2];
            int nlon = int.Parse(args[3]), levels = int.Parse(args[4]);
            double years = double.Parse(args[5], CultureInfo.InvariantCulture);
            ulong seed = ulong.Parse(args[6]);
            double stepS = double.Parse(args[7], CultureInfo.InvariantCulture);
            string outDir = Path.Combine(args[8], name);
            Directory.CreateDirectory(outDir);
            var bp = Program.ByName(bodyName);
            var cfg = Timing.Grid(nlon, levels, stepS, seed);
            if (Arg(args, "coriolis", "on") == "off") cfg.CoriolisOn = false;
            if (Arg(args, "ocean", "on") == "off") cfg.OceanOn = false;
            bp.EddyScale = double.Parse(Arg(args, "eddy", "1"), CultureInfo.InvariantCulture);
            bool detect = Arg(args, "detect", "0") == "1", dump = Arg(args, "dump", "0") == "1", wave = Arg(args, "wave", "0") == "1", conv = Arg(args, "conv", "0") == "1";

            var sim = new Atmosphere(bp, cfg);
            int n2 = sim.N2, nl = sim.NLon, nt = sim.NLat, K = sim.K;
            var g = new MetricGrid(sim);
            double wsum = g.WSum;
            var sig = sim.SigmaLevels;
            int k08 = 0, k09 = 0;
            for (int k = 0; k < K; k++) { if (Math.Abs(sig[k] - 0.8) < Math.Abs(sig[k08] - 0.8)) k08 = k; if (Math.Abs(sig[k] - 0.9) < Math.Abs(sig[k09] - 0.9)) k09 = k; }
            long totalSteps = (long)Math.Round(years * bp.YearS / stepS);
            long frameSteps = (long)Math.Round(21600.0 / stepS);
            long stepsPerYear = (long)Math.Round(bp.YearS / stepS);
            double dtH = stepS / 3600.0;
            var fl = detect ? new FrontsAndLows(sim, frameSteps * stepS, k08) : null;
            int frameStartIdx = -1;

            // Land and ocean masks and the cold-season tables.
            var land = new bool[n2]; var ocean = new bool[n2];
            for (int c = 0; c < n2; c++) { ocean[c] = sim.OceanFrac[c] >= 0.5; land[c] = !ocean[c]; }
            var cold = new bool[2][];
            for (int h = 0; h < 2; h++)
            {
                cold[h] = new bool[1000];
                double lat = (h == 0 ? 57.0 : -57.0) * Math.PI / 180.0; var q = new double[1000]; double qm = 0;
                for (int b = 0; b < 1000; b++)
                {
                    double ph = (b + 0.5) / 1000.0;
                    double decl = -bp.ObliquityRad * Math.Cos(2 * Math.PI * ph);
                    double d = 1.0 + bp.Eccentricity * Math.Cos(2 * Math.PI * (ph - bp.PerihelionPhase));
                    double x = -Math.Tan(lat) * Math.Tan(decl); double h0 = x >= 1 ? 0 : (x <= -1 ? Math.PI : Math.Acos(x));
                    q[b] = d * d * (h0 * Math.Sin(lat) * Math.Sin(decl) + Math.Cos(lat) * Math.Cos(decl) * Math.Sin(h0));
                    qm += q[b] / 1000.0;
                }
                for (int b = 0; b < 1000; b++) cold[h][b] = q[b] < qm;
            }

            // Accumulators.
            int nYears = (int)Math.Ceiling(years) + 1;
            var yrTsum = new double[nYears]; var yrTn = new long[nYears]; var yrWater = new double[nYears]; var yrWaterN = new long[nYears];
            var yrMaxWind = new double[nYears]; var yrSnowMax = new double[nYears]; var yrPrecip = new double[nYears]; var yrPrecipN = new long[nYears];
            var yrPrecipOcean = new double[nYears]; var yrPrecipOceanN = new double[nYears];
            var zonalPrecip = new double[nYears][]; for (int y = 0; y < nYears; y++) zonalPrecip[y] = new double[nt];
            var zonalPrecipN = new long[nYears];
            bool nan = false; double maxWindAll = 0, maxLedger = 0, maxSnowCell = 0;
            // wind histograms (0.1 m/s bins): all cells (area weighted), all (cell count), storm-track ocean (45-65), land
            const int HB = 1500; var hAllW = new double[HB]; var hAllN = new double[HB]; var hTrack = new double[HB]; var hLand = new double[HB];
            // site-hours (45-70, own cold season)
            var cls = new long[6]; long siteHours = 0, blizLiteral = 0, blizSnowOnly = 0;
            var clsSnow = new long[6]; var clsRain = new long[6];
            // snow line, rows, ice
            var latBandCells = new List<int>[2][];
            var bandLandCount = new int[2, SnowBandLat.Length];
            for (int h = 0; h < 2; h++)
            {
                latBandCells[h] = new List<int>[SnowBandLat.Length];
                for (int b = 0; b < SnowBandLat.Length; b++)
                {
                    latBandCells[h][b] = new List<int>();
                    for (int j = 0; j < nt; j++)
                    {
                        double lat = g.LatDeg[j]; double target = (h == 0 ? 1 : -1) * SnowBandLat[b];
                        if (b == 0 && h == 1) continue;
                        bool inBand = b == 0 ? Math.Abs(lat) <= 2.5 : Math.Abs(lat - target) <= 2.5;
                        if (!inBand) continue;
                        for (int i = 0; i < nl; i++) if (land[j * nl + i]) latBandCells[h][b].Add(j * nl + i);
                    }
                    bandLandCount[h, b] = latBandCells[h][b].Count;
                }
            }
            var snowSum = new double[2, SnowBandLat.Length, 4]; var snowN = new long[4];
            var rowCoverSum = new double[4, nt]; var rowN = new long[4];
            var rowIceSum = new double[4, nt]; var iceAreaSum = new double[2, 4]; var iceWinN = new long[4];
            const int PB = 100; var cycIce = new double[2, PB]; var cycN = new long[PB];
            long iceAnyStepsAfterSpin = 0, iceWarmCellSteps = 0; double maxIceAreaFrac = 0;
            double cc = 0, cx = 0, cy = 0, cxx = 0, cyy = 0, cxy = 0; // snow vs cold precip correlation
            var warmRun = new int[n2]; long snowOnWarm = 0;
            int warmNeed = (int)Math.Round(3.0 * bp.RotationPeriodS / stepS);
            var prevSnow = new double[n2];
            // convective clusters
            long convCells = 0, convTropCells = 0, convTropTotal = 0;
            const int CB = 400; var hConv = new double[CB];
            var clusterLife = new List<double>(); var clusterArea = new List<double>();
            int[] prevLabel = new int[n2], curLabel = new int[n2];
            var prevAge = new Dictionary<int, int>(); int nextLabel = 1;
            // wave-1 in the Sun-fixed frame (last 30 body days)
            long waveStart = totalSteps - (long)Math.Round(30.0 * bp.RotationPeriodS / stepS);
            var wA = new double[4, nt]; var wU = new double[nt]; long waveN = 0;
            // daily precipitation maps for the seed comparison (days 10..70 of year 2)
            var dailyAccum = new double[n2]; long dailyN = 0; var daily = new List<float[]>();
            long dailyStart = stepsPerYear + (long)Math.Round(10.0 * 86400.0 / stepS), dailyEnd = dailyStart + (long)Math.Round(60.0 * 86400.0 / stepS);
            // smoothness: log10 histogram of |change|/dt for cloud coverage and precipitation level
            var smC = new long[80]; var smP = new long[80]; long smN = 0;
            var prevCov = new double[n2]; var prevLev = new double[n2]; bool havePrev = false;
            // site series (year 2)
            var siteRows = new[] { g.NearestRow(50.0 * Math.PI / 180), g.NearestRow(5.0 * Math.PI / 180), g.NearestRow(80.0 * Math.PI / 180) };
            var siteCsv = new System.Text.StringBuilder("ut_s,p50N_mmh,p5N_mmh,p80N_mmh\n");
            // dust
            long dummy = 0;
            // month dump
            long monthStart = stepsPerYear, monthSteps = (long)Math.Round(30.0 * 86400.0 / stepS);
            string[] fn = { "cwp", "precip", "psanom", "t08", "wind10", "snow", "ice", "convw", "u09", "v09" };
            var monthFrames = new List<float[]>[fn.Length]; for (int f = 0; f < fn.Length; f++) monthFrames[f] = new List<float[]>();
            var monthTimes = new List<double>();
            var snapT = new List<double>(); var snapDir = outDir;
            var eventsJson = new List<object>();
            double snow2cm = 0.02;
            var sw = Stopwatch.StartNew();

            for (long s = 0; s < totalSteps; s++)
            {
                prevSnow = (double[])sim.SnowW.Clone();
                sim.Step();
                double ut = sim.Ut;
                int y = (int)Math.Floor(ut / bp.YearS); if (y >= nYears) y = nYears - 1;
                double phase = ut / bp.YearS - Math.Floor(ut / bp.YearS);
                bool afterSpin = ut >= bp.YearS;
                if (double.IsNaN(sim.MaxWind) || double.IsInfinity(sim.MaxWind)) nan = true;
                maxWindAll = Math.Max(maxWindAll, sim.MaxWind); yrMaxWind[y] = Math.Max(yrMaxWind[y], sim.MaxWind);
                maxLedger = Math.Max(maxLedger, Math.Abs(sim.WaterLedgerResidual));
                // global means
                double tsum = 0, psum = 0, posum = 0, wo = 0, snowMean = 0;
                for (int j = 0; j < nt; j++)
                {
                    double w = g.W[j]; double rt = 0, rp = 0, rs = 0;
                    for (int i = 0; i < nl; i++)
                    {
                        int c = j * nl + i; double t = sim.T2m[c], p = sim.PrecipMmH[c];
                        rt += t; rp += p; rs += sim.SnowW[c];
                        if (ocean[c]) { posum += w * p; wo += w; }
                        if (sim.SnowW[c] > maxSnowCell) maxSnowCell = sim.SnowW[c];
                        if (double.IsNaN(t) || double.IsNaN(p)) nan = true;
                    }
                    tsum += w * rt; psum += w * rp; snowMean += w * rs;
                    zonalPrecip[y][j] += rp / nl;
                }
                zonalPrecipN[y]++;
                yrTsum[y] += tsum / wsum; yrTn[y]++; yrPrecip[y] += psum / wsum; yrPrecipN[y]++;
                snowMean /= wsum; if (snowMean > yrSnowMax[y]) yrSnowMax[y] = snowMean;
                if (wo > 0) { yrPrecipOcean[y] += posum / wo; yrPrecipOceanN[y]++; }
                if (s % 6 == 0) { yrWater[y] += sim.AtmosphereWaterKgM2(); yrWaterN[y]++; }

                if (afterSpin)
                {
                    // wind percentiles and site-hours
                    int hemiCold0 = cold[0][(int)(phase * 1000) % 1000] ? 1 : 0, hemiCold1 = cold[1][(int)(phase * 1000) % 1000] ? 1 : 0;
                    for (int j = 0; j < nt; j++)
                    {
                        double lat = g.LatDeg[j], alat = Math.Abs(lat);
                        bool site = alat >= 45 && alat <= 70 && (lat > 0 ? hemiCold0 == 1 : hemiCold1 == 1);
                        for (int i = 0; i < nl; i++)
                        {
                            int c = j * nl + i; double w10 = sim.Wind10[c];
                            int hb = Math.Min(HB - 1, (int)(w10 * 10)); hAllW[hb] += g.W[j]; hAllN[hb]++;
                            if (land[c]) hLand[hb]++;
                            if (ocean[c] && alat >= 45 && alat <= 65) hTrack[hb]++;
                            if (site)
                            {
                                siteHours++;
                                double r = sim.PrecipMmH[c]; double sf = r > 0 ? sim.SnowFall[c] / r : 0;
                                int k = Class(r, sf, w10); cls[k]++;
                                if (sf >= 0.5) clsSnow[k]++; else clsRain[k]++;
                                if (r >= 2.0 && w10 >= 15.6) { blizLiteral++; if (sf >= 0.5) blizSnowOnly++; }
                            }
                        }
                    }
                    // smoothness (first post-spin year only)
                    if (y == 1)
                    {
                        for (int c = 0; c < n2; c++)
                        {
                            double cov = Math.Min(1.0, sim.CloudPath[c] / 0.3);
                            double r = sim.PrecipMmH[c]; double lev = PrecipLevel(r, r > 0 && sim.SnowFall[c] / r >= 0.5);
                            if (havePrev)
                            {
                                double dc = Math.Abs(cov - prevCov[c]) / stepS, dp = Math.Abs(lev - prevLev[c]) / stepS;
                                smC[Bin(dc)]++; smP[Bin(dp)]++; smN++;
                            }
                            prevCov[c] = cov; prevLev[c] = lev;
                        }
                        havePrev = true;
                    }
                    // convective updraft (years 2 and 3 only)
                    if (conv && y <= 2)
                    {
                        for (int j = 0; j < nt; j++)
                        {
                            bool trop = Math.Abs(g.LatDeg[j]) < 20;
                            for (int i = 0; i < nl; i++)
                            {
                                int c = j * nl + i; double w = sim.ConvW[c];
                                if (trop) { convTropTotal++; hConv[Math.Min(CB - 1, (int)(w * 10))]++; if (w >= 2.0) convTropCells++; }
                                if (w >= 2.0) convCells++;
                            }
                        }
                        Label(sim.ConvW, nl, nt, curLabel, ref nextLabel, prevLabel, prevAge, clusterLife, clusterArea, stepS);
                        var tmp = prevLabel; prevLabel = curLabel; curLabel = tmp;
                    }
                }

                // snow and ice tables (years 3-5), snow vs cold precipitation, warm-snow check
                if (y >= 1)
                {
                    for (int c = 0; c < n2; c++)
                    {
                        if (land[c])
                        {
                            if (sim.T2m[c] > 278.0) warmRun[c]++; else warmRun[c] = 0;
                            if (warmRun[c] >= warmNeed && sim.SnowW[c] >= snow2cm) snowOnWarm++;
                        }
                    }
                    for (int j = 0; j < nt; j++)
                    {
                        double alat = Math.Abs(g.LatDeg[j]); if (alat < 40 || alat > 75) continue;
                        for (int i = 0; i < nl; i++)
                        {
                            int c = j * nl + i; if (!land[c] || sim.T2m[c] >= 270.0) continue;
                            double xx = sim.SnowFall[c] * dtH * 1e-3, yy = sim.SnowW[c] - prevSnow[c];
                            cc++; cx += xx; cy += yy; cxx += xx * xx; cyy += yy * yy; cxy += xx * yy;
                        }
                    }
                }
                if (y >= 2 && y <= 4)
                {
                    int si = SeasonIndex(phase);
                    // ice area per hemisphere and annual cycle
                    var ia = new double[2];
                    for (int j = 0; j < nt; j++)
                    {
                        int h = g.LatDeg[j] >= 0 ? 0 : 1;
                        for (int i = 0; i < nl; i++)
                        {
                            int c = j * nl + i;
                            if (ocean[c] && sim.IceH[c] > 0) ia[h] += g.W[j] * (1.0 - Math.Exp(-sim.IceH[c] / 0.15)) / wsum;
                        }
                    }
                    int pb = Math.Min(PB - 1, (int)(phase * PB)); cycN[pb]++; cycIce[0, pb] += ia[0]; cycIce[1, pb] += ia[1];
                    if (si >= 0)
                    {
                        snowN[si]++; rowN[si]++; iceWinN[si]++;
                        for (int h = 0; h < 2; h++)
                        {
                            int seas = h == 0 ? si : (si + 2) % 4;
                            iceAreaSum[h, seas] += ia[h];
                            for (int b = 0; b < SnowBandLat.Length; b++)
                            {
                                var cells = latBandCells[h][b]; if (cells.Count == 0) continue;
                                int cov = 0; foreach (int c in cells) if (sim.SnowW[c] >= snow2cm) cov++;
                                snowSum[h, b, seas] += (double)cov / cells.Count;
                            }
                        }
                        for (int j = 0; j < nt; j++)
                        {
                            int h = g.LatDeg[j] >= 0 ? 0 : 1; int seas = h == 0 ? si : (si + 2) % 4;
                            int nlandRow = 0, cv = 0, oc = 0; double ice = 0;
                            for (int i = 0; i < nl; i++)
                            {
                                int c = j * nl + i;
                                if (land[c]) { nlandRow++; if (sim.SnowW[c] >= snow2cm) cv++; }
                                else { oc++; ice += 1.0 - Math.Exp(-sim.IceH[c] / 0.15); }
                            }
                            rowCoverSum[seas, j] += nlandRow > 0 ? (double)cv / nlandRow : double.NaN;
                            rowIceSum[seas, j] += oc > 0 ? ice / oc : double.NaN;
                        }
                    }
                }
                // sea-ice sanity
                if (afterSpin)
                {
                    bool any = false;
                    for (int c = 0; c < n2; c++)
                    {
                        if (sim.IceH[c] > 0) { any = true; if (ocean[c] && sim.Ts[c] > sim.Body.SeaFreezeK + 0.5) iceWarmCellSteps++; }
                    }
                    if (any) iceAnyStepsAfterSpin++;
                }
                // wave-1 in the Sun-fixed frame
                if (wave && s >= waveStart)
                {
                    double day = ut / bp.RotationPeriodS; day -= Math.Floor(day);
                    double subLon = Math.PI - 2 * Math.PI * day;
                    for (int j = 0; j < nt; j++)
                    {
                        if (Math.Abs(g.LatDeg[j]) > 45) continue;
                        double au = 0, bu = 0, av = 0, bv = 0, zu = 0;
                        for (int i = 0; i < nl; i++)
                        {
                            int c = k09 * n2 + j * nl + i; double lam = g.LonRad[i] - subLon;
                            double co = Math.Cos(lam), si2 = Math.Sin(lam);
                            au += sim.U3[c] * co; bu += sim.U3[c] * si2; av += sim.V3[c] * co; bv += sim.V3[c] * si2; zu += sim.U3[c];
                        }
                        wA[0, j] += 2.0 * au / nl; wA[1, j] += 2.0 * bu / nl; wA[2, j] += 2.0 * av / nl; wA[3, j] += 2.0 * bv / nl; wU[j] += zu / nl;
                    }
                    waveN++;
                }
                // daily precipitation maps
                if (s >= dailyStart && s < dailyEnd)
                {
                    for (int c = 0; c < n2; c++) dailyAccum[c] += sim.PrecipMmH[c];
                    dailyN++;
                    if (dailyN * stepS >= 86400.0 - 1e-6)
                    {
                        var m = new float[n2]; for (int c = 0; c < n2; c++) m[c] = (float)(dailyAccum[c] / dailyN);
                        daily.Add(m); Array.Clear(dailyAccum, 0, n2); dailyN = 0;
                    }
                }
                // site series in year 2
                if (y == 1 && s % Math.Max(1, (long)(3600.0 / stepS)) == 0)
                    siteCsv.Append(ut.ToString("F0", CultureInfo.InvariantCulture)).Append(',').Append(sim.PrecipMmH[siteRows[0] * nl + nl / 4].ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                        .Append(sim.PrecipMmH[siteRows[1] * nl + nl / 4].ToString("F4", CultureInfo.InvariantCulture)).Append(',').Append(sim.PrecipMmH[siteRows[2] * nl + nl / 4].ToString("F4", CultureInfo.InvariantCulture)).Append('\n');
                // frames
                if ((s + 1) % frameSteps == 0)
                {
                    bool inMonth = dump && s >= monthStart && s < monthStart + monthSteps;
                    if (fl != null && afterSpin) { if (frameStartIdx < 0) frameStartIdx = fl.Frames; fl.Frame(sim, true); }
                    if (inMonth)
                    {
                        monthTimes.Add(ut);
                        var cur = new[] { sim.CloudPath, sim.PrecipMmH, sim.PsAnom, null, sim.Wind10, sim.SnowW, sim.IceH, sim.ConvW, null, null };
                        for (int f = 0; f < fn.Length; f++)
                        {
                            var a = new float[n2];
                            for (int c = 0; c < n2; c++)
                                a[c] = (float)(f == 3 ? sim.T3[k08 * n2 + c] : f == 8 ? sim.U3[k09 * n2 + c] : f == 9 ? sim.V3[k09 * n2 + c] : cur[f][c]);
                            monthFrames[f].Add(a);
                        }
                        // strongest convective cell of the frame
                        int bc = 0; for (int c = 1; c < n2; c++) if (sim.ConvW[c] > sim.ConvW[bc]) bc = c;
                        var ev = new Dictionary<string, object>();
                        ev["ut"] = ut; ev["conv_max_ms"] = sim.ConvW[bc]; ev["conv_lat"] = g.LatDeg[bc / nl]; ev["conv_lon"] = g.LonRad[bc % nl] * 180 / Math.PI;
                        if (fl != null && fl.BandsByFrame.Count > 0)
                        {
                            var bl = new List<object>(); foreach (var b in fl.BandsByFrame[fl.BandsByFrame.Count - 1]) bl.Add(new Dictionary<string, object> { { "lat", b.Lat * 180 / Math.PI }, { "lon", b.Lon * 180 / Math.PI }, { "extent_km", b.ExtentKm }, { "aspect", b.Aspect }, { "axis_deg", b.AxisDeg }, { "arm", b.Arm } });
                            ev["bands"] = bl;
                            var ml = new List<object>(); foreach (var m in fl.MinimaByFrame[fl.MinimaByFrame.Count - 1]) ml.Add(new[] { m.Lat * 180 / Math.PI, m.Lon * 180 / Math.PI, m.Value });
                            ev["minima"] = ml;
                        }
                        eventsJson.Add(ev);
                        long idx = (s - monthStart) / frameSteps;
                        if (idx % 20 == 10)
                        {
                            snapT.Add(ut);
                            WriteF32(Path.Combine(outDir, "snap" + (snapT.Count - 1) + "_t3.f32"), sim.T3); WriteF32(Path.Combine(outDir, "snap" + (snapT.Count - 1) + "_u3.f32"), sim.U3);
                            WriteF32(Path.Combine(outDir, "snap" + (snapT.Count - 1) + "_v3.f32"), sim.V3); WriteF32(Path.Combine(outDir, "snap" + (snapT.Count - 1) + "_w3.f32"), sim.Omega3);
                            WriteF32(Path.Combine(outDir, "snap" + (snapT.Count - 1) + "_qc3.f32"), sim.Qc); WriteF32(Path.Combine(outDir, "snap" + (snapT.Count - 1) + "_qv3.f32"), sim.Qv);
                        }
                    }
                }
                if (s % 2000 == 0) Console.Error.WriteLine(name + " step " + s + "/" + totalSteps + " " + (sw.Elapsed.TotalSeconds).ToString("F0") + " s");
            }
            double wall = sw.Elapsed.TotalSeconds;

            // ---- assemble stats ----
            var st = new Dictionary<string, object>();
            st["name"] = name; st["body"] = bodyName; st["grid"] = nl + "x" + nt + "x" + K; st["years"] = years; st["seed"] = seed; st["step_s"] = stepS;
            st["coriolis_on"] = cfg.CoriolisOn; st["ocean_on"] = cfg.OceanOn; st["eddy_scale"] = bp.EddyScale; st["maps"] = "synthetic (MapGen), B09 data absent";
            st["wall_s"] = wall; st["ms_per_step"] = wall * 1000.0 / totalSteps; st["final_hash"] = sim.StateHash().ToString("X16");
            st["nan"] = nan; st["max_wind_ms"] = maxWindAll; st["max_ledger_residual_kgm2"] = maxLedger; st["max_snow_cell_m"] = maxSnowCell;
            st["surface_mean_k_target"] = bp.SurfaceMeanK; st["deformation_radius_km"] = sim.DeformationRadiusM / 1000.0;
            var yearly = new List<object>();
            for (int y = 0; y < nYears; y++)
            {
                if (yrTn[y] == 0) continue;
                yearly.Add(new Dictionary<string, object> {
                    { "year", y + 1 }, { "steps", yrTn[y] }, { "mean_t2m_k", yrTsum[y] / yrTn[y] }, { "mean_atm_water_kgm2", yrWaterN[y] > 0 ? yrWater[y] / yrWaterN[y] : double.NaN },
                    { "max_wind_ms", yrMaxWind[y] }, { "max_global_mean_snow_m", yrSnowMax[y] }, { "mean_precip_mmh", yrPrecip[y] / yrPrecipN[y] },
                    { "mean_precip_ocean_mmh", yrPrecipOceanN[y] > 0 ? yrPrecipOcean[y] / yrPrecipOceanN[y] : double.NaN } });
            }
            st["yearly"] = yearly;
            // zonal-mean precipitation: mean over full years >= 2
            var zm = new double[nt]; long zn = 0;
            for (int y = 1; y < nYears; y++) { if (zonalPrecipN[y] == 0) continue; for (int j = 0; j < nt; j++) zm[j] += zonalPrecip[y][j]; zn += zonalPrecipN[y]; }
            for (int j = 0; j < nt; j++) zm[j] = zn > 0 ? zm[j] / zn : double.NaN;
            st["lat_deg"] = g.LatDeg; st["zonal_mean_precip_mmh"] = zm; st["lat_weight"] = g.W;
            double gm = 0; for (int j = 0; j < nt; j++) gm += zm[j] * g.W[j] * nl; st["global_mean_precip_mmh_years2plus"] = gm / wsum;
            // wind percentiles
            st["wind10_all_area_weighted_p50_p95_p99"] = new[] { Pct(hAllW, 0.5), Pct(hAllW, 0.95), Pct(hAllW, 0.99) };
            st["wind10_all_cells_p50_p95_p99"] = new[] { Pct(hAllN, 0.5), Pct(hAllN, 0.95), Pct(hAllN, 0.99) };
            st["wind10_storm_track_ocean_p50_p95_p99"] = new[] { Pct(hTrack, 0.5), Pct(hTrack, 0.95), Pct(hTrack, 0.99) };
            st["wind10_land_p50_p95_p99"] = new[] { Pct(hLand, 0.5), Pct(hLand, 0.95), Pct(hLand, 0.99) };
            // site hours
            var sh = new Dictionary<string, object>();
            sh["site_hours"] = siteHours; sh["class_counts_none_flurries_light_moderate_heavy_blizzard"] = new List<long>(cls);
            sh["snow_counts"] = new List<long>(clsSnow); sh["rain_counts"] = new List<long>(clsRain);
            sh["blizzard_condition_literal"] = blizLiteral; sh["blizzard_condition_snow_only"] = blizSnowOnly;
            st["site_hours"] = sh;
            // snow-line table
            var tab = new List<object>();
            for (int h = 0; h < 2; h++)
                for (int b = 0; b < SnowBandLat.Length; b++)
                {
                    if (b == 0 && h == 1) continue;
                    var row = new double[4]; for (int s2 = 0; s2 < 4; s2++) row[s2] = SnowDiv(snowSum[h, b, s2], snowN, h, s2);
                    tab.Add(new Dictionary<string, object> { { "hemisphere", h == 0 ? "N" : "S" }, { "band_deg", SnowBandLat[b] }, { "land_cells", bandLandCount[h, b] }, { "cover_winter_spring_summer_autumn", row } });
                }
            st["snow_line_table"] = tab;
            var rc = new List<object>(); var ri = new List<object>();
            for (int s2 = 0; s2 < 4; s2++)
            {
                var a = new double[nt]; var b2 = new double[nt];
                for (int j = 0; j < nt; j++) { a[j] = rowN[s2] > 0 ? rowCoverSum[s2, j] / rowN[s2] : double.NaN; b2[j] = rowN[s2] > 0 ? rowIceSum[s2, j] / rowN[s2] : double.NaN; }
                rc.Add(a); ri.Add(b2);
            }
            st["row_cover_by_season_own_hemisphere"] = rc; st["row_ice_by_season_own_hemisphere"] = ri;
            var iceA = new double[2, 4]; for (int h = 0; h < 2; h++) for (int s2 = 0; s2 < 4; s2++) iceA[h, s2] = iceWinN[s2] > 0 ? iceAreaSum[h, s2] / iceWinN[s2] : double.NaN;
            st["ice_area_frac_by_season_N"] = new[] { iceA[0, 0], iceA[0, 1], iceA[0, 2], iceA[0, 3] }; st["ice_area_frac_by_season_S"] = new[] { iceA[1, 0], iceA[1, 1], iceA[1, 2], iceA[1, 3] };
            var cyc0 = new double[PB]; var cyc1 = new double[PB];
            for (int b = 0; b < PB; b++) { cyc0[b] = cycN[b] > 0 ? cycIce[0, b] / cycN[b] : double.NaN; cyc1[b] = cycN[b] > 0 ? cycIce[1, b] / cycN[b] : double.NaN; }
            st["ice_cycle_N_by_phase"] = cyc0; st["ice_cycle_S_by_phase"] = cyc1;
            st["ice_steps_after_spin"] = iceAnyStepsAfterSpin; st["ice_warm_cell_steps_over_0p5K"] = iceWarmCellSteps;
            double nn = cc, cov_ = cxy / nn - (cx / nn) * (cy / nn), vx = cxx / nn - (cx / nn) * (cx / nn), vy = cyy / nn - (cy / nn) * (cy / nn);
            st["snowfall_vs_dsnow_correlation_cold_land_40_75"] = nn > 10 && vx > 0 && vy > 0 ? cov_ / Math.Sqrt(vx * vy) : double.NaN;
            st["snow_cells_after_3_warm_days"] = snowOnWarm;
            if (conv)
            {
                var cv = new Dictionary<string, object>();
                cv["tropical_cell_steps"] = convTropTotal; cv["tropical_cells_over_2ms"] = convTropCells; cv["share_tropical"] = convTropTotal > 0 ? (double)convTropCells / convTropTotal : double.NaN;
                cv["updraft_tropics_p95_p99_ms"] = new[] { Pct(hConv, 0.95) , Pct(hConv, 0.99) };
                cv["cluster_count"] = clusterLife.Count; cv["cluster_lifetime_h_median"] = Tracker.Median(clusterLife) / 3600.0; cv["cluster_area_cells_median"] = Tracker.Median(clusterArea);
                cv["cell_km_lon_equator"] = g.Dx[nt / 2] / 1000.0; cv["cell_km_lat"] = g.Dy[nt / 2] / 1000.0;
                st["convective"] = cv;
            }
            st["dust"] = new Dictionary<string, object> { { "land_cell_steps", sim.DustLandCellSteps }, { "lift_cell_steps", sim.DustLiftCellSteps }, { "lift_below_threshold", sim.DustLiftBelowThreshold }, { "threshold_ms", bp.DustWindThresholdMs } };
            if (wave && waveN > 0)
            {
                double w1 = 0, z1 = 0; int nr = 0;
                for (int j = 0; j < nt; j++)
                {
                    if (Math.Abs(g.LatDeg[j]) > 45) continue;
                    double a0 = wA[0, j] / waveN, a1 = wA[1, j] / waveN, a2 = wA[2, j] / waveN, a3 = wA[3, j] / waveN;
                    w1 += Math.Sqrt(a0 * a0 + a1 * a1 + a2 * a2 + a3 * a3); z1 += Math.Abs(wU[j] / waveN); nr++;
                }
                st["wave1"] = new Dictionary<string, object> { { "amp_wave1_ms", w1 / nr }, { "amp_zonal_ms", z1 / nr }, { "ratio", (w1 / nr) / Math.Max(1e-9, z1 / nr) }, { "steps", waveN } };
            }
            // smoothness
            st["smoothness"] = new Dictionary<string, object> { { "coverage_p99_per_s", HistP99(smC, smN) }, { "precip_level_p99_per_s", HistP99(smP, smN) }, { "samples", smN }, { "coverage_def", "min(1, cloud water path / 0.3 kg m-2)" } };
            if (fl != null)
            {
                int f0 = Math.Max(0, frameStartIdx);
                st["detectors"] = fl.Summary(f0, fl.Frames);
            }
            File.WriteAllText(Path.Combine(outDir, "stats.json"), Json.Write(st));
            File.WriteAllText(Path.Combine(outDir, "sites_year2.csv"), siteCsv.ToString());
            // daily maps
            if (daily.Count > 0) WriteF32(Path.Combine(outDir, "daily_precip.f32"), daily);
            if (dump && monthTimes.Count > 0)
            {
                for (int f = 0; f < fn.Length; f++) WriteF32(Path.Combine(outDir, "month_" + fn[f] + ".f32"), monthFrames[f]);
                var meta = new Dictionary<string, object> { { "nlon", nl }, { "nlat", nt }, { "levels", K }, { "frames", monthTimes.Count }, { "times_s", monthTimes }, { "fields", fn }, { "lat_deg", g.LatDeg }, { "sigma", sig },
                    { "ocean_frac_file", "ocean.f32" }, { "snapshots_s", snapT }, { "rotation_s", bp.RotationPeriodS }, { "year_s", bp.YearS }, { "frame_s", frameSteps * stepS } };
                File.WriteAllText(Path.Combine(outDir, "month_meta.json"), Json.Write(meta));
                File.WriteAllText(Path.Combine(outDir, "month_events.json"), Json.Write(eventsJson));
                WriteF32(Path.Combine(outDir, "ocean.f32"), sim.OceanFrac); WriteF32(Path.Combine(outDir, "terrain.f32"), sim.TerrainM);
            }
            Console.WriteLine(name + " done in " + wall.ToString("F0") + " s, hash " + sim.StateHash().ToString("X16"));
            return 0;
        }

        private static double SnowDiv(double sum, long[] n, int h, int seas)
        {
            // n is indexed by the phase window; the own-season index maps back to the phase window of the hemisphere.
            int si = h == 0 ? seas : (seas + 2) % 4;
            return n[si] > 0 ? sum / n[si] : double.NaN;
        }

        // The season window (0 = phase 0 ... 3 = phase 0.75, +-0.05 year) or -1.
        public static int SeasonIndex(double phase)
        {
            for (int s = 0; s < 4; s++)
            {
                double d = Math.Abs(phase - s * 0.25); if (d > 0.5) d = 1.0 - d;
                if (d <= 0.05) return s;
            }
            if (phase >= 0.95) return 0;
            return -1;
        }

        private static int Bin(double v)
        {
            if (v <= 1e-12) return 0;
            int b = (int)(Math.Log10(v) * 5.0 + 60.0); // 5 bins per decade from 1e-12
            return Math.Max(0, Math.Min(79, b));
        }

        private static double HistP99(long[] h, long n)
        {
            if (n == 0) return double.NaN;
            long target = (long)(0.99 * n), acc = 0;
            for (int b = 0; b < h.Length; b++) { acc += h[b]; if (acc >= target) return Math.Pow(10.0, (b + 1 - 60.0) / 5.0); }
            return double.NaN;
        }

        private static double Pct(double[] hist, double q)
        {
            double tot = 0; foreach (double v in hist) tot += v;
            double acc = 0;
            for (int b = 0; b < hist.Length; b++) { acc += hist[b]; if (acc >= q * tot) return (b + 0.5) / 10.0; }
            return double.NaN;
        }

        // 4-connected labelling of ConvW >= 2 with overlap tracking to the previous step.
        private static void Label(double[] w, int nl, int nt, int[] cur, ref int next, int[] prev, Dictionary<int, int> age, List<double> life, List<double> area, double stepS)
        {
            Array.Clear(cur, 0, cur.Length);
            var comps = new List<List<int>>();
            var stack = new Stack<int>();
            for (int c0 = 0; c0 < w.Length; c0++)
            {
                if (w[c0] < 2.0 || cur[c0] != 0) continue;
                var cells = new List<int>(); cur[c0] = -1; stack.Push(c0);
                while (stack.Count > 0)
                {
                    int c = stack.Pop(); cells.Add(c);
                    int j = c / nl, i = c % nl;
                    int[] nb = { j > 0 ? c - nl : -1, j < nt - 1 ? c + nl : -1, j * nl + (i + 1) % nl, j * nl + (i - 1 + nl) % nl };
                    foreach (int d in nb) if (d >= 0 && w[d] >= 2.0 && cur[d] == 0) { cur[d] = -1; stack.Push(d); }
                }
                comps.Add(cells);
            }
            var newAge = new Dictionary<int, int>();
            var claimed = new HashSet<int>();
            foreach (var cells in comps)
            {
                var votes = new Dictionary<int, int>();
                foreach (int c in cells) if (prev[c] > 0) { int v; votes.TryGetValue(prev[c], out v); votes[prev[c]] = v + 1; }
                int best = 0, bv = 0; foreach (var kv in votes) if (kv.Value > bv && !claimed.Contains(kv.Key)) { best = kv.Key; bv = kv.Value; }
                int lab; int a = 1;
                if (best > 0) { lab = best; claimed.Add(best); a = age[best] + 1; } else lab = next++;
                foreach (int c in cells) cur[c] = lab;
                newAge[lab] = a; area.Add(cells.Count);
            }
            foreach (var kv in age) if (!newAge.ContainsKey(kv.Key)) life.Add(kv.Value * stepS);
            age.Clear(); foreach (var kv in newAge) age[kv.Key] = kv.Value;
        }

        public static void WriteF32(string path, double[] a)
        {
            using (var bw = new BinaryWriter(File.Create(path))) foreach (double v in a) bw.Write((float)v);
        }

        public static void WriteF32(string path, List<float[]> frames)
        {
            using (var bw = new BinaryWriter(File.Create(path))) foreach (var f in frames) foreach (float v in f) bw.Write(v);
        }
    }
}
