// Scene of the weather player for the look of each cloud type (E40 B3; tools/weather_metrics/e40ty.py writes the jobs and assembles the sheets):
//   -scene type-sheet -in <e40ty inputs>   every job "ty-*": one cloud type of the shipped cfg (the product's soft water content, the type's light response, the layer's light
//                                          factors) from the ground, from above and looking at the sun, converged, with the sun as the product gives it (the white sun, the
//                                          shader's Kerbin air above each sample). The dump is the pass's radiance and alpha (float16 npy); the display stage is the sheet's.
using System.Collections;
using System.IO;
using System.Text;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    [WeatherScene("type-sheet")]
    public sealed class TypeSheetScene : WeatherSceneBase
    {
        private const int W = 1708, H = 960;
        private const float White = 2.35f;

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

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            var rig = new TemporalRig();
            bool ready = input != null && File.Exists(Path.Combine(input, "jobs.json")) && rig.Init();
            Sink.Flag("type_sheet_available", ready);
            if (!ready) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR);
            rt.Create();
            Cam.targetTexture = rt;
            var recon = new LayerReconstructor();
            recon.Init(Shader.Find(LayerReconstructor.ShaderName));
            var full = new ReconSettings { Mode = ReconMode.Full };
            int done = 0;
            foreach (LayersJob job in file.jobs)
            {
                if (job.id == null || !job.id.StartsWith("ty-")) continue;
                rig.Load(job, input);
                LayerFrame f = rig.Frame;
                GrainTable.ApplyConverged(f);
                // The product's soft density takes the layer's own exponent: the frame's stays 1 (SoftTable.Apply).
                f.EdgePower = 1f;
                // The sun as the product gives it (E40 sun colour): the white sun outside the air, Kerbin's air above each sample in the shader.
                f.Light.SunAirWhiteBalance = false;
                f.Light.UseAir(new Vector3(0.01534f, 0.03571f, 0.08755f) / 1000f, 3840f, 0.01214f / 0.9f / 1000f, 1000f);
                f.SunColour = new Color(White, White, White, 1f);
                GrainTable.Pose(Cam, job, 0f, 0f);
                recon.Reset();
                var cb = new CommandBuffer { name = "type sheet" };
                recon.Record(cb, rig.Renderer, f, Cam, full, 0, null, null, null);
                Graphics.ExecuteCommandBuffer(cb);
                cb.Release();
                Dump(recon.Output, job.id);
                done++;
                yield return null;
            }
            recon.Dispose();
            rig.Dispose();
            Cam.targetTexture = Target;
            rt.Release();
            Sink.Info("type_sheet_frames", done);
            Sink.Flag("type_sheet_completed", true);
        }
    }
}
