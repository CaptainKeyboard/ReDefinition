// Scenes of the weather player for the soft water content of the own clouds (E40 soft density; tools/weather_metrics/e40gs.py measures the output, the inputs
// are e40gn.py's):
//   -scene soft-temporal -in <e40gn inputs>   the layer pass as the game runs it (GrainProbeScene's rig and scenarios) for a list of MEDIA (the density model) on
//                                             the unchanged grid march; a converged reference of every written pose PER MEDIUM
//   -scene soft-cost -in <e40gn inputs>       the GPU milliseconds of the same passes at 1707 x 960 (a timing run)
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    internal static class SoftTable
    {
        internal sealed class Medium
        {
            public string Name;
            // 0: the density as it is (the share of the soft edge); Softness above 0 and EdgePower replace the layer's edge softness and the exponent of the share
            // (the numbers that exist). 1 / 2: the water content (LayerFrame.SoftMode) with its width in noise rank, its exponent, the gain at the base and the rise.
            public int Mode;
            public float Softness, EdgePower = 1f, Width = 0.5f, Power = 2f, BaseGain = 1f, Rise, SigmaScale = 1f;
            public bool SunAtCloud;
            // True: the PRODUCT's own density (E40 soft density, package B1): the job's layer numbers stay as the file gave them, CloudLayerDef's effective numbers, the type table's
            // rise by height, the exponent and the clear-sky rule make the water content (Width is the owner's softness setting). Every other medium runs the old density
            // (layers.softDensity 0) and rewrites the job's numbers itself.
            public bool Product;
        }

        internal static readonly Medium[] Media =
        {
            new Medium { Name = "hard" },
            new Medium { Name = "sig01", SigmaScale = 0.1f },
            new Medium { Name = "e50p2", Softness = 0.5f, EdgePower = 2f },
            new Medium { Name = "e100p3", Softness = 1f, EdgePower = 3f },
            new Medium { Name = "s30k2", Mode = 1, Width = 0.3f, Power = 2f, BaseGain = 0.3f, Rise = 0.5f },
            new Medium { Name = "s50k2", Mode = 1, Width = 0.5f, Power = 2f, BaseGain = 0.3f, Rise = 0.5f },
            new Medium { Name = "s50k3", Mode = 1, Width = 0.5f, Power = 3f, BaseGain = 0.3f, Rise = 0.5f },
            new Medium { Name = "s100k2", Mode = 1, Width = 1f, Power = 2f, BaseGain = 0.3f, Rise = 0.5f },
            new Medium { Name = "s50k2f", Mode = 1, Width = 0.5f, Power = 2f },
            new Medium { Name = "c50k2", Mode = 2, Width = 0.5f, Power = 2f, BaseGain = 0.3f, Rise = 0.5f },
            new Medium { Name = "c100k2", Mode = 2, Width = 1f, Power = 2f, BaseGain = 0.3f, Rise = 0.5f },
            // The same lit by the sun as filtered by the air above a cloud at 2 km (E40-sun-colour-design.md: 0.945 / 0.882 / 0.738 of the white sun 2.35 at the
            // scene's sun elevation) instead of the air above the camera at the ground: a preview of the sun-colour patch on the soft medium.
            new Medium { Name = "c100k2w", Mode = 2, Width = 1f, Power = 2f, BaseGain = 0.3f, Rise = 0.5f, SunAtCloud = true },
            // The product's density with the owner's softness 0.5 and 1.0 (the cfg's per-layer numbers of the job's layer: no softnessScale, the exponent 2, the type's gain 0.3 and
            // rise 0.5, the same as s50k2 / s100k2 above, which they have to reproduce within noise).
            new Medium { Name = "pr50", Mode = 3, Product = true, Width = 0.5f },
            new Medium { Name = "pr100", Mode = 3, Product = true, Width = 1f },
        };

        // The marches every medium is drawn with (GrainTable's: the product's grid march with the golden-ratio offset; 32 and 24 grid points at most).
        // -marches a,b,c replaces the list; seq1m<N> is N grid points at most, tseq1m<N> the same on the span cut to the types present (LayerFrame.SpanTight).
        internal static string[] Marches
        {
            get
            {
                string arg = WeatherPass.Argument("-marches");
                return string.IsNullOrEmpty(arg) ? new[] { "seq1", "seq1m32", "seq1m24" } : arg.Split(',');
            }
        }
        internal static readonly string[] Modes = { "plain", "acc20" };

        // The media of a run: all, or the names of -media a,b,c.
        internal static List<Medium> Selected()
        {
            string arg = WeatherPass.Argument("-media");
            var list = new List<Medium>();
            foreach (Medium m in Media)
                if (string.IsNullOrEmpty(arg) || Array.IndexOf(arg.Split(','), m.Name) >= 0) list.Add(m);
            return list;
        }

        internal static CloudWaterNumbers Numbers(Medium m)
        {
            return new CloudWaterNumbers { Width = m.Width, Power = m.Power, BaseGain = m.BaseGain, RiseShare = m.Rise };
        }

        // The water content at the outline (CloudWater.Closed) of the job's first cloud type.
        internal static float Closed(LayersJob job, Medium m, double detailMean)
        {
            LayersJobLayer jl = job.layers[0];
            LayersJobType jt = jl.types[0];
            CloudWaterNumbers k = Numbers(m);
            double mean = CloudWater.ColumnMean(h =>
            {
                double x = h * (jt.profile.Length - 1);
                int a = Math.Min((int)x, jt.profile.Length - 2);
                return jt.profile[a] + (jt.profile[a + 1] - jt.profile[a]) * (x - a);
            }, k);
            return (float)CloudWater.Closed(jl.sigma * (jt.topShare - jt.baseShare) * (jl.outer - jl.inner) * mean, jt.erosion, detailMean, k);
        }

        // The layer's numbers as the job file gave them (the rig is loaded with a medium's numbers in their place).
        internal struct Original
        {
            public float Softness, Sigma, Coverage, NoiseAmount;
            public float[] Profile;

            internal static Original Of(LayersJobLayer jl)
            {
                return new Original { Softness = jl.softness, Sigma = jl.sigma, Coverage = jl.coverage, NoiseAmount = jl.noiseAmount, Profile = (float[])jl.types[0].profile.Clone() };
            }

            internal void Restore(LayersJobLayer jl)
            {
                jl.softness = Softness;
                jl.sigma = Sigma;
                jl.coverage = Coverage;
                jl.noiseAmount = NoiseAmount;
                jl.types[0].profile = (float[])Profile.Clone();
            }
        }

        // Loads the rig with the medium's layer numbers and returns the water content at the outline (0 for a medium of the old density). The water content
        // reaches the unchanged shader through the layer's own numbers: the edge softness is its width, the noise amount 1, the coverage constant
        // CloudWater.Coverage, the type's profile times the rise by height, the edge hardness off (NoiseSwitches.SoftEdge) and the exponent layers.edgePower.
        internal static float Load(TemporalRig rig, LayersJob job, string input, Medium m, Original o, double detailMean)
        {
            LayersJobLayer jl = job.layers[0];
            o.Restore(jl);
            jl.sigma = o.Sigma * m.SigmaScale;
            float closed = 0f;
            // The tuning key of the old density: every medium but the product's runs on it (the player's tuning table is cleared by each Load, so the key goes into the job).
            LayersJobKey[] savedTuning = job.tuning;
            var keys = new System.Collections.Generic.List<LayersJobKey>(savedTuning ?? new LayersJobKey[0]);
            if (!m.Product) keys.Add(new LayersJobKey { key = CloudSoftDensity.TuneOn, value = 0f });
            job.tuning = keys.ToArray();
            if (m.Product)
            {
                // The product path: nothing of the job's layer is rewritten. The owner's softness and the detail mean reach the layer through CloudSoftDensity (the game: OwnCloudOptions and
                // the noise binding), the edge hardness off through the switch OwnCloudOptions.Apply(NoiseSwitches) sets.
                CloudSoftDensity.Softness = m.Width;
                CloudSoftDensity.DetailMean = detailMean;
                rig.Renderer.Noise.Switches.SoftEdge = true;
                rig.Load(job, input);
                job.tuning = savedTuning;
                return 0f;
            }
            if (m.Mode > 0)
            {
                CloudWaterNumbers k = Numbers(m);
                closed = Closed(job, m, detailMean);
                jl.softness = m.Width;
                jl.noiseAmount = 1f;
                jl.coverage = (float)CloudWater.Coverage(m.Mode == 2, o.Coverage, o.Softness, closed, k);
                float[] p = jl.types[0].profile;
                for (int i = 0; i < p.Length; i++) p[i] *= (float)CloudWater.Rise(i / (double)(p.Length - 1), k);
            }
            else if (m.Softness > 0f) jl.softness = m.Softness;
            rig.Renderer.Noise.Switches.SoftEdge = m.Mode > 0;
            rig.Load(job, input);
            job.tuning = savedTuning;
            return closed;
        }

        internal static double DetailMean(TemporalRig rig)
        {
            NoiseVolumeSet set = rig.Renderer.Noise.ShapeVolumes;
            return set != null ? set.DetailMean : 0.5;
        }

        internal static void Apply(LayerFrame f, Medium m)
        {
            // The product's exponent is the layer's own (the shader multiplies the frame's by it): the frame's stays 1.
            f.EdgePower = m.Product ? 1f : m.Mode > 0 ? m.Power : m.EdgePower;
            // The frame is the rig's fresh one of this medium's Load, so the job's sun colour is there unless it is replaced here.
            if (m.SunAtCloud) f.SunColour = new Color(2.221f, 2.073f, 1.734f);
        }

        // The march of a name on the frame. Beside GrainTable's: "refine" is the product's entry refinement (cloudEntryRefinement: the budget 0.5, the finest step
        // an eighth of the grid step) on the seq1 march, "ign" the seq1 march with the interleaved gradient noise instead of the hash (cloudHashDither off).
        internal static void ApplyMarch(LayerFrame f, string name)
        {
            bool refine = name == "refine", ign = name == "ign";
            GrainTable.Apply(f, March(refine || ign ? "seq1" : name));
            f.HashDither = !ign;
            if (refine)
            {
                f.RefineBudget = 0.5f;
                f.RefineFraction = 0.125f;
            }
        }

        internal static GrainTable.March March(string name)
        {
            bool tight = name.StartsWith("tseq1m");
            if (tight || name.StartsWith("seq1m"))
                return new GrainTable.March { Name = name, Seq = 1, Tight = tight, MaxSteps = int.Parse(name.Substring(tight ? 6 : 5)) };
            return GrainTable.MarchByName(name);
        }
    }

    [WeatherScene("soft-temporal")]
    public sealed class SoftTemporalScene : WeatherSceneBase
    {
        private const int W = 1708, H = 960, Frames = 40;

        private sealed class Scenario
        {
            public string Name;
            public float YawPerFrame, SidePerFrame;
            public int[] Dump;
        }

        // GrainTemporalScene's scenarios without the slow turn (the fast one is the harder case of the same motion).
        private static readonly Scenario[] Scenarios =
        {
            new Scenario { Name = "static", Dump = new[] { 36, 37, 38, 39 } },
            new Scenario { Name = "fly", SidePerFrame = 5f, Dump = new[] { 37, 38 } },
            new Scenario { Name = "turnfast", YawPerFrame = 3f, Dump = new[] { 37, 38 } },
            new Scenario { Name = "flyfast", SidePerFrame = 40f, Dump = new[] { 37, 38 } },
            new Scenario { Name = "cut", Dump = new[] { 0, 2, 5, 10 } },
        };

        private Texture2D readback;

        private void Dump(RenderTexture rt, string name)
        {
            if (readback == null || readback.width != rt.width || readback.height != rt.height)
                readback = new Texture2D(rt.width, rt.height, TextureFormat.RGBAHalf, false, true);
            RenderTexture.active = rt;
            readback.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            RenderTexture.active = null;
            byte[] raw = readback.GetRawTextureData();
            string header = "{'descr': '<f2', 'fortran_order': False, 'shape': (" + rt.height + ", " + rt.width + ", 4), }";
            int total = 10 + header.Length + 1;
            header = header + new string(' ', (64 - total % 64) % 64) + "\n";
            using (var stream = new FileStream(Path.Combine(Output, name + ".npy"), FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new byte[] { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0 });
                writer.Write((ushort)header.Length);
                writer.Write(Encoding.ASCII.GetBytes(header));
                writer.Write(raw);
            }
        }

        private void Render(TemporalRig rig, LayerReconstructor recon, ReconSettings s, int number)
        {
            var cb = new CommandBuffer { name = "soft temporal" };
            recon.Record(cb, rig.Renderer, rig.Frame, Cam, s, number, null, null, null);
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();
        }

        private static float Yaw(Scenario sc, int i)
        {
            return sc.YawPerFrame * Mathf.Max(i - 6, 0);
        }

        private static float Side(Scenario sc, int i)
        {
            return sc.SidePerFrame * Mathf.Max(i - 6, 0);
        }

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            var rig = new TemporalRig();
            bool ready = input != null && File.Exists(Path.Combine(input, "jobs.json")) && rig.Init();
            Sink.Flag("soft_temporal_available", ready);
            if (!ready) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR);
            rt.Create();
            Cam.targetTexture = rt;
            var refRecon = new LayerReconstructor();
            refRecon.Init(Shader.Find(LayerReconstructor.ShaderName));
            var full = new ReconSettings { Mode = ReconMode.Full };
            int dumped = 0;
            foreach (string view in new[] { "ground", "above" })
            {
                LayersJob job = GrainTable.Find(file, "gn-" + view + "-prod-f0");
                if (job == null) { Sink.Flag("soft_temporal_job_" + view, false); continue; }
                SoftTable.Original original = SoftTable.Original.Of(job.layers[0]);
                rig.Load(job, input);
                double detailMean = SoftTable.DetailMean(rig);
                foreach (SoftTable.Medium medium in SoftTable.Selected())
                {
                    float closed = SoftTable.Load(rig, job, input, medium, original, detailMean);
                    Sink.Info("soft.closed." + view + "." + medium.Name, closed);
                    foreach (Scenario sc in Scenarios)
                    {
                        // The converged reference of every pose that is written, of THIS medium.
                        GrainTable.ApplyConverged(rig.Frame);
                        SoftTable.Apply(rig.Frame, medium);
                        foreach (int i in sc.Dump)
                        {
                            if ((sc.Name == "static" || sc.Name == "cut") && i != sc.Dump[0]) continue;
                            GrainTable.Pose(Cam, job, Yaw(sc, i), Side(sc, i));
                            refRecon.Reset();
                            Render(rig, refRecon, full, 0);
                            Dump(refRecon.Output, "gs-" + view + "-" + medium.Name + "-" + sc.Name + "-ref-f" + i);
                            yield return null;
                        }
                        foreach (string march in SoftTable.Marches)
                            foreach (string mode in SoftTable.Modes)
                            {
                                // The fewer grid points are measured with the history and a still camera only.
                                if (march != "seq1" && (mode == "plain" || sc.Name != "static")) continue;
                                SoftTable.ApplyMarch(rig.Frame, march);
                                SoftTable.Apply(rig.Frame, medium);
                                ReconSettings settings = GrainTable.ModeByName(mode).Settings();
                                rig.Recon.Reset();
                                for (int i = 0; i < Frames; i++)
                                {
                                    GrainTable.Pose(Cam, job, Yaw(sc, i), Side(sc, i));
                                    Render(rig, rig.Recon, settings, 100 + i);
                                    if (Array.IndexOf(sc.Dump, i) >= 0)
                                    {
                                        Dump(rig.Recon.Output, "gs-" + view + "-" + medium.Name + "-" + sc.Name + "-" + march + "-" + mode + "-f" + i);
                                        dumped++;
                                    }
                                    if (i % 8 == 7) yield return null;
                                }
                            }
                    }
                }
                original.Restore(job.layers[0]);
            }
            refRecon.Dispose();
            rig.Dispose();
            Cam.targetTexture = Target;
            rt.Release();
            Sink.Info("soft_temporal_frames", dumped);
            Sink.Flag("soft_temporal_completed", true);
        }
    }

    [WeatherScene("soft-cost")]
    public sealed class SoftCostScene : WeatherSceneBase
    {
        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            var rig = new TemporalRig();
            bool ready = input != null && File.Exists(Path.Combine(input, "jobs.json")) && WeatherGpuTimer.Start() && rig.Init();
            Sink.Flag("soft_cost_timers_available", ready);
            if (!ready) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var dummy = new RenderTexture(4, 4, 0, RenderTextureFormat.RFloat);
            dummy.Create();
            RenderTexture.active = dummy;
            GL.Clear(false, true, Color.black);
            RenderTexture.active = null;
            var depthId = new RenderTargetIdentifier(dummy);
            var cb = new CommandBuffer { name = "soft cost" };
            Cam.AddCommandBuffer(CameraEvent.AfterSkybox, cb);
            var segments = new float[8];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Action<CommandBuffer> mStart = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabStart),
                mMarched = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabMarched),
                mResolved = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabComposited),
                mEnd = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.CurtainEnd);
            var rt = new RenderTexture(1707, 960, 24, RenderTextureFormat.DefaultHDR) { name = "soft cost target" };
            rt.Create();
            Cam.targetTexture = rt;
            ReconSettings settings = GrainTable.ModeByName("acc20").Settings();
            List<SoftTable.Medium> media = SoftTable.Selected();
            // Two rounds in opposite order, so that a drift of the machine over the run falls on every case alike; the row is the median of both.
            var samples = new Dictionary<string, List<double>[]>();
            for (int round = 0; round < 2; round++)
            {
                foreach (string view in new[] { "ground", "above" })
                {
                    LayersJob job = GrainTable.Find(file, "gn-" + view + "-prod-f0");
                    if (job == null) { Sink.Flag("soft_cost_job_" + view, false); continue; }
                    SoftTable.Original original = SoftTable.Original.Of(job.layers[0]);
                    rig.Load(job, input);
                    double detailMean = SoftTable.DetailMean(rig);
                    for (int k = 0; k < media.Count; k++)
                    {
                        SoftTable.Medium medium = media[round == 0 ? k : media.Count - 1 - k];
                        SoftTable.Load(rig, job, input, medium, original, detailMean);
                        rig.Frame.UseSceneDepth = true;
                        GrainTable.Pose(Cam, job, 0f, 0f);
                        foreach (string march in SoftTable.Marches)
                        {
                            if (clock.Elapsed.TotalMinutes > 18.0) { Sink.Flag("soft_cost_completed", false); yield break; }
                            SoftTable.ApplyMarch(rig.Frame, march);
                            SoftTable.Apply(rig.Frame, medium);
                            rig.Recon.Reset();
                            string label = view + "." + medium.Name + "." + march;
                            if (!samples.ContainsKey(label)) samples[label] = new[] { new List<double>(), new List<double>(), new List<double>() };
                            List<double>[] s = samples[label];
                            int guard = 0, n = 0, taken = 0;
                            while (taken < 40 && guard++ < 1200)
                            {
                                cb.Clear();
                                cb.SetGlobalTexture(Shader.PropertyToID("_SceneDepth"), depthId);
                                rig.Recon.Record(cb, rig.Renderer, rig.Frame, Cam, settings, FrameIndex, mStart, mMarched, mResolved);
                                rig.Recon.Composite(cb, rig.Renderer, BuiltinRenderTextureType.CameraTarget, mEnd);
                                yield return Frames(1);
                                if (n++ < 20) continue;
                                float total;
                                int number;
                                if (!WeatherGpuTimer.Read(segments, out total, out number)) continue;
                                float m = segments[WeatherGpuTimer.SlabMarched - WeatherGpuTimer.SlabStart], r = segments[WeatherGpuTimer.SlabComposited - WeatherGpuTimer.SlabStart],
                                    e = segments[WeatherGpuTimer.CurtainEnd - WeatherGpuTimer.SlabStart];
                                if (m >= 0f && r >= 0f && e >= 0f) { s[0].Add(m); s[1].Add(r); s[2].Add(e); taken++; }
                            }
                        }
                    }
                    original.Restore(job.layers[0]);
                }
            }
            foreach (KeyValuePair<string, List<double>[]> kv in samples)
            {
                double tm = Median(kv.Value[0]), tr = Median(kv.Value[1]), tc = Median(kv.Value[2]);
                Sink.Info("softcost.march." + kv.Key, tm);
                Sink.Info("softcost.total." + kv.Key, tm + tr + tc);
            }
            Cam.targetTexture = Target;
            rt.Release();
            Cam.RemoveCommandBuffer(CameraEvent.AfterSkybox, cb);
            rig.Dispose();
            Sink.Flag("soft_cost_completed", true);
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return double.NaN;
            double[] sorted = values.ToArray();
            Array.Sort(sorted);
            return sorted[sorted.Length / 2];
        }
    }
}
