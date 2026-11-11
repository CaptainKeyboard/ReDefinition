using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AtmoSim.Core;

// Records the golden values of the weather generator core for the tests of the game's assembly (tests/data/generator):
//   golden.json    the source hash of the core, the 20 000-query hash of the offline proof (positions made with Math.Asin as the proof's Runner
//                  does) and the same with positions made by DMath.Asin (platform independent), a map hash, and the values of three seeds at
//                  20 points and 5 times, and of three surface sites.
//   proof-lat.bin  the 20 000 latitudes of the proof's queries as the proof's runtime made them (Math.Asin differs between runtimes in the
//                  last bits, which changes the hash; the other runtime reads the latitudes from this file).
// Usage: GeneratorGolden <core folder> <output folder>
public static class Program
{
    private static readonly double[] Uts = { 0.0, 1.0e6, 7.5e6, 2.0e7, 3.1e7 };

    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: GeneratorGolden <core folder> <output folder>");
            return 2;
        }
        string coreFolder = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);

        var json = new StringBuilder();
        json.Append("{\n");
        json.Append("  \"sourceHash\": \"" + SourceHash(coreFolder) + "\",\n");

        // The proof's hash (positions by Math.Asin, as Timing.Queries) and the portable one (DMath.Asin).
        var bp = BodyParams.KerbinLike();
        var g = new WeatherGenerator(bp, 1);
        double[] lat = new double[20000], lon = new double[20000], alt = new double[20000], ut = new double[20000];
        double[] latPortable = new double[20000];
        var r = new Rng(77);
        for (int i = 0; i < 20000; i++)
        {
            double u = 2.0 * r.Uniform() - 1.0;
            lat[i] = Math.Asin(u);
            latPortable[i] = DMath.Asin(u);
            lon[i] = (2.0 * r.Uniform() - 1.0) * Math.PI;
            alt[i] = r.Uniform() * 12000.0;
            ut[i] = r.Uniform() * 3.0e7;
        }
        json.Append("  \"proofHash\": \"" + Hashed(g, lat, lon, alt, ut).ToString("X16") + "\",\n");
        json.Append("  \"proofHashPortable\": \"" + Hashed(g, latPortable, lon, alt, ut).ToString("X16") + "\",\n");
        using (var bin = new BinaryWriter(File.Create(Path.Combine(output, "proof-lat.bin"))))
        {
            for (int i = 0; i < lat.Length; i++) bin.Write(BitConverter.DoubleToInt64Bits(lat[i]));
        }

        json.Append("  \"mapHash\": \"" + MapHash(g, 128, 64, 4.0e6).ToString("X16") + "\",\n");

        // Values of three seeds at 20 points and 5 times.
        var pr = new Rng(5);
        var pLat = new double[20]; var pLon = new double[20]; var pAlt = new double[20];
        for (int i = 0; i < 20; i++)
        {
            pLat[i] = DMath.Asin(2.0 * pr.Uniform() - 1.0);
            pLon[i] = (2.0 * pr.Uniform() - 1.0) * Math.PI;
            pAlt[i] = pr.Uniform() * 9000.0;
        }
        for (int seed = 1; seed <= 3; seed++)
        {
            var gs = new WeatherGenerator(BodyParams.KerbinLike(), (ulong)seed);
            var values = new List<string>();
            Weather w;
            foreach (double t in Uts)
            {
                for (int i = 0; i < 20; i++)
                {
                    gs.Sample(pLat[i], pLon[i], pAlt[i], t, out w);
                    foreach (double v in new[] { w.TempK, w.Coverage, w.PrecipMmH, w.WindEast, w.WindNorth, w.CloudWater, w.CellStrength, w.Humidity, (double)(int)w.Type })
                        values.Add("\"" + BitConverter.DoubleToInt64Bits(v).ToString("X16") + "\"");
                }
            }
            json.Append("  \"values_seed" + seed + "\": [" + string.Join(",", values) + "],\n");
        }

        // Three surface sites at three times (the first is on land at high latitude, the second at sea, the third near the equator).
        var sites = new[] { new[] { 1.0, 0.3 }, new[] { -0.9, 2.0 }, new[] { 0.2, -1.0 } };
        var surface = new List<string>();
        foreach (double[] site in sites)
        {
            var s = new SurfaceSite(g, site[0], site[1]);
            foreach (double t in new[] { 3.0e6, 6.0e6, 9.0e6 })
            {
                SurfaceState st = s.Evaluate(t);
                foreach (double v in new[] { st.SnowSweMm, st.IceThicknessM, st.IceFraction, st.Wet01, st.MixedLayerK })
                    surface.Add("\"" + BitConverter.DoubleToInt64Bits(v).ToString("X16") + "\"");
            }
        }
        json.Append("  \"surface\": [" + string.Join(",", surface) + "]\n");
        json.Append("}\n");
        File.WriteAllText(Path.Combine(output, "golden.json"), json.ToString(), new UTF8Encoding(false));
        Console.WriteLine("recorded " + Path.Combine(output, "golden.json"));
        return 0;
    }

    private static ulong Hashed(WeatherGenerator g, double[] lat, double[] lon, double[] alt, double[] ut)
    {
        var hs = new ulong[lat.Length];
        Weather w;
        for (int i = 0; i < lat.Length; i++)
        {
            g.Sample(lat[i], lon[i], alt[i], ut[i], out w);
            hs[i] = WeatherGenerator.HashOf(ref w, (ulong)i);
        }
        ulong h = 0xCBF29CE484222325UL;
        for (int i = 0; i < hs.Length; i++) h = Hash.Mix(h ^ hs[i]);
        return h;
    }

    private static ulong MapHash(WeatherGenerator g, int nx, int ny, double ut)
    {
        ulong h = 0xCBF29CE484222325UL;
        Weather w;
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                g.Sample((90.0 - (j + 0.5) * 180.0 / ny) * Math.PI / 180.0, (-180.0 + (i + 0.5) * 360.0 / nx) * Math.PI / 180.0, 0.0, ut, out w);
                h = WeatherGenerator.HashOf(ref w, h);
            }
        return h;
    }

    // SHA-256 over the relative names and the contents (line ends as LF) of the core's source files in the order of their names.
    private static string SourceHash(string folder)
    {
        var files = new List<string>();
        foreach (string f in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(folder, f).Replace('\\', '/');
            if (rel.StartsWith("obj/") || rel.StartsWith("bin/")) continue;
            files.Add(rel);
        }
        files.Sort(StringComparer.Ordinal);
        var sha = SHA256.Create();
        var all = new StringBuilder();
        foreach (string rel in files) all.Append(rel).Append('\n').Append(File.ReadAllText(Path.Combine(folder, rel)).Replace("\r\n", "\n")).Append('\n');
        return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(all.ToString()))).Replace("-", "");
    }
}
