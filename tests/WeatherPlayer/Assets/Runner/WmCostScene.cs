// The weather-map cost prototype (master plan section 26, WM1; tools/weather_metrics/wm1.py prepares the jobs and reads the results):
//   -scene wm-cost -in <folder> [-rounds N] [-warm N] [-take N]   the GPU milliseconds of the layer pass for every job whose id starts with "wm-", light on and off
//   -scene wm-picture -in <folder> [-frames N]                     the cloud output of every "wm-" job after N held frames, as <id>-cloud.npy (float32 rgba, rows bottom first)
// A job is the game's pinned capture (every frame, reconstruction and noise setting of pass.json, as the pin replay loads it) with the layer maps of one weather situation and one
// camera: position / forward / up relative to the planet's centre (the capture's world axes), the scene depth of depthFile or the sky where there is none. Light off is the pass's
// cost bit 0 (LayerFrame.CostDebug: no sun march), set on the job's own value. Each round runs every job and both lights in turn; the order alternates from round to round
// (and light on / off alternates with it), so a drift of the machine falls on all cases alike.
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
    internal static class WmJobs
    {
        // The frame number is the scene's own; everything else the capture's.
        private static readonly string[] Skip = { "Frame.FrameNumber" };

        internal static List<LayersJob> Read(string input)
        {
            var list = new List<LayersJob>();
            if (input == null || !File.Exists(Path.Combine(input, "jobs.json"))) return list;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            foreach (LayersJob job in file.jobs)
                if (job.id != null && job.id.StartsWith("wm-", StringComparison.Ordinal) && job.settings != null && job.settings.Length > 0) list.Add(job);
            return list;
        }

        private static List<LayerPassSetting> Settings(LayersJob job)
        {
            var list = new List<LayerPassSetting>();
            foreach (LayersJobSetting s in job.settings) list.Add(new LayerPassSetting { path = s.path, values = s.values });
            return list;
        }

        // The pin replay's load (PinReplayScene): the stack, the settings, the noise volumes, then the stack and the settings once more on the volumes' detail mean. Returns the
        // reconstruction settings; problems lists what could not be set.
        internal static ReconSettings Load(TemporalRig rig, LayersJob job, string input, List<string> problems)
        {
            List<LayerPassSetting> settings = Settings(job);
            rig.Load(job, input);
            var recon = new ReconSettings();
            LayerPassSettings.Apply(settings, rig.Frame, recon, rig.Renderer.Noise.Switches, Skip);
            rig.Renderer.Noise.EnsureReady();
            LayerPassSettings.Apply(settings, rig.Frame, recon, rig.Renderer.Noise.Switches, Skip);
            rig.Load(job, input);
            recon = new ReconSettings();
            problems.AddRange(LayerPassSettings.Apply(settings, rig.Frame, recon, rig.Renderer.Noise.Switches, Skip));
            if (!rig.Renderer.Noise.Ready) problems.Add("the noise volumes are not ready after the second load");
            rig.Renderer.Maps.ReachPinned = null;
            return recon;
        }

        // The camera of the job: its position relative to the planet's centre in the capture's world axes, the planet's centre where the settings put it.
        internal static void Pose(Camera cam, TemporalRig rig, LayersJob job)
        {
            cam.fieldOfView = job.fovV;
            cam.transform.position = rig.Frame.PlanetCenter + LayersSetup.V(job.position);
            cam.transform.rotation = Quaternion.LookRotation(LayersSetup.V(job.forward), LayersSetup.V(job.up));
        }

        // The scene depth of the job (metres along the forward axis, 0 for sky) at the job's size; the sky everywhere without a file or where the file has another size.
        internal static Texture2D Depth(LayersJob job, string input, out bool fromFile)
        {
            var depth = new Texture2D(job.width, job.height, TextureFormat.RFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            fromFile = false;
            byte[] bytes = new byte[job.width * job.height * 4];
            if (!string.IsNullOrEmpty(job.depthFile))
            {
                byte[] raw = File.ReadAllBytes(Path.Combine(input, job.depthFile));
                int headerLength = raw[8] | (raw[9] << 8);
                if (raw.Length - 10 - headerLength == bytes.Length)
                {
                    Buffer.BlockCopy(raw, 10 + headerLength, bytes, 0, bytes.Length);
                    fromFile = true;
                }
            }
            depth.LoadRawTextureData(bytes);
            depth.Apply(false);
            return depth;
        }
    }

    [WeatherScene("wm-cost")]
    public sealed class WmCostScene : WeatherSceneBase
    {
        private static int Arg(string name, int fallback, int min)
        {
            int n;
            return int.TryParse(WeatherPass.Argument(name), out n) && n >= min ? n : fallback;
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return double.NaN;
            double[] sorted = values.ToArray();
            Array.Sort(sorted);
            return sorted[sorted.Length / 2];
        }

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            List<LayersJob> jobs = WmJobs.Read(input);
            var rig = new TemporalRig();
            bool ready = jobs.Count > 0 && WeatherGpuTimer.Start() && rig.Init();
            Sink.Flag("wm_cost_ready", ready);
            if (!ready) yield break;
            int rounds = Arg("-rounds", 2, 1), warm = Arg("-warm", 16, 2), take = Arg("-take", 32, 4);
            // -only id,id: these jobs only. -masks 0,14,...: the layer masks each case is timed with (LayerFrame.LayerMask, bit i = layer i marched, 0 all; the
            // leave-one-out masks attribute the cost to the layers); the label gets ".m<mask>" for every mask but 0.
            string only = WeatherPass.Argument("-only");
            if (!string.IsNullOrEmpty(only))
            {
                var keep = new HashSet<string>(only.Split(','));
                jobs = jobs.FindAll(j => keep.Contains(j.id));
            }
            var masks = new List<int>();
            foreach (string m in (WeatherPass.Argument("-masks") ?? "0").Split(','))
            {
                int v;
                if (int.TryParse(m, out v) && v >= 0) masks.Add(v);
            }
            if (masks.Count == 0) masks.Add(0);
            var cb = new CommandBuffer { name = "wm cost" };
            Cam.AddCommandBuffer(CameraEvent.AfterSkybox, cb);
            var segments = new float[8];
            Action<CommandBuffer> mStart = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabStart),
                mMarched = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabMarched),
                mResolved = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabComposited),
                mEnd = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.PraecipitatioEnd);
            var march = new Dictionary<string, List<double>>();
            var total = new Dictionary<string, List<double>>();
            var perRound = new Dictionary<string, List<double>>();
            var log = new StringBuilder();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int round = 0; round < rounds; round++)
            {
                bool forward = round % 2 == 0;
                for (int ji = 0; ji < jobs.Count; ji++)
                {
                    LayersJob job = jobs[forward ? ji : jobs.Count - 1 - ji];
                    var problems = new List<string>();
                    double loadStart = clock.Elapsed.TotalSeconds;
                    ReconSettings recon = WmJobs.Load(rig, job, input, problems);
                    if (round == 0)
                        foreach (string p in problems) Debug.Log("[WeatherPlayer] wm cost " + job.id + ": not set: " + p);
                    Sink.Flag("wm_settings." + job.id, problems.Count == 0);
                    var rt = new RenderTexture(job.width, job.height, 24, RenderTextureFormat.DefaultHDR) { name = "wm cost target" };
                    rt.Create();
                    Cam.targetTexture = rt;
                    bool fromFile;
                    Texture2D depth = WmJobs.Depth(job, input, out fromFile);
                    WmJobs.Pose(Cam, rig, job);
                    int baseCost = rig.Frame.CostDebug, baseMask = rig.Frame.LayerMask;
                    double loadSeconds = clock.Elapsed.TotalSeconds - loadStart;
                    for (int ci = 0; ci < 2 * masks.Count; ci++)
                    {
                        int li = ci % 2, mi = forward ? ci / 2 : masks.Count - 1 - ci / 2;
                        bool lightOn = (li == 0) == forward;
                        rig.Frame.CostDebug = lightOn ? baseCost & ~1 : baseCost | 1;
                        rig.Frame.LayerMask = masks[mi] == 0 ? baseMask : masks[mi];
                        string label = job.id + "." + (lightOn ? "lit" : "nolight") + (masks[mi] == 0 ? "" : ".m" + masks[mi]);
                        if (!march.ContainsKey(label)) { march[label] = new List<double>(); total[label] = new List<double>(); perRound[label] = new List<double>(); }
                        rig.Recon.Reset();
                        var mine = new List<double>();
                        int n = 0, taken = 0, guard = 0;
                        double caseStart = clock.Elapsed.TotalSeconds;
                        while (taken < take && guard++ < 40 * take)
                        {
                            cb.Clear();
                            cb.SetGlobalTexture(Shader.PropertyToID("_SceneDepth"), depth);
                            rig.Recon.Record(cb, rig.Renderer, rig.Frame, Cam, recon, FrameIndex, mStart, mMarched, mResolved);
                            rig.Recon.Composite(cb, rig.Renderer, BuiltinRenderTextureType.CameraTarget, mEnd);
                            yield return Frames(1);
                            if (n++ < warm) continue;
                            float sum;
                            int number;
                            if (!WeatherGpuTimer.Read(segments, out sum, out number)) continue;
                            float m = segments[WeatherGpuTimer.SlabMarched - WeatherGpuTimer.SlabStart], r = segments[WeatherGpuTimer.SlabComposited - WeatherGpuTimer.SlabStart],
                                e = segments[WeatherGpuTimer.PraecipitatioEnd - WeatherGpuTimer.SlabStart];
                            if (m < 0f || r < 0f || e < 0f) continue;
                            march[label].Add(m);
                            total[label].Add(m + r + e);
                            mine.Add(m);
                            taken++;
                        }
                        perRound[label].Add(Median(mine));
                        log.Append("round ").Append(round).Append(' ').Append(label).Append(" load ").Append(loadSeconds.ToString("0.00")).Append(" s, case ")
                           .Append((clock.Elapsed.TotalSeconds - caseStart).ToString("0.00")).Append(" s, samples ").Append(taken).Append(", march median ").Append(Median(mine).ToString("0.000"))
                           .Append(" ms, depth ").Append(fromFile ? "file" : "sky").Append(", at ").Append(clock.Elapsed.TotalSeconds.ToString("0.0")).Append(" s\n");
                    }
                    rig.Frame.CostDebug = baseCost;
                    rig.Frame.LayerMask = baseMask;
                    Cam.targetTexture = Target;
                    rt.Release();
                    UnityEngine.Object.Destroy(depth);
                }
            }
            foreach (KeyValuePair<string, List<double>> kv in march)
            {
                Sink.Info("wmcost.march." + kv.Key, Median(kv.Value));
                Sink.Info("wmcost.total." + kv.Key, Median(total[kv.Key]));
                Sink.Info("wmcost.n." + kv.Key, kv.Value.Count);
                List<double> rs = perRound[kv.Key];
                for (int k = 0; k < rs.Count; k++) Sink.Info("wmcost.marchr" + k + "." + kv.Key, rs[k]);
            }
            File.WriteAllText(Path.Combine(Output, "wm-cost-timeline.txt"), log.ToString());
            Cam.RemoveCommandBuffer(CameraEvent.AfterSkybox, cb);
            rig.Dispose();
            Sink.Flag("wm_cost_completed", true);
        }
    }

    [WeatherScene("wm-picture")]
    public sealed class WmPictureScene : WeatherSceneBase
    {
        private Texture2D readback;

        private void WriteCloud(RenderTexture rt, string path)
        {
            if (readback == null || readback.width != rt.width || readback.height != rt.height)
                readback = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
            RenderTexture.active = rt;
            readback.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            RenderTexture.active = null;
            byte[] raw = readback.GetRawTextureData();
            var values = new float[raw.Length / 4];
            Buffer.BlockCopy(raw, 0, values, 0, raw.Length);
            PinFiles.WriteFloat32(path, rt.height, rt.width, 4, values);
        }

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            List<LayersJob> jobs = WmJobs.Read(input);
            var rig = new TemporalRig();
            bool ready = jobs.Count > 0 && rig.Init();
            Sink.Flag("wm_picture_ready", ready);
            if (!ready) yield break;
            int frames;
            if (!int.TryParse(WeatherPass.Argument("-frames"), out frames) || frames < 1) frames = 48;
            int written = 0;
            foreach (LayersJob job in jobs)
            {
                var problems = new List<string>();
                ReconSettings recon = WmJobs.Load(rig, job, input, problems);
                foreach (string p in problems) Debug.Log("[WeatherPlayer] wm picture " + job.id + ": not set: " + p);
                var rt = new RenderTexture(job.width, job.height, 24, RenderTextureFormat.DefaultHDR) { name = "wm picture target" };
                rt.Create();
                Cam.targetTexture = rt;
                bool fromFile;
                Texture2D depth = WmJobs.Depth(job, input, out fromFile);
                WmJobs.Pose(Cam, rig, job);
                rig.Recon.Reset();
                for (int k = 0; k < frames; k++)
                {
                    var cb = new CommandBuffer { name = "wm picture" };
                    cb.SetGlobalTexture(Shader.PropertyToID("_SceneDepth"), depth);
                    rig.Recon.Record(cb, rig.Renderer, rig.Frame, Cam, recon, k, null, null, null);
                    Graphics.ExecuteCommandBuffer(cb);
                    cb.Release();
                    if (k % 8 == 7) yield return null;
                }
                WriteCloud(rig.Recon.Output, Path.Combine(Output, job.id + "-cloud.npy"));
                File.WriteAllText(Path.Combine(Output, job.id + "-uniforms.json"), LayerPassReadout.Uniforms(rig.Renderer.Material));
                Sink.Flag("wm_picture_depth." + job.id, fromFile || string.IsNullOrEmpty(job.depthFile));
                Cam.targetTexture = Target;
                rt.Release();
                UnityEngine.Object.Destroy(depth);
                written++;
                yield return null;
            }
            rig.Dispose();
            Sink.Info("wm_pictures", written);
            Sink.Flag("wm_picture_completed", written == jobs.Count);
        }
    }
}
