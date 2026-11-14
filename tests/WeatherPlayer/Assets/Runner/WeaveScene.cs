// The scene of the static dither pattern (E40 L2 E1; tools/weather_metrics/e40wv.py measures the output, the inputs are e40gc.py's):
//   -scene weave -in <e40gc inputs> [-views a,b] [-media pr50] [-marches case,case] [-frames N]
// A still camera, the pass as the game runs it (SoftTable's cases: a march name and modifiers), N frames (default 120, so that even a history weight of 0.05 has settled).
// Written per view, medium and case: the last three frames (wv-<view>-<medium>-<case>-f0..f2, the three-frame strips) and the MEAN of the last eight frames
// (wv-...-mean8): the part of the residual that is the same in every frame is what stays in the mean. The converged reference of the pose: wv-<view>-<medium>-ref.
// Frames are raw float32 (n, 4) arrays of the full target.
using System;
using System.Collections;
using System.IO;
using System.Text;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    [WeatherScene("weave")]
    public sealed class WeaveScene : WeatherSceneBase
    {
        private const int W = 1708, H = 960, MeanOf = 8;

        private Texture2D readback;

        private float[] Read(RenderTexture rt)
        {
            if (readback == null || readback.width != rt.width || readback.height != rt.height)
                readback = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
            RenderTexture.active = rt;
            readback.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            RenderTexture.active = null;
            return readback.GetRawTextureData<float>().ToArray();
        }

        private void Write(float[] data, int width, int height, string name)
        {
            string header = "{'descr': '<f4', 'fortran_order': False, 'shape': (" + height + ", " + width + ", 4), }";
            int total = 10 + header.Length + 1;
            header = header + new string(' ', (64 - total % 64) % 64) + "\n";
            var bytes = new byte[data.Length * 4];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            using (var stream = new FileStream(Path.Combine(Output, name + ".npy"), FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new byte[] { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0 });
                writer.Write((ushort)header.Length);
                writer.Write(Encoding.ASCII.GetBytes(header));
                writer.Write(bytes);
            }
        }

        private void Render(TemporalRig rig, LayerReconstructor recon, ReconSettings s, int number)
        {
            var cb = new CommandBuffer { name = "weave" };
            recon.Record(cb, rig.Renderer, rig.Frame, Cam, s, number, null, null, null);
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();
        }

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            var rig = new TemporalRig();
            bool ready = input != null && File.Exists(Path.Combine(input, "jobs.json")) && rig.Init();
            Sink.Flag("weave_available", ready);
            if (!ready) yield break;
            int frames = 120;
            string framesArg = WeatherPass.Argument("-frames");
            if (!string.IsNullOrEmpty(framesArg)) frames = Math.Max(MeanOf + 3, int.Parse(framesArg));
            float yawPerFrame = 0f;
            string yawArg = WeatherPass.Argument("-yaw");
            if (!string.IsNullOrEmpty(yawArg)) yawPerFrame = float.Parse(yawArg, System.Globalization.CultureInfo.InvariantCulture);
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR);
            rt.Create();
            Cam.targetTexture = rt;
            var refRecon = new LayerReconstructor();
            refRecon.Init(Shader.Find(LayerReconstructor.ShaderName));
            var full = new ReconSettings { Mode = ReconMode.Full };
            int dumped = 0;
            foreach (string view in SoftTable.Views)
            {
                LayersJob job = GrainTable.Find(file, "gn-" + view + "-prod-f0");
                if (job == null) { Sink.Flag("weave_job_" + view, false); continue; }
                SoftTable.Original original = SoftTable.Original.Of(job.layers[0]);
                rig.Load(job, input);
                double detailMean = SoftTable.DetailMean(rig);
                foreach (SoftTable.Medium medium in SoftTable.Selected())
                {
                    SoftTable.Load(rig, job, input, medium, original, detailMean);
                    SoftTable.ApplyCase(rig.Frame, SoftTable.Parse("seq1"));
                    GrainTable.ApplyConverged(rig.Frame);
                    GrainTable.Pose(Cam, job, 0f, 0f);
                    refRecon.Reset();
                    Render(rig, refRecon, full, 0);
                    Write(Read(refRecon.Output), W, H, "wv-" + view + "-" + medium.Name + "-ref");
                    yield return null;
                    foreach (string march in SoftTable.Marches)
                    {
                        SoftTable.Case marchCase = SoftTable.Parse(march);
                        SoftTable.ApplyCase(rig.Frame, marchCase);
                        ReconSettings settings = SoftTable.Settings(marchCase, "acc20");
                        rig.Recon.Reset();
                        GrainTable.Pose(Cam, job, 0f, 0f);
                        float[] sum = null;
                        for (int i = 0; i < frames; i++)
                        {
                            // -yaw D: the camera turns D degrees a frame and ends at the reference's pose (the mean of eight frames is then meaningless; the last frames count).
                            if (yawPerFrame != 0f) GrainTable.Pose(Cam, job, yawPerFrame * (i - (frames - 1)), 0f);
                            Render(rig, rig.Recon, settings, 100 + i);
                            int fromEnd = frames - 1 - i;
                            if (fromEnd < MeanOf)
                            {
                                float[] frame = Read(rig.Recon.Output);
                                if (sum == null) sum = new float[frame.Length];
                                for (int k = 0; k < frame.Length; k++) sum[k] += frame[k];
                                if (fromEnd < 3) Write(frame, W, H, "wv-" + view + "-" + medium.Name + "-" + march + "-f" + fromEnd);
                            }
                            if (i % 8 == 7) yield return null;
                        }
                        for (int k = 0; k < sum.Length; k++) sum[k] /= MeanOf;
                        Write(sum, W, H, "wv-" + view + "-" + medium.Name + "-" + march + "-mean8");
                        dumped++;
                    }
                }
                original.Restore(job.layers[0]);
            }
            refRecon.Dispose();
            rig.Dispose();
            Cam.targetTexture = Target;
            rt.Release();
            Sink.Info("weave_cases", dumped);
            Sink.Flag("weave_completed", true);
        }
    }
}
