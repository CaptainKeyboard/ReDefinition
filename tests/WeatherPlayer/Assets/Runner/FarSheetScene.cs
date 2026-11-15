// Scene of the weather player for the light of far clouds (E40 FL far; tools/weather_metrics/e40far.py writes the jobs and measures the output):
//   -scene far-sheet -in <e40far inputs>   every job "fl-*": one cloud seen from a given distance, drawn by the product's layer pass (the march numbers of the job, the sun as the
//                                          product gives it: the white sun outside the air and Kerbin's air above each sample). Per job three float32 arrays (rows from the top):
//                                          <id>-lum.npy (radiance and alpha of the march), <id>-aid.npy (the direct light's luminance, the light taps, the ambient's luminance,
//                                          alpha: aid 4) and <id>-aux.npy (alpha, optical depth, steps, first hit's distance). The light switches of a job are tokens of its id
//                                          between dashes: oct1 (one octave: no multiple scattering), ao0 (no ambient occlusion and no sky diffusion), diff0 (no deep-core diffusion).
//                                          job.accumulate frames (the frame numbers move the step offset) are averaged for the lum and aid arrays.
using System;
using System.Collections;
using System.IO;
using System.Text;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    [WeatherScene("far-sheet")]
    public sealed class FarSheetScene : WeatherSceneBase
    {
        private const float White = 2.35f;

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            bool ready = input != null && File.Exists(Path.Combine(input, "jobs.json"));
            Sink.Flag("far_sheet_input", ready);
            if (!ready) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            Shader shader = Shader.Find(LayerRenderer.ShaderName);
            if (shader == null) yield break;
            var renderer = new LayerRenderer();
            bool ok = renderer.Init(shader);
            Sink.Flag("far_sheet_compiled", ok);
            if (!ok) yield break;
            Directory.CreateDirectory(Output);
            int done = 0;
            foreach (LayersJob job in file.jobs)
            {
                if (job.id == null || !job.id.StartsWith("fl-")) continue;
                LayerFrame frame = LayersSetup.Load(job, input, renderer);
                frame.UseSceneDepth = false;
                frame.UseProductCost();
                // The sun as the product gives it (E40 sun colour): the white sun outside the air, Kerbin's air above each sample in the shader.
                frame.Light.SunAirWhiteBalance = false;
                frame.Light.UseAir(new Vector3(0.01534f, 0.03571f, 0.08755f) / 1000f, 3840f, 0.01214f / 0.9f / 1000f, 1000f);
                frame.SunColour = new Color(White, White, White, 1f);
                string[] tokens = job.id.Split('-');
                if (Array.IndexOf(tokens, "oct1") >= 0) frame.Light.Octaves = 1;
                if (Array.IndexOf(tokens, "ao0") >= 0) frame.Light.AmbientOcclusion = 0f;
                if (Array.IndexOf(tokens, "diff0") >= 0) frame.Light.Diffusion = 0f;
                float tan = Mathf.Tan(job.fovV * Mathf.Deg2Rad * 0.5f);
                Vector3 right = LayersSetup.V(job.right) * tan * (job.width / (float)job.height), up = LayersSetup.V(job.up) * tan;
                renderer.SetCamera(LayersSetup.V(job.position), LayersSetup.V(job.forward), right, up, Vector2.zero);
                var target = new RenderTexture(job.width, job.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point };
                target.Create();
                int frames = Mathf.Max(1, job.accumulate);
                float[] lum = null, aid = null;
                for (int k = 0; k < frames; k++)
                {
                    frame.FrameNumber = job.frameNumber + k;
                    frame.AidMode = 0;
                    renderer.Apply(frame);
                    Graphics.Blit(null, target, renderer.Material, LayerRenderer.PassMarch);
                    yield return new WaitForEndOfFrame();
                    Accumulate(ref lum, Read(target), frames);
                    frame.AidMode = 4;
                    renderer.Apply(frame);
                    Graphics.Blit(null, target, renderer.Material, LayerRenderer.PassMarch);
                    yield return new WaitForEndOfFrame();
                    Accumulate(ref aid, Read(target), frames);
                }
                frame.FrameNumber = job.frameNumber;
                frame.AidMode = 0;
                renderer.Apply(frame);
                Graphics.Blit(null, target, renderer.Material, LayerRenderer.PassAux);
                yield return new WaitForEndOfFrame();
                float[] aux = Read(target);
                Write(job, "lum", lum);
                Write(job, "aid", aid);
                Write(job, "aux", aux);
                target.Release();
                done++;
                yield return null;
            }
            Sink.Info("far_sheet_frames", done);
            Sink.Flag("far_sheet_completed", true);
        }

        private static void Accumulate(ref float[] sum, float[] more, int frames)
        {
            if (sum == null) sum = new float[more.Length];
            float w = 1f / frames;
            for (int i = 0; i < more.Length; i++) sum[i] += more[i] * w;
        }

        private static float[] Read(RenderTexture texture)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = texture;
            var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false, true);
            copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            copy.Apply(false);
            RenderTexture.active = previous;
            byte[] bytes = copy.GetRawTextureData();
            UnityEngine.Object.Destroy(copy);
            var raw = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, raw, 0, bytes.Length);
            return raw;
        }

        // (h, w, 4) float32 with the top row first (the texture's row 0 is the bottom).
        private void Write(LayersJob job, string name, float[] pixels)
        {
            int w = job.width, h = job.height;
            string header = "{'descr': '<f4', 'fortran_order': False, 'shape': (" + h + ", " + w + ", 4), }";
            int total = 10 + header.Length + 1;
            header = header + new string(' ', (64 - total % 64) % 64) + "\n";
            using (var stream = new FileStream(Path.Combine(Output, job.id + "-" + name + ".npy"), FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new byte[] { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0 });
                writer.Write((ushort)header.Length);
                writer.Write(Encoding.ASCII.GetBytes(header));
                var row = new byte[w * 16];
                for (int y = 0; y < h; y++)
                {
                    Buffer.BlockCopy(pixels, (h - 1 - y) * w * 4 * 4, row, 0, row.Length);
                    writer.Write(row);
                }
            }
        }
    }
}
