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
                for (int rep = 0; rep < Repeats && depthOk; rep++)
                {
                    rig.Load(job, input);
                    var recon = new ReconSettings();
                    List<string> problems = LayerPassSettings.Apply(settings, rig.Frame, recon, rig.Renderer.Noise.Switches, Skip);
                    // The noise volumes follow the game's size level and seed; the soft density's detail mean is the volume's, so the settings are set again after it.
                    rig.Renderer.Noise.EnsureReady();
                    problems.AddRange(LayerPassSettings.Apply(settings, rig.Frame, recon, rig.Renderer.Noise.Switches, Skip));
                    Cam.fieldOfView = job.fovV;
                    Cam.transform.position = LayersSetup.V(job.worldCamera);
                    Cam.transform.rotation = new Quaternion(job.worldRotation[0], job.worldRotation[1], job.worldRotation[2], job.worldRotation[3]);
                    rig.Recon.Reset();
                    for (int k = 0; k < job.frames; k++)
                    {
                        var cb = new CommandBuffer { name = "pin replay" };
                        cb.SetGlobalTexture(Shader.PropertyToID("_SceneDepth"), depth);
                        rig.Recon.Record(cb, rig.Renderer, rig.Frame, Cam, recon, job.firstFrame + k, null, null, null);
                        Graphics.ExecuteCommandBuffer(cb);
                        cb.Release();
                        if (k % 8 == 7) yield return null;
                    }
                    string stem = job.id + "-r" + rep;
                    WriteCloud(rig.Recon.Output, Path.Combine(Output, stem + "-cloud.npy"));
                    File.WriteAllText(Path.Combine(Output, stem + "-uniforms.json"), LayerPassReadout.Uniforms(rig.Renderer.Material));
                    var report = new StringBuilder();
                    report.Append("{\"typeTableHash\": ").Append(LayerPassReadout.Str(rig.Renderer.TypeTableHash())).Append(", \"firstFrame\": ").Append(job.firstFrame)
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
