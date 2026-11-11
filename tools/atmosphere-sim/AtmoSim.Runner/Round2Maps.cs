using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AtmoSim.Core;

namespace AtmoSim.Runner
{
    // Tuning round 2: map frames at a chosen resolution without the detectors (for the sheets of the cloud look), and profile prints of the moist belt.
    public static class Round2Maps
    {
        private static double D(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }

        // frames <body> <seed> <startDays> <stepHours> <count> <dir> [nlon nlat]: cover, precip, level, front, pa, type, wind, temp as float32 files (row 0 north,
        // column 0 at longitude 0), frames_meta.json.
        public static int Frames(string[] args)
        {
            var bp = Study.ByName(args[1]);
            ulong seed = ulong.Parse(args[2], CultureInfo.InvariantCulture);
            double start = D(args[3]) * 86400.0, step = D(args[4]) * 3600.0;
            int count = int.Parse(args[5], CultureInfo.InvariantCulture);
            string dir = args[6];
            int nx = args.Length > 7 ? int.Parse(args[7], CultureInfo.InvariantCulture) : 720, ny = args.Length > 8 ? int.Parse(args[8], CultureInfo.InvariantCulture) : 360;
            Directory.CreateDirectory(dir);
            var g = new WeatherGenerator(bp, seed);
            string[] names = { "cover", "precip", "level", "front", "pa", "type", "wind", "temp" };
            var wr = new BinaryWriter[names.Length];
            for (int k = 0; k < names.Length; k++) wr[k] = new BinaryWriter(File.Create(Path.Combine(dir, "frames_" + names[k] + ".f32")));
            var times = new List<double>();
            var res = new float[names.Length][];
            for (int k = 0; k < names.Length; k++) res[k] = new float[nx * ny];
            var oc = new BinaryWriter(File.Create(Path.Combine(dir, "frames_ocean.f32")));
            for (int j = 0; j < ny; j++) for (int i = 0; i < nx; i++) oc.Write((float)g.Grid.OceanAt((90.0 - (j + 0.5) * 180.0 / ny) * Math.PI / 180.0, 2.0 * Math.PI * (i + 0.5) / nx));
            oc.Close();
            for (int f = 0; f < count; f++)
            {
                double ut = start + f * step;
                times.Add(ut);
                System.Threading.Tasks.Parallel.For(0, ny, j =>
                {
                    Weather wl;
                    double lat = (90.0 - (j + 0.5) * 180.0 / ny) * Math.PI / 180.0;
                    for (int i = 0; i < nx; i++)
                    {
                        g.Sample(lat, 2.0 * Math.PI * (i + 0.5) / nx, 0.0, ut, out wl);
                        int k = j * nx + i;
                        res[0][k] = (float)wl.Coverage; res[1][k] = (float)wl.PrecipMmH; res[2][k] = (float)g.Level01(wl.PrecipMmH); res[3][k] = (float)wl.FrontKPer100Km;
                        res[4][k] = (float)wl.PressureAnomPa; res[5][k] = (float)(int)wl.Type; res[6][k] = (float)wl.WindSpeed; res[7][k] = (float)wl.TempK;
                    }
                });
                for (int q = 0; q < names.Length; q++) for (int k = 0; k < nx * ny; k++) wr[q].Write(res[q][k]);
            }
            foreach (var s in wr) s.Close();
            File.WriteAllText(Path.Combine(dir, "frames_meta.json"), Json.Write(new Dictionary<string, object> {
                { "body", bp.Name }, { "seed", seed }, { "nlon", nx }, { "nlat", ny }, { "frames", count }, { "frame_s", step }, { "fields", names }, { "times_s", times }, { "radius_km", bp.RadiusM / 1000.0 } }));
            Console.WriteLine("wrote " + count + " frames " + nx + "x" + ny + " to " + dir);
            return 0;
        }

        // diag-clim <body> <seed>: air temperature (degrees C, climate part) over the first ocean point found at each latitude, midwinter / equinox / midsummer of the northern hemisphere.
        public static int Clim(string[] args)
        {
            var bp = Study.ByName(args[1]);
            var g = new WeatherGenerator(bp, ulong.Parse(args[2], CultureInfo.InvariantCulture));
            foreach (double latD in new[] { 0.0, 30.0, 40.0, 45.0, 50.0, 55.0, 60.0, 65.0, 70.0, 80.0 })
            {
                double lat = latD * Math.PI / 180.0, bestLon = 0.0; bool found = false;
                for (double lonD = -179.0; lonD < 180.0; lonD += 3.0) if (g.Grid.OceanAt(lat, lonD * Math.PI / 180.0) > 0.95) { bestLon = lonD * Math.PI / 180.0; found = true; break; }
                if (!found) { Console.WriteLine("lat " + latD + ": no ocean"); continue; }
                string s = "lat " + latD.ToString("F0").PadLeft(3) + " ocean:";
                foreach (double ph in new[] { 0.0, 0.25, 0.5, 0.75 }) s += " " + (g.ClimateTemperature(lat, bestLon, (2.0 + ph) * bp.YearS) - 273.15).ToString("F1").PadLeft(6);
                Console.WriteLine(s);
            }
            return 0;
        }

        // diag-ice <body> <seed>: mean ice fraction (share of sea texels with ice >= 0.15) by latitude at several northern phases of the year.
        public static int Ice(string[] args)
        {
            var bp = Study.ByName(args[1]);
            var g = new WeatherGenerator(bp, ulong.Parse(args[2], CultureInfo.InvariantCulture));
            double[] ph = { 0.0, 0.08, 0.16, 0.25, 0.5 };
            Console.WriteLine("lat   sea sites; share with ice at phase 0 0.08 0.16 0.25 0.5 (north)");
            for (double a = 40.0; a <= 85.0; a += 2.5)
            {
                var cnt = new int[ph.Length]; int n = 0;
                for (double lon = -178.0; lon < 180.0; lon += 6.0)
                {
                    double la = a * Math.PI / 180.0, lo = lon * Math.PI / 180.0;
                    if (g.Grid.OceanAt(la, lo) < 0.5) continue;
                    var s = new SurfaceSite(g, la, lo); n++;
                    for (int k = 0; k < ph.Length; k++) if (s.Evaluate((2.0 + ph[k]) * bp.YearS).IceFraction >= 0.15) cnt[k]++;
                }
                string t = "";
                for (int k = 0; k < ph.Length; k++) t += " " + (n > 0 ? (double)cnt[k] / n : double.NaN).ToString("F2");
                Console.WriteLine(a.ToString("F1").PadLeft(5) + " " + n.ToString().PadLeft(3) + t);
            }
            return 0;
        }

        // diag-icesite <body> <seed> <latDeg>: ice thickness, mixed layer temperature and air temperature through one year at the first sea site of a latitude.
        public static int IceSite(string[] args)
        {
            var bp = Study.ByName(args[1]);
            var g = new WeatherGenerator(bp, ulong.Parse(args[2], CultureInfo.InvariantCulture));
            double la = D(args[3]) * Math.PI / 180.0, lo = 0.0; bool f = false;
            for (double lon = -178.0; lon < 180.0; lon += 2.0) { lo = lon * Math.PI / 180.0; if (g.Grid.OceanAt(la, lo) > 0.95) { f = true; break; } }
            if (!f) { Console.WriteLine("no sea"); return 1; }
            var s = new SurfaceSite(g, la, lo);
            for (double ph = 0.0; ph < 1.0; ph += 0.05)
            {
                double ut = (2.0 + ph) * bp.YearS;
                var st = s.Evaluate(ut);
                Console.WriteLine("phase " + ph.ToString("F2") + " ice " + st.IceThicknessM.ToString("F3") + " m, tml " + (st.MixedLayerK - 273.15).ToString("F1") + " C, climate air " + (g.ClimateTemperature(la, lo, ut) - 273.15).ToString("F1") + " C");
            }
            return 0;
        }

        // diag-edge <body>: the criterion's sea-ice edge (north, south) for the seeds 1 to 3 (use ATMO_SET for trial values; --recon for mode R).
        public static int Edge(string[] args)
        {
            string s = "";
            foreach (ulong seed in new ulong[] { 1, 2, 3 })
            {
                var g = new WeatherGenerator(Study.ByName(args[1]), seed);
                s += " seed " + seed + ": " + CriteriaSurface.IceEdge(g, 0).ToString("F1") + " / " + CriteriaSurface.IceEdge(g, 1).ToString("F1") + ";";
                if (Study.ReconDir != null) break;
            }
            Console.WriteLine("edge N / S:" + s);
            return 0;
        }

        // diag-noise <file> <cellRad>: the lattice noise on a 720x360 grid as float32.
        public static int Noise(string[] args)
        {
            double cell = D(args[2]);
            using (var w = new BinaryWriter(File.Create(args[1])))
                for (int j = 0; j < 360; j++)
                    for (int i = 0; i < 720; i++)
                        w.Write((float)WeatherGenerator.LatticeNoise((90.0 - (j + 0.5) * 0.5) * Math.PI / 180.0, (-180.0 + (i + 0.5) * 0.5) * Math.PI / 180.0, cell, 12345UL));
            return 0;
        }

        // diag-alloc: bytes allocated by 20 000 samples after a warm-up (use ATMO_SET to switch levers off).
        public static int Alloc(string[] args)
        {
            var g = new WeatherGenerator(Study.ByName("kerbin"), 1);
            Weather w;
            for (int i = 0; i < 2000; i++) g.Sample(0.8, 0.01 * i, 0.0, 1000.0 + i * 7, out w);
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20000; i++) g.Sample(0.8, 0.0003 * i, 100.0, 1000.0 + i * 7, out w);
            long used = GC.GetAllocatedBytesForCurrentThread() - a0;
            Console.WriteLine("allocated " + used);
            return 0;
        }

        // diag-itcz <body> <seed> <utDays>: coverage, rain and the lift terms along latitude at several longitudes.
        public static int Itcz(string[] args)
        {
            var bp = Study.ByName(args[1]);
            var g = new WeatherGenerator(bp, ulong.Parse(args[2], CultureInfo.InvariantCulture));
            double ut = D(args[3]) * 86400.0;
            Weather w;
            double lo0 = args.Length > 5 ? D(args[4]) : -30.0, hi0 = args.Length > 5 ? D(args[5]) : 30.0, st0 = args.Length > 6 ? D(args[6]) : 2.0;
            foreach (double lonD in new[] { 0.0, 90.0, 180.0, 270.0 })
            {
                Console.WriteLine("lon " + lonD);
                for (double latD = lo0; latD <= hi0; latD += st0)
                {
                    g.Sample(latD * Math.PI / 180.0, lonD * Math.PI / 180.0, 0.0, ut, out w);
                    if (Environment.GetEnvironmentVariable("ATMO_DBGNOISE") == "1") Console.Write(" noise " + g.DebugFairNoise(latD * Math.PI / 180.0, lonD * Math.PI / 180.0, ut).ToString("F3"));
                    Console.WriteLine("  lat " + latD.ToString("F0").PadLeft(4) + " cover " + w.Coverage.ToString("F2") + " cw " + w.CloudWater.ToString("F2") + " hum " + w.Humidity.ToString("F2") + " uplift " + w.Uplift.ToString("F2")
                        + " rain " + w.PrecipMmH.ToString("F2") + " cell " + w.CellStrength.ToString("F2") + " ocean " + w.OceanFraction.ToString("F2"));
                }
            }
            return 0;
        }
    }
}
