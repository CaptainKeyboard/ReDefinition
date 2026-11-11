using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using AtmoSim.Core;

namespace ReDefinition.WeatherPlayer
{
    // The golden values of the weather generator (package C25) under the player's runtime: the same Unity 2019.4 Mono as KSP, no game start.
    // The generator core (tools/atmosphere-sim/AtmoSim.Core) is the repo's source, linked by file name into the player. tests/data/generator
    // holds the values the offline proof recorded; the scene computes them again on the main thread and on a worker thread and compares.
    //
    // Reference expectation (written before the scene was built):
    //   * the proof's hash of 20000 queries (positions made with the platform's Math.Asin, read from proof-lat.bin) equals proofHash;
    //   * the same hash with positions made by DMath.Asin equals proofHashPortable, the 128 x 64 map hash equals mapHash;
    //   * 3 seeds x 20 points x 5 times x 9 values equal values_seed1..3 bit for bit, the surface layer equals "surface";
    //   * a worker thread gives every one of these numbers again, equal to the main thread's.
    // What is measured: the bits of the core's answers under Mono, on this machine's libm for the platform Math.Asin of the first row. The
    // game's own pieces (the PQS raster, the thermal fit) are rows of series K-W3a.
    //
    //   -mode scene -scene generator-golden -golden <tests/data/generator>
    [WeatherScene("generator-golden")]
    public sealed class GeneratorGoldenScene : WeatherSceneBase
    {
        private static readonly double[] Uts = { 0.0, 1.0e6, 7.5e6, 2.0e7, 3.1e7 };

        private sealed class Result
        {
            internal ulong Proof, Portable, Map;
            internal string[] Values = new string[3 * 900];
            internal string[] Surface = new string[45];
            internal double QueryMicroseconds;
            internal Exception Error;
        }

        public override IEnumerator Run()
        {
            string folder = WeatherPass.Argument("-golden");
            if (folder == null || !File.Exists(Path.Combine(folder, "golden.json")))
            {
                Sink.Flag("golden_folder_found", false);
                yield break;
            }
            string json = File.ReadAllText(Path.Combine(folder, "golden.json"));
            byte[] bin = File.ReadAllBytes(Path.Combine(folder, "proof-lat.bin"));
            UnityEngine.Debug.Log("C25 golden under " + Environment.Version + ", " + (Type.GetType("Mono.Runtime") != null ? "Mono" : "not Mono"));
            Sink.Flag("runtime.is_mono", Type.GetType("Mono.Runtime") != null);

            Result main = Compute(bin);
            Result worker = null;
            var thread = new Thread(() => worker = Compute(bin));
            thread.Start();
            thread.Join();

            Judge("main", main, json);
            Judge("worker", worker, json);
            if (main.Error == null && worker != null && worker.Error == null)
            {
                bool same = main.Proof == worker.Proof && main.Portable == worker.Portable && main.Map == worker.Map
                    && string.Join(",", main.Values) == string.Join(",", worker.Values) && string.Join(",", main.Surface) == string.Join(",", worker.Surface);
                Sink.Flag("worker_equals_main", same);
            }
            else Sink.Flag("worker_equals_main", false);
            Sink.Info("main.sample_us", main.QueryMicroseconds);
            if (worker != null) Sink.Info("worker.sample_us", worker.QueryMicroseconds);
            yield return null;
        }

        private void Judge(string who, Result r, string json)
        {
            if (r == null || r.Error != null)
            {
                UnityEngine.Debug.Log("C25 golden " + who + ": " + (r == null ? "no result" : r.Error.ToString()));
                Sink.Flag(who + ".computed", false);
                return;
            }
            UnityEngine.Debug.Log("C25 golden " + who + ": proof " + Hex(r.Proof) + " (golden " + Text(json, "proofHash") + "), portable " + Hex(r.Portable) + " (golden "
                + Text(json, "proofHashPortable") + "), map " + Hex(r.Map) + " (golden " + Text(json, "mapHash") + ")");
            Sink.Flag(who + ".proof_hash_equals_golden", Hex(r.Proof) == Text(json, "proofHash"));
            Sink.Flag(who + ".portable_hash_equals_golden", Hex(r.Portable) == Text(json, "proofHashPortable"));
            Sink.Flag(who + ".map_hash_equals_golden", Hex(r.Map) == Text(json, "mapHash"));
            int wrong = 0;
            for (int seed = 1; seed <= 3; seed++)
            {
                string[] expected = Strings(json, "values_seed" + seed);
                if (expected == null || expected.Length != 900) { wrong += 900; continue; }
                for (int i = 0; i < 900; i++) if (expected[i] != r.Values[(seed - 1) * 900 + i]) wrong++;
            }
            Sink.AtMost(who + ".values_wrong_of_2700", wrong, 0);
            string[] surface = Strings(json, "surface");
            int surfaceWrong = 0;
            if (surface == null || surface.Length != r.Surface.Length) surfaceWrong = r.Surface.Length;
            else for (int i = 0; i < surface.Length; i++) if (surface[i] != r.Surface[i]) surfaceWrong++;
            Sink.AtMost(who + ".surface_wrong_of_45", surfaceWrong, 0);
        }

        private static Result Compute(byte[] bin)
        {
            var result = new Result();
            try
            {
                var lat = new double[20000]; var lon = new double[20000]; var alt = new double[20000]; var ut = new double[20000];
                var latPortable = new double[20000];
                var r = new Rng(77);
                for (int i = 0; i < 20000; i++)
                {
                    lat[i] = Math.Asin(2.0 * r.Uniform() - 1.0);
                    lon[i] = (2.0 * r.Uniform() - 1.0) * Math.PI;
                    alt[i] = r.Uniform() * 12000.0;
                    ut[i] = r.Uniform() * 3.0e7;
                }
                // The portable positions: the same stream with the core's own arcsine.
                var rp = new Rng(77);
                for (int i = 0; i < 20000; i++)
                {
                    latPortable[i] = DMath.Asin(2.0 * rp.Uniform() - 1.0);
                    rp.Uniform(); rp.Uniform(); rp.Uniform();
                }
                var recorded = new double[20000];
                for (int i = 0; i < 20000; i++) recorded[i] = BitConverter.Int64BitsToDouble(BitConverter.ToInt64(bin, 8 * i));

                WeatherGenerator g = new WeatherGenerator(BodyParams.KerbinLike(), 1);
                var watch = Stopwatch.StartNew();
                result.Proof = HashQueries(g, recorded, lon, alt, ut);
                result.QueryMicroseconds = watch.Elapsed.TotalMilliseconds * 1000.0 / 20000.0;
                result.Portable = HashQueries(g, latPortable, lon, alt, ut);

                ulong h = 0xCBF29CE484222325UL;
                Weather w;
                for (int j = 0; j < 64; j++)
                    for (int i = 0; i < 128; i++)
                    {
                        g.Sample((90.0 - (j + 0.5) * 180.0 / 64) * Math.PI / 180.0, (-180.0 + (i + 0.5) * 360.0 / 128) * Math.PI / 180.0, 0.0, 4.0e6, out w);
                        h = WeatherGenerator.HashOf(ref w, h);
                    }
                result.Map = h;

                var pr = new Rng(5);
                var plat = new double[20]; var plon = new double[20]; var palt = new double[20];
                for (int i = 0; i < 20; i++)
                {
                    plat[i] = DMath.Asin(2.0 * pr.Uniform() - 1.0);
                    plon[i] = (2.0 * pr.Uniform() - 1.0) * Math.PI;
                    palt[i] = pr.Uniform() * 9000.0;
                }
                for (int seed = 1; seed <= 3; seed++)
                {
                    var gs = new WeatherGenerator(BodyParams.KerbinLike(), (ulong)seed);
                    int at = (seed - 1) * 900;
                    foreach (double t in Uts)
                        for (int i = 0; i < 20; i++)
                        {
                            gs.Sample(plat[i], plon[i], palt[i], t, out w);
                            double[] values = { w.TempK, w.Coverage, w.PrecipMmH, w.WindEast, w.WindNorth, w.CloudWater, w.CellStrength, w.Humidity, (double)(int)w.Type };
                            foreach (double v in values) result.Values[at++] = Hex((ulong)BitConverter.DoubleToInt64Bits(v));
                        }
                }

                double[][] sites = { new[] { 1.0, 0.3 }, new[] { -0.9, 2.0 }, new[] { 0.2, -1.0 } };
                int sat = 0;
                foreach (double[] site in sites)
                {
                    var s = new SurfaceSite(g, site[0], site[1]);
                    foreach (double t in new[] { 3.0e6, 6.0e6, 9.0e6 })
                    {
                        SurfaceState st = s.Evaluate(t);
                        double[] values = { st.SnowSweMm, st.IceThicknessM, st.IceFraction, st.Wet01, st.MixedLayerK };
                        foreach (double v in values) result.Surface[sat++] = Hex((ulong)BitConverter.DoubleToInt64Bits(v));
                    }
                }
            }
            catch (Exception e)
            {
                result.Error = e;
            }
            return result;
        }

        // The proof's hash (tools/atmosphere-sim Timing.HashQueries): one hash per query, folded in order.
        private static ulong HashQueries(WeatherGenerator g, double[] lat, double[] lon, double[] alt, double[] ut)
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

        private static string Hex(ulong v)
        {
            return v.ToString("X16", CultureInfo.InvariantCulture);
        }

        private static string Text(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\":\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string[] Strings(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\":\\s*\\[([^\\]]*)\\]");
            if (!m.Success) return null;
            var result = new List<string>();
            foreach (Match s in Regex.Matches(m.Groups[1].Value, "\"([^\"]*)\"")) result.Add(s.Groups[1].Value);
            return result.ToArray();
        }
    }
}
