// The history's test bed on the game's pinned captures (the master plan's section 12.4 work, HIST2; the inputs are tools/weather_metrics/hist2.py prepare, which writes pin jobs as
// tools/weather_metrics/e40pp.py --pin-prepare does):
//   -scene pin-motion -in <folder> -variants <list> [-path...]   a camera path through the game's clouds, every variant along the same path, and a reference per dumped frame
//   -scene pin-cost -in <folder> -variants <list> [-rounds N]      the GPU time of the march and of the reconstruction per variant at the capture's pose (needs the proxy)
// A variant is name:setting;setting;.. where a setting is a path of the replay's settings (LayerPassSettings: Frame.<field>, Recon.<field>, Noise.<field>) = value, applied over the
// game's settings of the capture, e.g. rot3:Recon.Block=3 or far5:Recon.Mode=2;Recon.Rotation=0;Recon.FarRotateM=5000. Variants are separated by commas; a variant without settings is
// the game's own pass. The name ref is the reference: the same pose and noise marched at every pixel without a history (Recon.Mode Full), the mean of -refFrames dither phases.
// The path (all in the camera's own frame at the capture's pose, metres and seconds, at -pathFps frames a second):
//   -pathStart f,r,u   offset of the first frame        -pathVel f,r,u   velocity (m/s)        -pathYaw d   turn about the local up (deg/s, positive to the right)
//   -pathWind k        the layers' wind (the game's per layer) times k, the history told of it (0: the pinned time's frozen pattern)
//   -pathWarm W -pathDump D   W frames before the first dumped one, then D dumped frames      -pathDepth 0   no scene depth (the rays run to the planet), 1 the captured depth (default)
// Dumps: <job>-<variant>-f<k>.npy, the reconstruction's output (half floats, rows bottom first, premultiplied radiance and alpha) at the dumped frames.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // What both scenes share: a pin job loaded as PinReplayScene loads it, the variants, the camera path.
    internal static class PinKit
    {
        internal sealed class Variant
        {
            public string Name;
            public List<LayerPassSetting> Settings = new List<LayerPassSetting>();
            public bool Reference { get { return Name == "ref"; } }
        }

        internal static List<Variant> Variants(string spec)
        {
            var list = new List<Variant>();
            if (string.IsNullOrEmpty(spec)) return list;
            foreach (string item in spec.Split(','))
            {
                if (item.Trim().Length == 0) continue;
                int colon = item.IndexOf(':');
                var v = new Variant { Name = (colon < 0 ? item : item.Substring(0, colon)).Trim() };
                if (colon >= 0)
                    foreach (string s in item.Substring(colon + 1).Split(';'))
                    {
                        int eq = s.IndexOf('=');
                        if (eq <= 0) continue;
                        v.Settings.Add(new LayerPassSetting { path = s.Substring(0, eq).Trim(), values = new[] { s.Substring(eq + 1).Trim() } });
                    }
                list.Add(v);
            }
            return list;
        }

        internal static Vector3 Triple(string name, Vector3 fallback)
        {
            string a = WeatherPass.Argument(name);
            if (string.IsNullOrEmpty(a)) return fallback;
            string[] p = a.Split(',');
            if (p.Length != 3) return fallback;
            return new Vector3(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture), float.Parse(p[2], CultureInfo.InvariantCulture));
        }

        internal static float Number(string name, float fallback)
        {
            float v;
            return float.TryParse(WeatherPass.Argument(name), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        internal static bool Complete(LayersJob job)
        {
            return job.id != null && job.id.StartsWith("pin-", StringComparison.Ordinal) && job.settings != null && job.settings.Length > 0 && job.worldCamera != null
                   && job.worldCamera.Length == 3 && job.worldRotation != null && job.worldRotation.Length == 4 && job.frames > 0;
        }

        // The scene depth the game's pass read (metres along the forward axis, 0 for sky); null where the file does not fit the job. Without a file, or with sky set, the sky everywhere.
        internal static Texture2D Depth(LayersJob job, string input, bool sky)
        {
            var depth = new Texture2D(job.width, job.height, TextureFormat.RFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            if (!sky && !string.IsNullOrEmpty(job.depthFile))
            {
                int dh, dw;
                float[] d = PinReplayScene.ReadNpy(Path.Combine(input, job.depthFile), out dh, out dw);
                if (dh != job.height || dw != job.width || d.Length != dw * dh)
                {
                    UnityEngine.Object.Destroy(depth);
                    return null;
                }
                var bytes = new byte[d.Length * 4];
                Buffer.BlockCopy(d, 0, bytes, 0, bytes.Length);
                depth.LoadRawTextureData(bytes);
            }
            else
            {
                depth.LoadRawTextureData(new byte[job.width * job.height * 4]);
            }
            depth.Apply(false);
            return depth;
        }

        // The job loaded with the game's settings (the order of PinReplayScene: load, noise volumes, settings, load again, settings), then the variant's settings over them.
        // Returns the settings that could not be set.
        internal static List<string> Load(TemporalRig rig, LayersJob job, string input, Variant variant, out ReconSettings recon)
        {
            List<LayerPassSetting> settings = PinReplayScene.Settings(job);
            rig.Load(job, input);
            recon = new ReconSettings();
            LayerPassSettings.Apply(settings, rig.Frame, recon, rig.Renderer.Noise.Switches, PinReplayScene.Skip);
            rig.Renderer.Noise.EnsureReady();
            LayerPassSettings.Apply(settings, rig.Frame, recon, rig.Renderer.Noise.Switches, PinReplayScene.Skip);
            rig.Load(job, input);
            recon = new ReconSettings();
            List<string> problems = LayerPassSettings.Apply(settings, rig.Frame, recon, rig.Renderer.Noise.Switches, PinReplayScene.Skip);
            if (!rig.Renderer.Noise.Ready) problems.Add("the noise volumes are not ready after the second load");
            rig.Renderer.Maps.ReachPinned = job.reachCov != null && job.reachCov.Length == 4 && job.reachType != null && job.reachType.Length == 4
                ? new[] { new Vector4(job.reachCov[0], job.reachCov[1], job.reachCov[2], job.reachCov[3]), new Vector4(job.reachType[0], job.reachType[1], job.reachType[2], job.reachType[3]) }
                : null;
            if (variant != null)
                foreach (string p in LayerPassSettings.Apply(variant.Settings, rig.Frame, recon, rig.Renderer.Noise.Switches, null)) problems.Add("variant " + variant.Name + ": " + p);
            return problems;
        }

        // The capture's own camera (the first record where the job has them, else the job's world camera) and planet centre.
        internal static void BasePose(LayersJob job, LayerFrame frame, out Vector3 position, out Quaternion rotation, out Vector3 planetCenter)
        {
            bool posed = job.recordCamera != null && job.recordCamera.Length == 3 * job.frames && job.recordRotation != null && job.recordRotation.Length == 4 * job.frames
                         && job.recordPlanetCenter != null && job.recordPlanetCenter.Length == 3 * job.frames;
            int k = job.frames - 1;
            if (posed)
            {
                position = new Vector3(job.recordCamera[3 * k], job.recordCamera[3 * k + 1], job.recordCamera[3 * k + 2]);
                rotation = new Quaternion(job.recordRotation[4 * k], job.recordRotation[4 * k + 1], job.recordRotation[4 * k + 2], job.recordRotation[4 * k + 3]);
                planetCenter = new Vector3(job.recordPlanetCenter[3 * k], job.recordPlanetCenter[3 * k + 1], job.recordPlanetCenter[3 * k + 2]);
            }
            else
            {
                position = LayersSetup.V(job.worldCamera);
                rotation = new Quaternion(job.worldRotation[0], job.worldRotation[1], job.worldRotation[2], job.worldRotation[3]);
                planetCenter = frame.PlanetCenter;
            }
        }
    }

    [WeatherScene("pin-motion")]
    public sealed class PinMotionScene : WeatherSceneBase
    {
        private Texture2D halfReadback, floatReadback;

        private void WriteHalf(RenderTexture rt, string path)
        {
            if (halfReadback == null || halfReadback.width != rt.width || halfReadback.height != rt.height)
                halfReadback = new Texture2D(rt.width, rt.height, TextureFormat.RGBAHalf, false, true);
            RenderTexture.active = rt;
            halfReadback.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            RenderTexture.active = null;
            WriteNpyHalf(path, rt.height, rt.width, halfReadback.GetRawTextureData());
        }

        private static void WriteNpyHalf(string path, int height, int width, byte[] raw)
        {
            string header = "{'descr': '<f2', 'fortran_order': False, 'shape': (" + height + ", " + width + ", 4), }";
            int total = 10 + header.Length + 1;
            header = header + new string(' ', (64 - total % 64) % 64) + "\n";
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new byte[] { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0 });
                writer.Write((ushort)header.Length);
                writer.Write(Encoding.ASCII.GetBytes(header));
                writer.Write(raw);
            }
        }

        private void Accumulate(RenderTexture rt, float[] sum)
        {
            if (floatReadback == null || floatReadback.width != rt.width || floatReadback.height != rt.height)
                floatReadback = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
            RenderTexture.active = rt;
            floatReadback.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            RenderTexture.active = null;
            byte[] raw = floatReadback.GetRawTextureData();
            for (int i = 0, b = 0; i < sum.Length; i++, b += 4) sum[i] += BitConverter.ToSingle(raw, b);
        }

        private static byte[] ToHalf(float[] values, float scale)
        {
            var raw = new byte[values.Length * 2];
            for (int i = 0; i < values.Length; i++)
            {
                ushort h = Mathf.FloatToHalf(values[i] * scale);
                raw[2 * i] = (byte)(h & 0xff);
                raw[2 * i + 1] = (byte)(h >> 8);
            }
            return raw;
        }

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            var rig = new TemporalRig();
            bool ready = input != null && File.Exists(Path.Combine(input, "jobs.json")) && rig.Init();
            Sink.Flag("pin_motion_available", ready);
            if (!ready) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            List<PinKit.Variant> variants = PinKit.Variants(WeatherPass.Argument("-variants"));
            string onlyJobs = WeatherPass.Argument("-jobs");
            Vector3 start = PinKit.Triple("-pathStart", Vector3.zero), vel = PinKit.Triple("-pathVel", Vector3.zero);
            float yawRate = PinKit.Number("-pathYaw", 0f), wind = PinKit.Number("-pathWind", 0f), fps = Mathf.Max(1f, PinKit.Number("-pathFps", 50f));
            int warm = Mathf.Max(0, (int)PinKit.Number("-pathWarm", 40f)), dumpCount = Mathf.Max(1, (int)PinKit.Number("-pathDump", 24f));
            int refFrames = Mathf.Clamp((int)PinKit.Number("-refFrames", 16f), 1, 64);
            bool sky = PinKit.Number("-pathDepth", 1f) < 0.5f;
            bool dumpMarch = PinKit.Number("-dumpMarch", 0f) > 0.5f;
            float dt = 1f / fps;
            var refRecon = new LayerReconstructor();
            refRecon.Init(Shader.Find(LayerReconstructor.ShaderName));
            int written = 0;
            foreach (LayersJob job in file.jobs)
            {
                if (!PinKit.Complete(job)) continue;
                if (onlyJobs != null && Array.IndexOf(onlyJobs.Split(','), job.id) < 0) continue;
                var target = new RenderTexture(job.width, job.height, 24, RenderTextureFormat.DefaultHDR);
                target.Create();
                Cam.targetTexture = target;
                Texture2D depth = PinKit.Depth(job, input, sky);
                Sink.Flag("pin_motion_depth." + job.id, depth != null);
                if (depth == null) continue;
                foreach (PinKit.Variant v in variants)
                {
                    ReconSettings recon;
                    List<string> problems = PinKit.Load(rig, job, input, v.Reference ? null : v, out recon);
                    foreach (string p in problems) Debug.Log("[WeatherPlayer] pin motion " + job.id + " " + v.Name + ": not set: " + p);
                    Sink.Flag("pin_motion_settings." + job.id + "." + v.Name, problems.Count == 0);
                    Vector3 basePos, planet;
                    Quaternion baseRot;
                    PinKit.BasePose(job, rig.Frame, out basePos, out baseRot, out planet);
                    rig.Frame.PlanetCenter = planet;
                    Vector3 up = (basePos - planet).normalized;
                    // The wind: the game's per-layer wind times the factor; the reconstruction is told the frame's interval, as the game tells it.
                    LayerNoise noise = rig.Renderer.Noise;
                    if (wind != 0f)
                    {
                        for (int l = 0; l < job.layers.Length; l++)
                        {
                            LayersJobLayer jl = job.layers[l];
                            Vector3 w = jl.wind != null && jl.wind.Length >= 3 ? LayersSetup.V(jl.wind) : Vector3.zero;
                            noise.SetMotion(l, new LayerNoiseMotion { FlowAmplitudeM = jl.flowAmplitude, FlowLooped = jl.flowLooped != 0, ColourStrength = jl.colourStrength, DriftMs = w * wind });
                        }
                        recon.DriftSeconds = dt;
                        recon.DriftPerFrame = noise.DriftOf(0) * dt;
                    }
                    var full = new ReconSettings { Mode = ReconMode.Full, LayerMotion = recon.LayerMotion };
                    rig.Recon.Reset();
                    Cam.fieldOfView = job.fovV;
                    var sum = new float[job.width * job.height * 4];
                    int total = warm + dumpCount;
                    for (int k = 0; k < total; k++)
                    {
                        float t = k * dt;
                        Quaternion rot = Quaternion.AngleAxis(yawRate * t, up) * baseRot;
                        Vector3 offset = start + vel * t;
                        Cam.transform.rotation = rot;
                        Cam.transform.position = basePos + rot * new Vector3(offset.y, offset.z, offset.x);
                        if (wind != 0f && k > 0) noise.Advance(dt);
                        bool dump = k >= warm;
                        var cb = new CommandBuffer { name = "pin motion" };
                        cb.SetGlobalTexture(Shader.PropertyToID("_SceneDepth"), depth);
                        int number = job.firstFrame + k;
                        if (!v.Reference)
                        {
                            rig.Recon.Record(cb, rig.Renderer, rig.Frame, Cam, recon, number, null, null, null);
                            Graphics.ExecuteCommandBuffer(cb);
                            cb.Release();
                            if (dump)
                            {
                                WriteHalf(rig.Recon.Output, Path.Combine(Output, job.id + "-" + v.Name + "-f" + k + ".npy"));
                                // -dumpMarch 1: the march target too (the block samples before the reconstruction), to compare two ways of drawing the march.
                                if (dumpMarch) WriteHalf(rig.Recon.March, Path.Combine(Output, "march~" + job.id + "-" + v.Name + "-f" + k + ".npy"));
                                written++;
                            }
                        }
                        else if (dump)
                        {
                            // The reference: the pose and the noise of this frame marched at every pixel without a history, refFrames dither phases (frame numbers far from the path's).
                            Array.Clear(sum, 0, sum.Length);
                            for (int j = 0; j < refFrames; j++)
                            {
                                var rc = new CommandBuffer { name = "pin motion reference" };
                                rc.SetGlobalTexture(Shader.PropertyToID("_SceneDepth"), depth);
                                refRecon.Reset();
                                refRecon.Record(rc, rig.Renderer, rig.Frame, Cam, full, 100000 + 7 * k + j, null, null, null);
                                Graphics.ExecuteCommandBuffer(rc);
                                rc.Release();
                                Accumulate(refRecon.Output, sum);
                            }
                            WriteNpyHalf(Path.Combine(Output, job.id + "-ref-f" + k + ".npy"), job.height, job.width, ToHalf(sum, 1f / refFrames));
                            written++;
                            cb.Release();
                        }
                        else
                        {
                            cb.Release();
                        }
                        if (k % 4 == 3 || dump) yield return null;
                    }
                }
                Cam.targetTexture = Target;
                target.Release();
                UnityEngine.Object.Destroy(depth);
            }
            refRecon.Dispose();
            rig.Dispose();
            Sink.Info("pin_motion_frames", written);
            Sink.Flag("pin_motion_completed", written > 0);
        }
    }

    [WeatherScene("pin-cost")]
    public sealed class PinCostScene : WeatherSceneBase
    {
        private static double Median(List<double> xs)
        {
            if (xs.Count == 0) return -1.0;
            xs.Sort();
            return xs[xs.Count / 2];
        }

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            var rig = new TemporalRig();
            bool ready = input != null && File.Exists(Path.Combine(input, "jobs.json")) && WeatherGpuTimer.Start() && rig.Init();
            Sink.Flag("pin_cost_available", ready);
            if (!ready) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            List<PinKit.Variant> variants = PinKit.Variants(WeatherPass.Argument("-variants"));
            string onlyJobs = WeatherPass.Argument("-jobs");
            int rounds = Mathf.Clamp((int)PinKit.Number("-rounds", 6f), 1, 12), samples = Mathf.Clamp((int)PinKit.Number("-samples", 30f), 5, 200);
            var cb = new CommandBuffer { name = "pin cost" };
            Cam.AddCommandBuffer(CameraEvent.AfterSkybox, cb);
            var segments = new float[8];
            const int march = WeatherGpuTimer.SlabMarched, recon = WeatherGpuTimer.CurtainEnd;
            Action<CommandBuffer> mStart = c => WeatherGpuTimer.Mark(c, WeatherGpuTimer.SlabStart), mMarch = c => WeatherGpuTimer.Mark(c, march), mEnd = c => WeatherGpuTimer.Mark(c, recon);
            rig.Recon.MarkCull = null;
            rig.Recon.MarkCullEnd = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (LayersJob job in file.jobs)
            {
                if (!PinKit.Complete(job)) continue;
                if (onlyJobs != null && Array.IndexOf(onlyJobs.Split(','), job.id) < 0) continue;
                var rt = new RenderTexture(job.width, job.height, 24, RenderTextureFormat.DefaultHDR) { name = "pin cost target" };
                rt.Create();
                Cam.targetTexture = rt;
                Texture2D depth = PinKit.Depth(job, input, false);
                if (depth == null) continue;
                // Rounds alternate the order of the variants, so that a drift of the machine falls on every variant alike; each round's median is written, the table takes the median of them.
                for (int round = 0; round < rounds; round++)
                {
                    for (int vi = 0; vi < variants.Count; vi++)
                    {
                        PinKit.Variant v = variants[round % 2 == 0 ? vi : variants.Count - 1 - vi];
                        if (clock.Elapsed.TotalMinutes > 40.0) { Sink.Flag("pin_cost_completed", false); yield break; }
                        ReconSettings settings;
                        List<string> problems = PinKit.Load(rig, job, input, v, out settings);
                        foreach (string p in problems) Debug.Log("[WeatherPlayer] pin cost " + job.id + " " + v.Name + ": not set: " + p);
                        Vector3 pos, planet;
                        Quaternion rot;
                        PinKit.BasePose(job, rig.Frame, out pos, out rot, out planet);
                        rig.Frame.PlanetCenter = planet;
                        Cam.fieldOfView = job.fovV;
                        Cam.transform.position = pos;
                        Cam.transform.rotation = rot;
                        rig.Recon.Reset();
                        var cMarch = new List<double>();
                        var cRecon = new List<double>();
                        int guard = 0, n = 0;
                        while (cMarch.Count < samples && guard++ < 1200)
                        {
                            cb.Clear();
                            cb.SetGlobalTexture(Shader.PropertyToID("_SceneDepth"), depth);
                            rig.Recon.Record(cb, rig.Renderer, rig.Frame, Cam, settings, job.firstFrame + n, mStart, mMarch, mEnd);
                            yield return Frames(1);
                            if (n++ < 20) continue;
                            float total;
                            int number;
                            if (!WeatherGpuTimer.Read(segments, out total, out number)) continue;
                            float a = segments[march - WeatherGpuTimer.SlabStart], r = segments[recon - WeatherGpuTimer.SlabStart];
                            if (a >= 0f && r >= 0f) { cMarch.Add(a); cRecon.Add(r); }
                        }
                        string label = job.id + "." + v.Name + ".r" + round;
                        Sink.Info("pincost.march." + label, Median(cMarch));
                        Sink.Info("pincost.recon." + label, Median(cRecon));
                    }
                }
                Cam.targetTexture = Target;
                rt.Release();
                UnityEngine.Object.Destroy(depth);
            }
            Cam.RemoveCommandBuffer(CameraEvent.AfterSkybox, cb);
            rig.Dispose();
            Sink.Flag("pin_cost_completed", true);
        }
    }
}
