// Scenes of the weather player for the grain of the own clouds (E40 grain; tools/weather_metrics/e40gn.py makes the inputs and measures the output):
//   -scene grain-temporal -in <e40gn inputs>   the layer pass as the game runs it (the half-size march laid up to the full target, with and without a history; the
//                                              2 x 2 block mode) over runs of frames with a still camera, a turn and a fly-by; writes the full targets of the last
//                                              frames and a converged reference of each of their poses as arrays
//   -scene grain-cost -in <e40gn inputs>       the GPU milliseconds of the same passes at 1707 x 960 through the proxy's timestamp queries (a timing run)
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
    // What both scenes share: the march variants (the frame's numbers) and the modes (how the full target is made).
    internal static class GrainTable
    {
        internal sealed class March
        {
            public string Name;
            public int MaxSteps = 48, CostDebug;
            public float Leap = 1f;
            // E40 grain against TVC: the march searches the altitudes of the types present only (LayerFrame.SpanTight). The step offset is the one fixed sequence
            // of the shader (E40 step (c)); the names with seq1 keep the names of the series from before it.
            public bool Tight;
        }

        internal sealed class Mode
        {
            public string Name;
            public Func<ReconSettings> Settings;
        }

        internal static readonly March[] Marches =
        {
            new March { Name = "prod" },
            new March { Name = "leap4x", MaxSteps = 192, Leap = 4f },
            new March { Name = "steps4x", MaxSteps = 192 },
            // The cost attribution without the light, with the finer grid and the leap without it, and without the noise.
            new March { Name = "prodnl", CostDebug = 1 },
            new March { Name = "leap4xnl", MaxSteps = 192, Leap = 4f, CostDebug = 1 },
            new March { Name = "prodnn", CostDebug = 2 },
            // E40 grain against TVC (series e40-gt-temporal, e40-gt-cost): the span cut to the types present, fewer grid points on the cut span, the leap on it. seq1 is
            // the product's march (the one sequence of the step offset), kept under its name from before E40 step (c).
            new March { Name = "seq1" },
            new March { Name = "tight", Tight = true },
            new March { Name = "tseq1", Tight = true },
            new March { Name = "t32seq1", Tight = true, MaxSteps = 32 },
            new March { Name = "t24seq1", Tight = true, MaxSteps = 24 },
            new March { Name = "t16seq1", Tight = true, MaxSteps = 16 },
            new March { Name = "t12seq1", Tight = true, MaxSteps = 12 },
            new March { Name = "tleap2seq1", Tight = true, MaxSteps = 96, Leap = 2f },
            new March { Name = "leap4xseq1", MaxSteps = 192, Leap = 4f },
        };

        // The cases of the series e40-gt-* (-cases gt): march variant and mode.
        internal static readonly string[][] GtTemporal =
        {
            new[] { "prod", "plain" }, new[] { "prod", "acc20" }, new[] { "seq1", "acc10" },
            new[] { "tight", "plain" }, new[] { "tight", "acc20" }, new[] { "tseq1", "acc10" },
            new[] { "t32seq1", "acc20" }, new[] { "t24seq1", "acc20" }, new[] { "tleap2seq1", "acc20" },
            new[] { "leap4x", "acc20" }, new[] { "leap4xseq1", "acc20" }, new[] { "t16seq1", "acc20" }, new[] { "t12seq1", "acc20" },
        };

        internal static readonly string[][] GtCost =
        {
            new[] { "prod", "plain" }, new[] { "prod", "acc20" }, new[] { "tight", "acc20" }, new[] { "tseq1", "acc20" },
            new[] { "t32seq1", "acc20" }, new[] { "t24seq1", "acc20" }, new[] { "tleap2seq1", "acc20" },
            new[] { "leap4x", "acc20" }, new[] { "t16seq1", "acc20" }, new[] { "t12seq1", "acc20" },
        };

        internal static bool Gt { get { return WeatherPass.Argument("-cases") == "gt"; } }

        // plain is the game's default (level 1: the half-size march, laid up); acc is the same with a history of the half-size target; b2 is level 2.
        internal static readonly Mode[] Modes =
        {
            new Mode { Name = "plain", Settings = () => new ReconSettings { Mode = ReconMode.Low, Resolution = 0.5f } },
            new Mode { Name = "acc20", Settings = () => new ReconSettings { Mode = ReconMode.Low, Resolution = 0.5f, Accumulate = 0.2f } },
            new Mode { Name = "acc10", Settings = () => new ReconSettings { Mode = ReconMode.Low, Resolution = 0.5f, Accumulate = 0.1f } },
            new Mode { Name = "acc30", Settings = () => new ReconSettings { Mode = ReconMode.Low, Resolution = 0.5f, Accumulate = 0.3f } },
            new Mode { Name = "b2", Settings = () => new ReconSettings { Mode = ReconMode.Block, Block = 2 } },
        };

        internal static March MarchByName(string name)
        {
            foreach (March m in Marches) if (m.Name == name) return m;
            return null;
        }

        internal static Mode ModeByName(string name)
        {
            foreach (Mode m in Modes) if (m.Name == name) return m;
            return null;
        }

        // The product's march of the rig (TemporalRig.Load) with the variant's numbers.
        internal static void Apply(LayerFrame f, March m)
        {
            f.MaxSteps = m.MaxSteps;
            f.MinStepM = 25f;
            f.StepGrowth = 0.0002f;
            f.Jitter = 1f;
            f.EarlyOut = 0.01f;
            f.TransmittanceGrowth = 2f;
            f.EmptyLeap = m.Leap;
            f.CostDebug = m.CostDebug;
            f.SpanTight = m.Tight;
        }

        // The converged march of the references (e40gn.py CONVERGED).
        internal static void ApplyConverged(LayerFrame f, bool tight = false)
        {
            f.SpanTight = tight;
            f.MaxSteps = 256;
            f.MinStepM = 4f;
            f.StepGrowth = 0f;
            f.Jitter = 0f;
            f.EarlyOut = 0.001f;
            f.TransmittanceGrowth = 0f;
            f.EmptyLeap = 1f;
            f.CostDebug = 0;
        }

        internal static LayersJob Find(LayersJobFile file, string id)
        {
            foreach (LayersJob j in file.jobs) if (j.id == id) return j;
            return null;
        }

        // The camera of a view turned by a yaw about the local vertical and moved sideways.
        internal static void Pose(Camera cam, LayersJob job, float yawDeg, float sideM)
        {
            Vector3 position = LayersSetup.V(job.position), forward = LayersSetup.V(job.forward), up = LayersSetup.V(job.up), right = LayersSetup.V(job.right);
            cam.fieldOfView = job.fovV;
            cam.transform.position = position + right * sideM;
            cam.transform.rotation = Quaternion.AngleAxis(yawDeg, position.normalized) * Quaternion.LookRotation(forward, up);
        }
    }

    [WeatherScene("grain-temporal")]
    public sealed class GrainTemporalScene : WeatherSceneBase
    {
        private const int W = 1708, H = 960, Frames = 40;

        private sealed class Scenario
        {
            public string Name;
            public float YawPerFrame, SidePerFrame;
            public int[] Dump;
        }

        // The turn is 30 degrees a second at 60 frames a second, the fly-by 300 m/s across the view.
        private static readonly Scenario[] Scenarios =
        {
            new Scenario { Name = "static", Dump = new[] { 36, 37, 38, 39 } },
            new Scenario { Name = "turn", YawPerFrame = 0.5f, Dump = new[] { 37, 38 } },
            new Scenario { Name = "fly", SidePerFrame = 5f, Dump = new[] { 37, 38 } },
            // 180 degrees a second and 2400 m/s across the view: the history has to give way.
            new Scenario { Name = "turnfast", YawPerFrame = 3f, Dump = new[] { 37, 38 } },
            new Scenario { Name = "flyfast", SidePerFrame = 40f, Dump = new[] { 37, 38 } },
            // The first frames after a cut (the history starts empty at frame 0).
            new Scenario { Name = "cut", Dump = new[] { 0, 2, 5, 10 } },
        };

        // The cases: march variant and mode.
        private static readonly string[][] Cases =
        {
            new[] { "prod", "plain" }, new[] { "prod", "acc10" }, new[] { "prod", "acc20" }, new[] { "prod", "acc30" }, new[] { "prod", "b2" },
            new[] { "leap4x", "plain" }, new[] { "leap4x", "acc20" },
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
            var cb = new CommandBuffer { name = "grain temporal" };
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
            Sink.Flag("grain_temporal_available", ready);
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
                if (job == null) { Sink.Flag("grain_temporal_job_" + view, false); continue; }
                rig.Load(job, input);
                foreach (Scenario sc in Scenarios)
                {
                    // The converged reference of every pose that is written (a still camera has one); with -cases gt also the one over the span cut to the
                    // types present (reft: its light taps lie closer, so it is the reference of the marches with the cut span).
                    foreach (bool tight in GrainTable.Gt ? new[] { false, true } : new[] { false })
                    {
                        GrainTable.ApplyConverged(rig.Frame, tight);
                        foreach (int i in sc.Dump)
                        {
                            if ((sc.Name == "static" || sc.Name == "cut") && i != sc.Dump[0]) continue;
                            GrainTable.Pose(Cam, job, Yaw(sc, i), Side(sc, i));
                            refRecon.Reset();
                            Render(rig, refRecon, full, 0);
                            Dump(refRecon.Output, "gt-" + view + "-" + sc.Name + (tight ? "-reft-f" : "-ref-f") + i);
                            yield return null;
                        }
                    }
                    foreach (string[] c in GrainTable.Gt ? GrainTable.GtTemporal : Cases)
                    {
                        GrainTable.Apply(rig.Frame, GrainTable.MarchByName(c[0]));
                        ReconSettings settings = GrainTable.ModeByName(c[1]).Settings();
                        rig.Recon.Reset();
                        for (int i = 0; i < Frames; i++)
                        {
                            GrainTable.Pose(Cam, job, Yaw(sc, i), Side(sc, i));
                            Render(rig, rig.Recon, settings, 100 + i);
                            if (Array.IndexOf(sc.Dump, i) >= 0)
                            {
                                Dump(rig.Recon.Output, "gt-" + view + "-" + sc.Name + "-" + c[0] + "-" + c[1] + "-f" + i);
                                dumped++;
                            }
                            if (i % 8 == 7) yield return null;
                        }
                    }
                }
            }
            refRecon.Dispose();
            rig.Dispose();
            Cam.targetTexture = Target;
            rt.Release();
            Sink.Info("grain_temporal_frames", dumped);
            Sink.Flag("grain_temporal_completed", true);
        }
    }

    [WeatherScene("grain-cost")]
    public sealed class GrainCostScene : WeatherSceneBase
    {
        private static readonly string[][] Cases =
        {
            new[] { "prod", "plain" }, new[] { "leap4x", "plain" }, new[] { "steps4x", "plain" },
            new[] { "prodnl", "plain" }, new[] { "leap4xnl", "plain" },
            new[] { "prod", "acc20" }, new[] { "leap4x", "acc20" }, new[] { "prod", "b2" },
        };

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            var rig = new TemporalRig();
            bool ready = input != null && File.Exists(Path.Combine(input, "jobs.json")) && WeatherGpuTimer.Start() && rig.Init();
            Sink.Flag("grain_cost_timers_available", ready);
            if (!ready) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var dummy = new RenderTexture(4, 4, 0, RenderTextureFormat.RFloat);
            dummy.Create();
            RenderTexture.active = dummy;
            GL.Clear(false, true, Color.black);
            RenderTexture.active = null;
            var depthId = new RenderTargetIdentifier(dummy);
            var cb = new CommandBuffer { name = "grain cost" };
            Cam.AddCommandBuffer(CameraEvent.AfterSkybox, cb);
            var segments = new float[8];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Action<CommandBuffer> mStart = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabStart),
                mMarched = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabMarched),
                mResolved = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabComposited),
                mEnd = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.CurtainEnd);
            var rt = new RenderTexture(1707, 960, 24, RenderTextureFormat.DefaultHDR) { name = "grain cost target" };
            rt.Create();
            Cam.targetTexture = rt;
            // Two rounds in opposite order, so that a drift of the machine over the run falls on every case alike; the row is the median of both.
            var samples = new Dictionary<string, List<double>[]>();
            for (int round = 0; round < 2; round++)
            {
                foreach (string view in new[] { "ground", "above" })
                {
                    LayersJob job = GrainTable.Find(file, "gn-" + view + "-prod-f0");
                    if (job == null) { Sink.Flag("grain_cost_job_" + view, false); continue; }
                    rig.Load(job, input);
                    rig.Frame.UseSceneDepth = true;
                    GrainTable.Pose(Cam, job, 0f, 0f);
                    string[][] cases = GrainTable.Gt ? GrainTable.GtCost : Cases;
                    for (int k = 0; k < cases.Length; k++)
                    {
                        string[] c = cases[round == 0 ? k : cases.Length - 1 - k];
                        if (clock.Elapsed.TotalMinutes > 16.0) { Sink.Flag("grain_cost_completed", false); yield break; }
                        GrainTable.Apply(rig.Frame, GrainTable.MarchByName(c[0]));
                        ReconSettings settings = GrainTable.ModeByName(c[1]).Settings();
                        rig.Recon.Reset();
                        string label = view + "." + c[0] + "." + c[1];
                        if (!samples.ContainsKey(label)) samples[label] = new[] { new List<double>(), new List<double>(), new List<double>() };
                        List<double>[] s = samples[label];
                        int guard = 0, n = 0, taken = 0;
                        while (taken < 50 && guard++ < 1500)
                        {
                            cb.Clear();
                            cb.SetGlobalTexture(Shader.PropertyToID("_SceneDepth"), depthId);
                            rig.Recon.Record(cb, rig.Renderer, rig.Frame, Cam, settings, FrameIndex, mStart, mMarched, mResolved);
                            rig.Recon.Composite(cb, rig.Renderer, BuiltinRenderTextureType.CameraTarget, mEnd);
                            yield return Frames(1);
                            if (n++ < 24) continue;
                            float total;
                            int number;
                            if (!WeatherGpuTimer.Read(segments, out total, out number)) continue;
                            float m = segments[WeatherGpuTimer.SlabMarched - WeatherGpuTimer.SlabStart], r = segments[WeatherGpuTimer.SlabComposited - WeatherGpuTimer.SlabStart],
                                e = segments[WeatherGpuTimer.CurtainEnd - WeatherGpuTimer.SlabStart];
                            if (m >= 0f && r >= 0f && e >= 0f) { s[0].Add(m); s[1].Add(r); s[2].Add(e); taken++; }
                        }
                    }
                }
            }
            foreach (KeyValuePair<string, List<double>[]> kv in samples)
            {
                double tm = Median(kv.Value[0]), tr = Median(kv.Value[1]), tc = Median(kv.Value[2]);
                Sink.Info("graincost.march." + kv.Key, tm);
                Sink.Info("graincost.resolve." + kv.Key, tr);
                Sink.Info("graincost.composite." + kv.Key, tc);
                Sink.Info("graincost.total." + kv.Key, tm + tr + tc);
            }
            Cam.targetTexture = Target;
            rt.Release();
            Cam.RemoveCommandBuffer(CameraEvent.AfterSkybox, cb);
            rig.Dispose();
            Sink.Flag("grain_cost_completed", true);
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
