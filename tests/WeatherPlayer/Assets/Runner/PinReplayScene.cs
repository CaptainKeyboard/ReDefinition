// The weather player's replay of the game's pinned capture (master plan phase 0 item 3; the game side is T09ProbePin.cs, the inputs are tools/weather_metrics/e40pp.py --pin-prepare):
//   -scene pin-replay -in <folder> [-repeat N]
// For every job whose id starts with "pin-": the game's layer stack and maps (as the E40 PP replay loads them), then EVERY field of the game's frame, reconstruction and noise
// switches from pass.json (LayerPassSettings.Apply; a field that cannot be set fails the job), the game's camera and planet centre in its world space, the scene depth the game's
// pass read, and the same records from a history cut with the game's frame numbers. The cloud output before the composite is written as <id>-r<k>-cloud.npy (float32,
// rows bottom first, as the game's cloud.npy), the uniforms as <id>-r<k>-uniforms.json, and the type table's hash and the settings that could not be set as <id>-r<k>-replay.json.
// -repeat N replays the job N times from a fresh load (N = 2: the player's own run-to-run difference, tools/weather_metrics/m0parity.py).
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
    [WeatherScene("pin-replay")]
    public sealed class PinReplayScene : WeatherSceneBase
    {
        // The frame number is the replay's own per record (firstFrame + k); everything else the game's.
        private static readonly string[] Skip = { "Frame.FrameNumber" };

        private static int Repeats
        {
            get
            {
                int n;
                return int.TryParse(WeatherPass.Argument("-repeat"), out n) && n >= 1 && n <= 4 ? n : 1;
            }
        }

        private static List<LayerPassSetting> Settings(LayersJob job)
        {
            var list = new List<LayerPassSetting>();
            if (job.settings == null) return list;
            foreach (LayersJobSetting s in job.settings) list.Add(new LayerPassSetting { path = s.path, values = s.values });
            return list;
        }

        // A float32 .npy (C order) as the game's PinFiles writes it: the values after the header.
        private static float[] ReadNpy(string path, out int height, out int width)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int headerLength = bytes[8] | (bytes[9] << 8);
            string header = Encoding.ASCII.GetString(bytes, 10, headerLength);
            int open = header.IndexOf("'shape': (", StringComparison.Ordinal) + 10, close = header.IndexOf(')', open);
            string[] dims = header.Substring(open, close - open).Split(',');
            height = int.Parse(dims[0].Trim());
            width = int.Parse(dims[1].Trim());
            var values = new float[(bytes.Length - 10 - headerLength) / 4];
            Buffer.BlockCopy(bytes, 10 + headerLength, values, 0, values.Length * 4);
            return values;
        }

        private Texture2D readback;
        private RenderTexture shiftedDepth;

        private RenderTexture ShiftedDepth(int width, int height)
        {
            if (shiftedDepth != null && shiftedDepth.width == width && shiftedDepth.height == height) return shiftedDepth;
            if (shiftedDepth != null) { shiftedDepth.Release(); UnityEngine.Object.Destroy(shiftedDepth); }
            shiftedDepth = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
            {
                name = "pin replay depth of the record", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
            };
            shiftedDepth.Create();
            return shiftedDepth;
        }

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

        // The switches of the replay's pacing and its diagnosis (e40pp.py passes them through M0PIN_EXTRA): -pinSync / -pinSleepMs (opt-in, 0 by default: they hide an
        // unexplained nondeterminism, not a fix), -pinYieldEvery (records per player frame, 8), -pinJitter (1: the depth follows the game's jitter), -pinHashFrames (hashes
        // of the march target and the output per record), -pinDiagState (GPU hashes of every texture the march reads and of both materials' uniforms),
        // -pinMarchCompare (later replays' march targets against the first, texel by texel), -pinDumpFrom / -pinDumpTo (the march and output of those records).
        private static int DiagInt(string name, int fallback)
        {
            int n;
            return int.TryParse(WeatherPass.Argument(name), out n) ? n : fallback;
        }

        private Texture2D hashReadback;

        private string Hash(RenderTexture rt)
        {
            if (hashReadback == null || hashReadback.width != rt.width || hashReadback.height != rt.height)
                hashReadback = new Texture2D(rt.width, rt.height, TextureFormat.RGBAHalf, false, true);
            RenderTexture.active = rt;
            hashReadback.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            RenderTexture.active = null;
            return LayerPassSettings.Hex(LayerPassSettings.Fnv1a64(hashReadback.GetRawTextureData()));
        }

        // The march target of every frame of the first replay of a job, to compare later replays texel by texel (diagnosis).
        private readonly List<byte[]> marchFirst = new List<byte[]>();

        private string MarchCompare(RenderTexture rt, int rep, int k)
        {
            if (hashReadback == null || hashReadback.width != rt.width || hashReadback.height != rt.height)
                hashReadback = new Texture2D(rt.width, rt.height, TextureFormat.RGBAHalf, false, true);
            RenderTexture.active = rt;
            hashReadback.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            RenderTexture.active = null;
            byte[] raw = hashReadback.GetRawTextureData();
            if (rep == 0)
            {
                if (k == 0) marchFirst.Clear();
                marchFirst.Add(raw);
                return "";
            }
            byte[] first = marchFirst[k];
            int n = 0;
            var b = new StringBuilder();
            for (int i = 0; i < raw.Length; i += 8)
            {
                bool same = true;
                for (int c = 0; c < 8; c++) if (raw[i + c] != first[i + c]) { same = false; break; }
                if (same) continue;
                n++;
                if (n <= 12)
                {
                    int texel = i / 8;
                    b.Append(" (").Append(texel % rt.width).Append(',').Append(texel / rt.width);
                    for (int c = 0; c < 4; c++)
                        b.Append(' ').Append(Mathf.HalfToFloat(BitConverter.ToUInt16(first, i + 2 * c)).ToString("R")).Append('/')
                         .Append(Mathf.HalfToFloat(BitConverter.ToUInt16(raw, i + 2 * c)).ToString("R"));
                    b.Append(')');
                }
            }
            return n == 0 ? "" : " marchDiff=" + n + b;
        }

        private static readonly string[] DiagTextures =
        {
            "_CovMaps", "_TypeMaps", "_HeightMaps", "_CovReach", "_TypeProfile", "_TypeInfo", "_TypeInfo2", "_NoiseShape", "_NoiseDetail", "_NoiseCurl", "_NoiseFlow",
            "_NoiseColour", "_NoiseLattice", "_CullTiles", "_CullMax0", "_CullMax1", "_CullMin0", "_CullMin1", "_LightCache", "_FarDepth", "_SceneDepth"
        };

        private static readonly string[] DiagReconVectors =
        {
            "_ReconP", "_ReconD", "_ReconK", "_CamPos", "_CamForward", "_CamRight", "_CamUp", "_PrevPos", "_PrevF", "_PrevR", "_PrevU", "_ShellR", "_Drift", "_MvN", "_MvMap",
            "_MvReach", "_CurTex_TexelSize"
        };

        // The GPU contents of a texture (mip 0, every slice), hashed; "-" where none is bound or the read fails.
        private static string GpuHash(Texture t)
        {
            if (t == null) return "-";
            int depth = t is Texture3D ? ((Texture3D)t).depth : t is Texture2DArray ? ((Texture2DArray)t).depth : 1;
            AsyncGPUReadbackRequest req = AsyncGPUReadback.Request(t, 0, 0, t.width, 0, t.height, 0, depth, null);
            req.WaitForCompletion();
            if (req.hasError) return "err";
            ulong h = 0xcbf29ce484222325UL;
            for (int l = 0; l < req.layerCount; l++) h = LayerPassSettings.Fnv1a64(req.GetData<byte>(l).ToArray(), h);
            return LayerPassSettings.Hex(h);
        }

        // Per frame: every texture the march reads (GPU contents), the layer material's uniforms and the reconstruction material's.
        private static string DiagState(TemporalRig rig)
        {
            var b = new StringBuilder();
            Material layers = rig.Renderer.Material;
            foreach (string name in DiagTextures)
            {
                Texture t = layers.HasProperty(name) ? layers.GetTexture(name) : Shader.GetGlobalTexture(name);
                b.Append(' ').Append(name).Append('=').Append(GpuHash(t));
            }
            b.Append(" layersU=").Append(LayerPassSettings.Hex(LayerPassSettings.Fnv1a64(Encoding.UTF8.GetBytes(LayerPassReadout.Uniforms(layers)))));
            var recon = (Material)typeof(LayerReconstructor).GetField("material", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(rig.Recon);
            var r = new StringBuilder();
            foreach (string name in DiagReconVectors) r.Append(recon.GetVector(name).ToString("R")).Append(';');
            foreach (string name in new[] { "_MvA", "_MvB", "_MvD" })
            {
                Vector4[] a = recon.GetVectorArray(name);
                if (a != null) foreach (Vector4 v in a) r.Append(v.ToString("R")).Append(';');
            }
            r.Append(recon.GetMatrix("_MvWorldToMap").ToString("R"));
            foreach (string name in new[] { "_CurTex", "_HistTex", "_MvCov" })
            {
                Texture t = recon.GetTexture(name);
                r.Append(';').Append(name).Append(t != null ? t.GetInstanceID() : 0);
            }
            b.Append(" reconU=").Append(LayerPassSettings.Hex(LayerPassSettings.Fnv1a64(Encoding.UTF8.GetBytes(r.ToString()))));
            b.Append(" mvcov=").Append(GpuHash(recon.GetTexture("_MvCov")));
            return b.ToString();
        }

        // The discriminating experiment of the self-parity hunt (-pinStatic N): ONE fixed record (the capture frame's number, the Full mode: no block, no history, no
        // reconstruction) marched N times back to back, without a sync or idle; each march target is read back asynchronously (ten records per player frame, so the
        // reads in flight stay small) and hashed. Writes <job>-static.txt (record, hash) and, for the first record and up to three that differ from it, the march target.
        private IEnumerator StaticMarch(TemporalRig rig, LayersJob job, string input, List<LayerPassSetting> settings, Texture depth, int count)
        {
            rig.Load(job, input);
            var recon = new ReconSettings();
            LayerPassSettings.Apply(settings, rig.Frame, recon, rig.Renderer.Noise.Switches, Skip);
            rig.Renderer.Noise.EnsureReady();
            LayerPassSettings.Apply(settings, rig.Frame, recon, rig.Renderer.Noise.Switches, Skip);
            rig.Load(job, input);
            recon = new ReconSettings();
            LayerPassSettings.Apply(settings, rig.Frame, recon, rig.Renderer.Noise.Switches, Skip);
            recon.Mode = ReconMode.Full;
            Cam.fieldOfView = job.fovV;
            Cam.transform.position = LayersSetup.V(job.worldCamera);
            Cam.transform.rotation = new Quaternion(job.worldRotation[0], job.worldRotation[1], job.worldRotation[2], job.worldRotation[3]);
            var hashes = new string[count];
            var kept = new Dictionary<int, byte[]>();
            int done = 0, width = 0, height = 0;
            for (int i = 0; i < count; i++)
            {
                rig.Recon.Reset();
                var cb = new CommandBuffer { name = "pin static march" };
                cb.SetGlobalTexture(Shader.PropertyToID("_SceneDepth"), depth);
                rig.Recon.Record(cb, rig.Renderer, rig.Frame, Cam, recon, job.firstFrame + job.frames - 1, null, null, null);
                Graphics.ExecuteCommandBuffer(cb);
                cb.Release();
                int index = i;
                width = rig.Recon.March.width;
                height = rig.Recon.March.height;
                AsyncGPUReadback.Request(rig.Recon.March, 0, r =>
                {
                    if (r.hasError) hashes[index] = "error";
                    else
                    {
                        byte[] raw = r.GetData<byte>().ToArray();
                        hashes[index] = LayerPassSettings.Hex(LayerPassSettings.Fnv1a64(raw));
                        if (index == 0 || (hashes[0] != null && hashes[index] != hashes[0] && kept.Count < 4)) kept[index] = raw;
                    }
                    done++;
                });
                if (i % 10 == 9) yield return null;
            }
            while (done < count) yield return null;
            var text = new StringBuilder();
            int differ = 0;
            for (int i = 0; i < count; i++)
            {
                if (hashes[i] != hashes[0]) differ++;
                text.Append(i).Append(' ').Append(hashes[i]).Append('\n');
            }
            File.WriteAllText(Path.Combine(Output, job.id + "-static.txt"), text.ToString());
            int saved = 0;
            foreach (KeyValuePair<int, byte[]> kv in kept)
            {
                if (kv.Key != 0 && ++saved > 3) break;
                var halves = new float[kv.Value.Length / 2];
                for (int h = 0; h < halves.Length; h++) halves[h] = Mathf.HalfToFloat(BitConverter.ToUInt16(kv.Value, 2 * h));
                PinFiles.WriteFloat32(Path.Combine(Output, job.id + "-static-" + kv.Key + ".npy"), height, width, 4, halves);
            }
            Sink.Info("pin_static_differing." + job.id, differ);
            Debug.Log("[WeatherPlayer] pin static march " + job.id + ": " + count + " records, " + differ + " differ from the first");
        }

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            var rig = new TemporalRig();
            bool ready = input != null && File.Exists(Path.Combine(input, "jobs.json")) && rig.Init();
            Sink.Flag("pin_replay_available", ready);
            if (!ready) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            int replayed = 0;
            foreach (LayersJob job in file.jobs)
            {
                if (job.id == null || !job.id.StartsWith("pin-", StringComparison.Ordinal)) continue;
                List<LayerPassSetting> settings = Settings(job);
                bool complete = settings.Count > 0 && job.worldCamera != null && job.worldCamera.Length == 3 && job.worldRotation != null && job.worldRotation.Length == 4 && job.frames > 0;
                Sink.Flag("pin_job_complete." + job.id, complete);
                if (!complete) continue;
                var target = new RenderTexture(job.width, job.height, 24, RenderTextureFormat.DefaultHDR);
                target.Create();
                Cam.targetTexture = target;
                // The scene depth: the game's, at the camera's size (metres along the forward axis, 0 for sky); without a file the sky everywhere.
                var depth = new Texture2D(job.width, job.height, TextureFormat.RFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                bool depthOk = true;
                if (!string.IsNullOrEmpty(job.depthFile))
                {
                    int dh, dw;
                    float[] d = ReadNpy(Path.Combine(input, job.depthFile), out dh, out dw);
                    depthOk = dh == job.height && dw == job.width && d.Length == dw * dh;
                    if (depthOk)
                    {
                        var bytes = new byte[d.Length * 4];
                        Buffer.BlockCopy(d, 0, bytes, 0, bytes.Length);
                        depth.LoadRawTextureData(bytes);
                    }
                }
                else
                {
                    depth.LoadRawTextureData(new byte[job.width * job.height * 4]);
                }
                depth.Apply(false);
                Sink.Flag("pin_depth." + job.id, depthOk);
                int staticCount = DiagInt("-pinStatic", 0);
                if (staticCount > 0 && depthOk)
                {
                    IEnumerator s = StaticMarch(rig, job, input, settings, depth, staticCount);
                    while (s.MoveNext()) yield return s.Current;
                }
                for (int rep = 0; rep < Repeats && depthOk; rep++)
                {
                    rig.Load(job, input);
                    var recon = new ReconSettings();
                    LayerPassSettings.Apply(settings, rig.Frame, recon, rig.Renderer.Noise.Switches, Skip);
                    // The noise volumes follow the game's size level and seed; a new volume sets the soft density's detail mean, which the stack's uploaded thresholds read
                    // (the first replay of a run drew with the thresholds of the mean before it: _LayerB.y 0.3911 against the game's 0.3929). So: the volumes, the game's
                    // statics again, then the stack loaded and uploaded once more with them, and the settings onto that load's frame.
                    rig.Renderer.Noise.EnsureReady();
                    LayerPassSettings.Apply(settings, rig.Frame, recon, rig.Renderer.Noise.Switches, Skip);
                    rig.Load(job, input);
                    recon = new ReconSettings();
                    List<string> problems = LayerPassSettings.Apply(settings, rig.Frame, recon, rig.Renderer.Noise.Switches, Skip);
                    // A load that had to build the volumes again (the job's noise switches are not the game's) leaves the thresholds stale: named, not hidden.
                    if (!rig.Renderer.Noise.Ready) problems.Add("the noise volumes are not ready after the second load");
                    // Nothing is filled in silently: a layer without the game's height map would get heights.json's built ones (HeightHook), and what the preparation named.
                    foreach (LayersJobLayer jl in job.layers)
                        if (string.IsNullOrEmpty(jl.heightMap)) problems.Add("layer " + jl.name + " has no height map of the game; the heights would be built, not the game's");
                    if (job.unsupported != null) problems.AddRange(job.unsupported);
                    // The type table is built from the tree's cfg (job_layers), the game's from the deployed one: a different hash is a replay of other tables.
                    string tableHash = rig.Renderer.TypeTableHash();
                    if (!string.IsNullOrEmpty(job.typeTableHash) && job.typeTableHash != tableHash)
                        problems.Add("the type table's hash " + tableHash + " is not the game's " + job.typeTableHash);
                    // The game's slew reach (the player's maps hold the shown value in both channels, so it is the uniform only).
                    rig.Renderer.Maps.ReachPinned = job.reachCov != null && job.reachCov.Length == 4 && job.reachType != null && job.reachType.Length == 4
                        ? new[] { new Vector4(job.reachCov[0], job.reachCov[1], job.reachCov[2], job.reachCov[3]), new Vector4(job.reachType[0], job.reachType[1], job.reachType[2], job.reachType[3]) }
                        : null;
                    bool posed = job.recordCamera != null && job.recordCamera.Length == 3 * job.frames && job.recordRotation != null && job.recordRotation.Length == 4 * job.frames
                                 && job.recordPlanetCenter != null && job.recordPlanetCenter.Length == 3 * job.frames;
                    bool jittered = job.hostJitter != null && job.hostJitter.Length == 2 * job.frames && DiagInt("-pinJitter", 1) != 0;
                    // -pinRayJitter (-1 by default: as the capture's Recon.HostJitter; 0 off, 1 on): the A/B of the march's rays following the jitter of the depth.
                    int rayJitterArg = DiagInt("-pinRayJitter", -1);
                    if (rayJitterArg >= 0) recon.HostJitter = rayJitterArg != 0;
                    Cam.fieldOfView = job.fovV;
                    Cam.transform.position = LayersSetup.V(job.worldCamera);
                    Cam.transform.rotation = new Quaternion(job.worldRotation[0], job.worldRotation[1], job.worldRotation[2], job.worldRotation[3]);
                    // A fresh reconstructor for every replay (-freshRecon, a diagnosis of the self-parity residual): no target or pose of the replay before it.
                    if (WeatherPass.Argument("-freshRecon") == "1")
                    {
                        rig.Recon.Dispose();
                        rig.Recon = new LayerReconstructor();
                        rig.Recon.Init(Shader.Find(LayerReconstructor.ShaderName));
                    }
                    rig.Recon.Reset();
                    int yieldEvery = DiagInt("-pinYieldEvery", 8);
                    bool hashFrames = DiagInt("-pinHashFrames", 0) != 0;
                    var hashes = new StringBuilder();
                    for (int k = 0; k < job.frames; k++)
                    {
                        if (posed)
                        {
                            // The game's camera and planet centre of this record (frames.json); the history reprojects between them as the game's did.
                            Cam.transform.position = new Vector3(job.recordCamera[3 * k], job.recordCamera[3 * k + 1], job.recordCamera[3 * k + 2]);
                            Cam.transform.rotation = new Quaternion(job.recordRotation[4 * k], job.recordRotation[4 * k + 1], job.recordRotation[4 * k + 2], job.recordRotation[4 * k + 3]);
                            rig.Frame.PlanetCenter = new Vector3(job.recordPlanetCenter[3 * k], job.recordPlanetCenter[3 * k + 1], job.recordPlanetCenter[3 * k + 2]);
                        }
                        var cb = new CommandBuffer { name = "pin replay" };
                        if (jittered)
                        {
                            // The game rendered record k's depth offset by its jitter j_k; the captured depth is the last record's. Point sampling the capture at
                            // uv - (j_k - j_last) / size gives the depth record k saw (to the nearest pixel: a silhouette crosses a pixel where the game's raster did).
                            int last = job.frames - 1;
                            // -pinJitterSignX / -pinJitterSignY (1 by default; -1 flips that axis): the A/B of the shift's direction.
                            var offset = new Vector2(-DiagInt("-pinJitterSignX", 1) * (job.hostJitter[2 * k] - job.hostJitter[2 * last]) / job.width,
                                                     -DiagInt("-pinJitterSignY", 1) * (job.hostJitter[2 * k + 1] - job.hostJitter[2 * last + 1]) / job.height);
                            Graphics.Blit(depth, ShiftedDepth(job.width, job.height), Vector2.one, offset);
                            cb.SetGlobalTexture(Shader.PropertyToID("_SceneDepth"), shiftedDepth);
                            // The host's jitter of record k as the game published it (_ReDefinition_Jitter z, w: 2 j / size), with the same sign switches as the depth's shift.
                            recon.HostJitterNdc = new Vector2(DiagInt("-pinJitterSignX", 1) * 2f * job.hostJitter[2 * k] / job.width,
                                                              DiagInt("-pinJitterSignY", 1) * 2f * job.hostJitter[2 * k + 1] / job.height);
                        }
                        else
                        {
                            cb.SetGlobalTexture(Shader.PropertyToID("_SceneDepth"), depth);
                            recon.HostJitterNdc = Vector2.zero;
                        }
                        rig.Recon.Record(cb, rig.Renderer, rig.Frame, Cam, recon, job.firstFrame + k, null, null, null);
                        Graphics.ExecuteCommandBuffer(cb);
                        cb.Release();
                        // Opt-in diagnosis, off by default: -pinSync 1 waits for each record on the GPU (a one-texel read of the output), -pinSleepMs N then idles.
                        // Together (1 / 30) they made the repeats bit-identical in the runs of 2026-11-19: that hides an unexplained nondeterminism, it is not a fix.
                        if (DiagInt("-pinSync", 0) != 0) AsyncGPUReadback.Request(rig.Recon.Output, 0, 0, 1, 0, 1, 0, 1, null).WaitForCompletion();
                        if (DiagInt("-pinSleepMs", 0) > 0) System.Threading.Thread.Sleep(DiagInt("-pinSleepMs", 0));
                        if (hashFrames)
                        {
                            hashes.Append(k).Append(' ').Append(Hash(rig.Recon.March)).Append(' ').Append(Hash(rig.Recon.Output));
                            if (DiagInt("-pinDiagState", 0) != 0) hashes.Append(DiagState(rig));
                            if (DiagInt("-pinMarchCompare", 0) != 0) hashes.Append(MarchCompare(rig.Recon.March, rep, k));
                            hashes.Append('\n');
                        }
                        if (DiagInt("-pinDumpFrom", 999) <= k && k <= DiagInt("-pinDumpTo", -1))
                        {
                            WriteCloud(rig.Recon.March, Path.Combine(Output, job.id + "-r" + rep + "-f" + k + "-march.npy"));
                            WriteCloud(rig.Recon.Output, Path.Combine(Output, job.id + "-r" + rep + "-f" + k + "-out.npy"));
                        }
                        if (yieldEvery > 0 && k % yieldEvery == yieldEvery - 1) yield return null;
                    }
                    if (hashFrames) File.WriteAllText(Path.Combine(Output, job.id + "-r" + rep + "-hashes.txt"), hashes.ToString());
                    string stem = job.id + "-r" + rep;
                    WriteCloud(rig.Recon.Output, Path.Combine(Output, stem + "-cloud.npy"));
                    // The depth the replay read, from the texture it bound and in the rows' order of the cloud file (m0parity's orientation check).
                    byte[] depthRaw = depth.GetRawTextureData();
                    var depthValues = new float[depthRaw.Length / 4];
                    Buffer.BlockCopy(depthRaw, 0, depthValues, 0, depthRaw.Length);
                    PinFiles.WriteFloat32(Path.Combine(Output, stem + "-depth.npy"), job.height, job.width, 1, depthValues);
                    File.WriteAllText(Path.Combine(Output, stem + "-uniforms.json"), LayerPassReadout.Uniforms(rig.Renderer.Material));
                    var report = new StringBuilder();
                    report.Append("{\"typeTableHash\": ").Append(LayerPassReadout.Str(tableHash)).Append(", \"jitter\": ").Append(jittered ? "true" : "false").Append(", \"posed\": ").Append(posed ? "true" : "false")
                          .Append(", \"firstFrame\": ").Append(job.firstFrame)
                          .Append(", \"frames\": ").Append(job.frames).Append(", \"blockIndex\": ").Append(rig.Recon.BlockIndex).Append(", \"problems\": [");
                    for (int i = 0; i < problems.Count; i++) report.Append(i > 0 ? ", " : "").Append(LayerPassReadout.Str(problems[i]));
                    report.Append("]}\n");
                    File.WriteAllText(Path.Combine(Output, stem + "-replay.json"), report.ToString());
                    Sink.Flag("pin_settings." + stem, problems.Count == 0);
                    foreach (string p in problems) Debug.Log("[WeatherPlayer] pin replay " + stem + ": not set: " + p);
                    replayed++;
                    yield return null;
                }
                Cam.targetTexture = Target;
                target.Release();
                UnityEngine.Object.Destroy(depth);
            }
            rig.Dispose();
            Sink.Info("pin_replays", replayed);
            Sink.Flag("pin_replay_completed", replayed > 0);
        }
    }
}
