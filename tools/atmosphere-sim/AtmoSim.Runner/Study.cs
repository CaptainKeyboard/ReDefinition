using System;
using System.Collections.Generic;
using System.IO;
using AtmoSim.Core;

namespace AtmoSim.Runner
{
    // The frame pipeline of the criteria 1, 2 and 12: the generator evaluated on the 256x128 evaluation grid every frame step, the
    // detectors D1 (pressure minima) and D2 (front bands) and their trackers on top. Raw numbers go to a JSON file; the header
    // thresholds are applied by Evaluate.
    public static class Study
    {
        public const int EvalLon = 256, EvalLat = 128;

        // When set (option --recon), the four bodies the B09 probe dumped get their real height and ocean grid and the climatology fitted to KSP's temperature samples.
        public static string ReconDir;

        public static BodyParams ByName(string n)
        {
            var p = BySet(n);
            if (ReconDir == null) return p;
            string rb = n == "duna" ? "Duna" : n == "eve" ? "Eve" : n == "laythe" ? "Laythe" : n == "earth" ? null : "Kerbin";
            if (rb == null) return p;
            ReconData.Apply(p, rb, ReconDir);
            if (n == "cold") { p.SurfaceMeanK -= 30.0; for (int k = 0; k < p.ProfileK.Length; k++) p.ProfileK[k] -= 30.0; }
            if (n == "slow") p.DiurnalLandAmpK = 20.0;
            if (n == "noocean") { Array.Clear(p.Grid.Ocean, 0, p.Grid.Ocean.Length); p.Grid.BuildMoist(MapGen.MoistBlurDeg); }
            return p;
        }

        // The fits of the four dumped bodies: data
        public static int Data(string[] args)
        {
            string dir = ReconDir ?? ReconData.FindDumpDir();
            if (dir == null) { Console.WriteLine("no dumps found"); return 1; }
            foreach (string body in new[] { "Kerbin", "Duna", "Laythe", "Eve" })
            {
                var p = BySet(body.ToLowerInvariant());
                var f = ReconData.Apply(p, body, dir);
                double oc = 0, w = 0;
                for (int j = 0; j < p.Grid.NLat; j++) { double c = Math.Cos(p.Grid.LatDeg(j) * Math.PI / 180.0); for (int i = 0; i < p.Grid.NLon; i++) { oc += p.Grid.Ocean[j * p.Grid.NLon + i] * c; w += c; } }
                Console.WriteLine(body + ": mean " + f.MeanK.ToString("F1") + " K (equator " + f.EquatorK.ToString("F1") + ", pole " + f.PoleK.ToString("F1") + "), largest step between profile points " + f.MaxProfileStepK.ToString("F1")
                    + " K, lapse " + (f.LapseKPerM * 1000).ToString("F2") + " K/km, diurnal amplitude " + f.DiurnalAmpK.ToString("F2") + " K, ocean fraction " + (oc / w).ToString("F3"));
            }
            return 0;
        }

        public static BodyParams BySet(string n)
        {
            switch (n)
            {
                case "kerbin": return BodyParams.KerbinLike();
                case "duna": return BodyParams.DunaLike();
                case "eve": return BodyParams.EveLike();
                case "laythe": return BodyParams.LaytheLike();
                case "slow": return BodyParams.SlowRotator();
                case "noocean": return BodyParams.NoOcean();
                case "cold": return BodyParams.ColdWorld();
                case "locked": return BodyParams.TidallyLockedWorld();
                case "earth": return BodyParams.EarthLike();
            }
            throw new ArgumentException("body " + n);
        }

        // Fills the detector input grids (row 0 north; the metric grid has longitudes (i + 0.5) 360/NLon from 0 to 2 pi).
        public static void Fields(WeatherGenerator g, double ut, int nx, int ny, double[] p, double[] t, double[] cwp, double[] precip)
        {
            Weather w;
            for (int j = 0; j < ny; j++)
            {
                double lat = (90.0 - (j + 0.5) * 180.0 / ny) * Math.PI / 180.0;
                for (int i = 0; i < nx; i++)
                {
                    double lon = 2.0 * Math.PI * (i + 0.5) / nx;
                    g.Sample(lat, lon, 0.0, ut, out w);
                    int k = j * nx + i;
                    p[k] = w.PressureAnomPa; t[k] = w.TempK; cwp[k] = w.CloudWater;
                    if (precip != null) precip[k] = w.PrecipMmH;
                }
            }
        }

        // The tracker run. args: tracker <body> <days> <seed> <frameHours> <out.json> [wavesOff]
        public static int Tracker(string[] args)
        {
            var bp = ByName(args[1]);
            double days = double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
            ulong seed = ulong.Parse(args[3]);
            double frameH = double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
            string outFile = args[5];
            if (args.Length > 6 && args[6] == "wavesOff") bp.WavesEnabled = false;
            var g = new WeatherGenerator(bp, seed);
            double frameS = frameH * 3600.0;
            int frames = (int)(days * 86400.0 / frameS);
            int n = EvalLon * EvalLat;
            var p = new double[n]; var t = new double[n]; var c = new double[n];
            double ringKm = Math.Max(3.0 * 2.0 * Math.PI * bp.RadiusM / EvalLon / 1000.0, 0.5 * g.D.SystemRadiusM / 1000.0);
            var fl = new FrontsAndLows(EvalLon, EvalLat, bp.RadiusM, bp.SurfacePressurePa, frameS, bp.RotationPeriodS, ringKm, Math.Max(1.5 * g.D.JetSpeed0, 3.0), 20);
            double ut0 = 2.0e6;
            for (int f = 0; f < frames; f++)
            {
                Fields(g, ut0 + f * frameS, EvalLon, EvalLat, p, t, c, null);
                fl.Frame(p, t, c, true);
            }
            var r = fl.Summary(0, frames);
            r["body"] = bp.Name; r["seed"] = seed; r["days"] = days; r["jet_speed_ms_annual"] = g.D.JetSpeed0; r["cells"] = g.D.Cells;
            r["system_radius_km"] = g.D.SystemRadiusM / 1000.0; r["slots_per_hemisphere"] = g.D.SlotsPerHemisphere;
            string json = Json.Write(r);
            Console.Write(json);
            File.WriteAllText(outFile, json);
            return 0;
        }

        // Zonal means over 10-degree bands and 40 days: zonal <body> <cells 0|1> <waves 0|1>
        public static int Zonal(string[] args)
        {
            var bp = ByName(args[1]);
            bp.CellsEnabled = args[2] == "1"; bp.WavesEnabled = args[3] == "1";
            var g = new WeatherGenerator(bp, 7);
            int nx = 72, ny = 36;
            var sum = new double[9, 7]; var wsum = new double[9];
            Weather w;
            for (int day = 0; day < 40; day++)
                for (int j = 0; j < ny; j++)
                {
                    double lat = (90.0 - (j + 0.5) * 180.0 / ny) * Math.PI / 180.0;
                    int band = (int)(Math.Abs(lat) * 180.0 / Math.PI / 10.0); if (band > 8) band = 8;
                    for (int i = 0; i < nx; i++)
                    {
                        double lon = (-180.0 + (i + 0.5) * 360.0 / nx) * Math.PI / 180.0;
                        g.Sample(lat, lon, 0.0, 3.0e6 + day * 86400.0 + i * 777.0, out w);
                        double c = Math.Cos(lat);
                        sum[band, 0] += w.PrecipMmH * c; sum[band, 1] += (w.PrecipMmH > 0.05 ? 1 : 0) * c; sum[band, 2] += w.Coverage * c;
                        sum[band, 3] += w.Humidity * c; sum[band, 4] += w.TempK * c; sum[band, 5] += w.WindSpeed * c; sum[band, 6] += w.CellStrength * c;
                        wsum[band] += c;
                    }
                }
            Console.WriteLine("band  precip(mm/h) rainShare coverage humidity T(K) wind cell");
            for (int b = 0; b < 9; b++)
                Console.WriteLine((b * 10) + "-" + (b * 10 + 10) + "  " + (sum[b, 0] / wsum[b]).ToString("F3") + "  " + (sum[b, 1] / wsum[b]).ToString("F3") + "  " + (sum[b, 2] / wsum[b]).ToString("F3")
                    + "  " + (sum[b, 3] / wsum[b]).ToString("F3") + "  " + (sum[b, 4] / wsum[b]).ToString("F1") + "  " + (sum[b, 5] / wsum[b]).ToString("F2") + "  " + (sum[b, 6] / wsum[b]).ToString("F3"));
            return 0;
        }

        // The frame export for the sheets: month <body> <seed> <startDays> <frameHours> <days> <outDir>. Raw float32 files of shape
        // (frames, 128, 256), row 0 north, longitude (i + 0.5) 360/256 from 0, plus meta and the detector events of every frame.
        public static int Month(string[] args)
        {
            var bp = ByName(args[1]);
            ulong seed = ulong.Parse(args[2]);
            double start = double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) * 86400.0;
            double frameS = double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) * 3600.0;
            int frames = (int)(double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) * 86400.0 / frameS);
            string dir = args[6];
            Directory.CreateDirectory(dir);
            var g = new WeatherGenerator(bp, seed);
            var mg = new MetricGrid(EvalLon, EvalLat, bp.RadiusM);
            int n = EvalLon * EvalLat;
            string[] names = { "cover", "precip", "level", "temp", "wind", "cell", "pa" };
            var streams = new BinaryWriter[names.Length];
            for (int k = 0; k < names.Length; k++) streams[k] = new BinaryWriter(File.Create(Path.Combine(dir, "month_" + names[k] + ".f32")));
            var ocean = new BinaryWriter(File.Create(Path.Combine(dir, "ocean.f32")));
            for (int j = 0; j < EvalLat; j++) for (int i = 0; i < EvalLon; i++) ocean.Write((float)g.Grid.OceanAt(mg.LatRad[j], mg.LonRad[i]));
            ocean.Close();
            double ringKm = Math.Max(3.0 * 2.0 * Math.PI * bp.RadiusM / EvalLon / 1000.0, 0.5 * g.D.SystemRadiusM / 1000.0);
            var events = new List<object>(); var times = new List<double>();
            var p = new double[n]; var t = new double[n]; var c = new double[n];
            Weather w;
            for (int f = 0; f < frames; f++)
            {
                double ut = start + f * frameS;
                times.Add(ut);
                for (int j = 0; j < EvalLat; j++)
                    for (int i = 0; i < EvalLon; i++)
                    {
                        g.Sample(mg.LatRad[j], mg.LonRad[i], 0.0, ut, out w);
                        int k = j * EvalLon + i;
                        p[k] = w.PressureAnomPa; t[k] = w.TempK; c[k] = w.CloudWater;
                        streams[0].Write((float)w.Coverage); streams[1].Write((float)w.PrecipMmH); streams[2].Write((float)g.Level01(w.PrecipMmH));
                        streams[3].Write((float)w.TempK); streams[4].Write((float)w.WindSpeed); streams[5].Write((float)w.CellStrength); streams[6].Write((float)w.PressureAnomPa);
                    }
                var mins = new List<object>(); foreach (var m in Detect.Minima(mg, p, bp.SurfacePressurePa, ringKm, true)) mins.Add(new[] { m.Lat * 180.0 / Math.PI, m.Lon * 180.0 / Math.PI });
                var bands = new List<object>();
                foreach (var b in Detect.Bands(mg, t, c, 20 * mg.CellM))
                    bands.Add(new Dictionary<string, object> { { "lat", b.Lat * 180.0 / Math.PI }, { "lon", b.Lon * 180.0 / Math.PI }, { "extent_km", b.ExtentKm }, { "aspect", b.Aspect }, { "arm", b.Arm }, { "axis_deg", b.AxisDeg } });
                events.Add(new Dictionary<string, object> { { "minima", mins }, { "bands", bands } });
            }
            foreach (var s in streams) s.Close();
            var latDeg = new double[EvalLat]; for (int j = 0; j < EvalLat; j++) latDeg[j] = mg.LatDeg[j];
            File.WriteAllText(Path.Combine(dir, "month_meta.json"), Json.Write(new Dictionary<string, object> {
                { "body", bp.Name }, { "seed", seed }, { "nlon", EvalLon }, { "nlat", EvalLat }, { "frames", frames }, { "frame_s", frameS }, { "fields", names }, { "times_s", times }, { "lat_deg", latDeg }, { "radius_km", bp.RadiusM / 1000.0 }, { "rotation_s", bp.RotationPeriodS } }));
            File.WriteAllText(Path.Combine(dir, "month_events.json"), Json.Write(events));
            Console.WriteLine("wrote " + frames + " frames to " + dir);
            return 0;
        }

        // One frame through the front detector with the numbers behind it: frontdebug <body> <ut days>
        public static int FrontDebug(string[] args)
        {
            var bp = ByName(args[1]);
            var g = new WeatherGenerator(bp, 1);
            int n = EvalLon * EvalLat;
            var p = new double[n]; var t = new double[n]; var c = new double[n];
            var mg = new MetricGrid(EvalLon, EvalLat, bp.RadiusM);
            for (int d = 0; d < 6; d++)
            {
                Fields(g, 2.0e6 + double.Parse(args[2]) * 86400.0 + d * 43200.0, EvalLon, EvalLat, p, t, c, null);
                int flagged = 0; double maxg = 0;
                var ta = Detect.ZonalAnomaly(mg, t);
                for (int j = 1; j < EvalLat - 1; j++)
                {
                    if (Math.Abs(mg.LatDeg[j]) < Detect.BeltLo || Math.Abs(mg.LatDeg[j]) > Detect.BeltHi) continue;
                    for (int i = 0; i < EvalLon; i++)
                    {
                        int ie = (i + 1) % EvalLon, iw = (i - 1 + EvalLon) % EvalLon;
                        double tx = (ta[j * EvalLon + ie] - ta[j * EvalLon + iw]) / (2.0 * mg.Dx[j]);
                        double ty = (ta[(j - 1) * EvalLon + i] - ta[(j + 1) * EvalLon + i]) / (mg.A * (mg.LatRad[j - 1] - mg.LatRad[j + 1]));
                        double gt = Math.Sqrt(tx * tx + ty * ty) * 1e5;
                        if (gt >= 3.0) flagged++; if (gt > maxg) maxg = gt;
                    }
                }
                var bands = Detect.Bands(mg, t, c, 20 * mg.CellM);
                Console.WriteLine("frame " + d + ": max |grad T| " + maxg.ToString("F2") + " K/100km, cells with >= 3 K/100km: " + flagged + ", bands " + bands.Count);
                foreach (var b in bands) Console.WriteLine("   band lat " + (b.Lat * 180 / Math.PI).ToString("F1") + " extent " + b.ExtentKm.ToString("F0") + " km aspect " + b.Aspect.ToString("F1") + " cells " + b.Cells + " arm " + b.Arm);
            }
            return 0;
        }

        // A short health print for one body: ranges of the main outputs over a coarse sample of the globe and a month.
        public static int Smoke(string[] args)
        {
            var bp = ByName(args.Length > 1 ? args[1] : "kerbin");
            var g = new WeatherGenerator(bp, 7);
            var d = g.D;
            Console.WriteLine(bp.Name + ": cells " + d.Cells + ", jet count " + d.JetCount.ToString("F2") + ", sub-solar weight " + d.SubSolarWeight.ToString("F2") + ", wave gate " + d.WaveGate.ToString("F2")
                + ", Ld " + (d.DeformationRadiusM / 1000.0).ToString("F0") + " km, system radius " + (d.SystemRadiusM / 1000.0).ToString("F0") + " km, jet " + d.JetSpeed0.ToString("F1") + " m/s at "
                + (d.JetLat0 * 180 / Math.PI).ToString("F0") + " deg, slots " + d.SlotsPerHemisphere + ", surface wind " + d.SurfaceWind.ToString("F1") + ", equator-pole " + d.EquatorPoleK.ToString("F1") + " K");
            int nx = 64, ny = 32;
            double tmin = 1e9, tmax = -1e9, wmax = 0, pmax = 0, cov = 0, pm = 0, wsum = 0, nsamp = 0, prec = 0;
            Weather w;
            for (int day = 0; day < 40; day += 2)
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        double lat = (90.0 - (j + 0.5) * 180.0 / ny) * Math.PI / 180.0, lon = (-180.0 + (i + 0.5) * 360.0 / nx) * Math.PI / 180.0;
                        g.Sample(lat, lon, 0.0, 3.0e6 + day * 86400.0, out w);
                        if (double.IsNaN(w.TempK) || double.IsNaN(w.WindSpeed) || double.IsNaN(w.PrecipMmH)) { Console.WriteLine("NaN at " + lat + " " + lon); return 1; }
                        tmin = Math.Min(tmin, w.TempK); tmax = Math.Max(tmax, w.TempK); wmax = Math.Max(wmax, w.WindSpeed); pmax = Math.Max(pmax, w.PrecipMmH);
                        double cw = Math.Cos(lat); cov += w.Coverage * cw; pm += w.PrecipMmH * cw; wsum += cw; nsamp++;
                        if (w.PrecipMmH > 0.05) prec += cw;
                    }
            Console.WriteLine("T " + tmin.ToString("F0") + ".." + tmax.ToString("F0") + " K, max wind " + wmax.ToString("F1") + " m/s, max precip " + pmax.ToString("F2") + " mm/h, mean coverage "
                + (cov / wsum).ToString("F3") + ", mean precip " + (pm / wsum).ToString("F4") + " mm/h, precipitating share " + (prec / wsum).ToString("F3"));
            return 0;
        }
    }
}
