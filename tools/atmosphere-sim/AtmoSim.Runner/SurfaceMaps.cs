using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AtmoSim.Core;

namespace AtmoSim.Runner
{
    // The snow and ice maps of the surface layer for the sheets: surfmap <body> <seed> <yearIndex> <dir>
    // writes snow water equivalent (mm), snow depth (m), sea-ice thickness (m), ice fraction and the wet state of the 256 x 128 evaluation grid at the four season
    // phases (0 northern midwinter, 0.25 spring, 0.5 summer, 0.75 autumn) of the given year, plus surf_meta.json.
    public static class SurfaceMaps
    {
        public static int Run(string[] args)
        {
            var bp = Study.ByName(args[1]);
            ulong seed = ulong.Parse(args[2]);
            int year = int.Parse(args[3]);
            string dir = args[4];
            Directory.CreateDirectory(dir);
            var g = new WeatherGenerator(bp, seed);
            int nx = Study.EvalLon, ny = Study.EvalLat;
            double[] phases = { 0.0, 0.25, 0.5, 0.75 };
            string[] names = { "swe", "depth", "ice_h", "ice_frac", "wet" };
            var data = new float[names.Length][][];
            for (int k = 0; k < names.Length; k++) { data[k] = new float[phases.Length][]; for (int p = 0; p < phases.Length; p++) data[k][p] = new float[nx * ny]; }
            var ocean = new float[nx * ny];
            Parallel.For(0, ny, j =>
            {
                double lat = (90.0 - (j + 0.5) * 180.0 / ny) * Math.PI / 180.0;
                for (int i = 0; i < nx; i++)
                {
                    double lon = -Math.PI + (i + 0.5) * 2.0 * Math.PI / nx;
                    ocean[j * nx + i] = (float)g.Grid.OceanAt(lat, lon);
                    var s = new SurfaceSite(g, lat, lon);
                    for (int p = 0; p < phases.Length; p++)
                    {
                        var st = s.Evaluate((year + phases[p]) * bp.YearS);
                        int idx = j * nx + i;
                        data[0][p][idx] = (float)st.SnowSweMm; data[1][p][idx] = (float)st.SnowDepthM; data[2][p][idx] = (float)st.IceThicknessM;
                        data[3][p][idx] = (float)st.IceFraction; data[4][p][idx] = (float)st.Wet01;
                    }
                }
            });
            for (int k = 0; k < names.Length; k++)
                using (var w = new BinaryWriter(File.Create(Path.Combine(dir, "surf_" + names[k] + ".f32"))))
                    for (int p = 0; p < phases.Length; p++) foreach (float v in data[k][p]) w.Write(v);
            using (var w = new BinaryWriter(File.Create(Path.Combine(dir, "surf_ocean.f32")))) foreach (float v in ocean) w.Write(v);
            File.WriteAllText(Path.Combine(dir, "surf_meta.json"), Json.Write(new Dictionary<string, object> {
                { "body", bp.Name }, { "seed", seed }, { "nlon", nx }, { "nlat", ny }, { "phases", phases }, { "year_index", year }, { "fields", names } }));
            Console.WriteLine("wrote surface maps to " + dir);
            return 0;
        }
    }
}
