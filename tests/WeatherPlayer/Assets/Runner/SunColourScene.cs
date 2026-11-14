// Scene of the weather player for the sun colour of the own clouds (E40 sun colour; tools/weather_metrics/e40sc.py measures the output, the inputs are e40gn's):
//   -scene sun-colour -in <e40gn inputs>   one layer, the camera at the ground (264 m) and above the layer (8364 m): a converged picture with the sun colour as the game
//                                          gave it before (Scatterer's colour at the camera, no air in the shader: "old") and as the pass gives it now (the camera's air
//                                          divided out, the shader's air above each sample: "new"). The unextinguished sun is the white of the fitted harness (2.35).
using System;
using System.Collections;
using System.IO;
using System.Text;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    [WeatherScene("sun-colour")]
    public sealed class SunColourScene : WeatherSceneBase
    {
        private const int W = 1708, H = 960;
        private const float White = 2.35f;

        // Kerbin's atmosphere node of StockScattererConfigs (m_betaR / BETA_MSca per km, HR / HM in km) as CloudBridgeAir.FromScatterer takes it: per m, Mie extinction = BETA_MSca / 0.9.
        private static void Kerbin(CloudLightSettings s)
        {
            s.UseAir(new Vector3(0.01534f, 0.03571f, 0.08755f) / 1000f, 3840f, 0.01214f / 0.9f / 1000f, 1000f);
        }

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
            Sink.Flag("sun_colour_available", ready);
            if (!ready) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR);
            rt.Create();
            Cam.targetTexture = rt;
            var recon = new LayerReconstructor();
            recon.Init(Shader.Find(LayerReconstructor.ShaderName));
            var full = new ReconSettings { Mode = ReconMode.Full };
            // The medium of the product's default look: the soft water content, width 0.5 (the owner's favourite).
            SoftTable.Medium medium = null;
            foreach (SoftTable.Medium m in SoftTable.Media) if (m.Name == "pr50") medium = m;
            int done = 0;
            foreach (string view in new[] { "ground", "above" })
            {
                LayersJob job = GrainTable.Find(file, "gn-" + view + "-prod-f0");
                if (job == null) { Sink.Flag("sun_colour_job_" + view, false); continue; }
                SoftTable.Original original = SoftTable.Original.Of(job.layers[0]);
                rig.Load(job, input);
                double detailMean = SoftTable.DetailMean(rig);
                SoftTable.Load(rig, job, input, medium, original, detailMean);
                Vector3 position = LayersSetup.V(job.position);
                double altitude = position.magnitude - job.planetRadius;
                double sinElevation = Vector3.Dot(position.normalized, LayersSetup.V(job.sun));
                foreach (string variant in new[] { "old", "new" })
                {
                    LayerFrame f = rig.Frame;
                    GrainTable.ApplyConverged(f);
                    f.Light.SunAirWhiteBalance = false;
                    // Scatterer's colour at the camera: the white sun through the air above the camera (the model, which reproduces the measured 0.69 and 0.96 blue over red).
                    var scatterer = new CloudLightSettings { SunAirWhiteBalance = false };
                    Kerbin(scatterer);
                    Color atCamera = new Color(White * (float)CloudLightMath.SunAir(sinElevation, scatterer.SunAirTau.x, scatterer.SunAirHeightM, scatterer.SunAirMieTau, scatterer.SunAirMieHeightM, altitude),
                                               White * (float)CloudLightMath.SunAir(sinElevation, scatterer.SunAirTau.y, scatterer.SunAirHeightM, scatterer.SunAirMieTau, scatterer.SunAirMieHeightM, altitude),
                                               White * (float)CloudLightMath.SunAir(sinElevation, scatterer.SunAirTau.z, scatterer.SunAirHeightM, scatterer.SunAirMieTau, scatterer.SunAirMieHeightM, altitude), 1f);
                    if (variant == "old")
                    {
                        f.Light.SunAirHeightM = 0f;
                        f.SunColour = atCamera;
                    }
                    else
                    {
                        Kerbin(f.Light);
                        f.SunColour = CloudSunAir.OutsideAir(atCamera, new Color(White, White, White, 1f), sinElevation, altitude, f.Light);
                    }
                    Sink.Info("suncolour." + view + "." + variant + ".r", f.SunColour.r);
                    Sink.Info("suncolour." + view + "." + variant + ".g", f.SunColour.g);
                    Sink.Info("suncolour." + view + "." + variant + ".b", f.SunColour.b);
                    GrainTable.Pose(Cam, job, 0f, 0f);
                    recon.Reset();
                    var cb = new CommandBuffer { name = "sun colour" };
                    recon.Record(cb, rig.Renderer, f, Cam, full, 0, null, null, null);
                    Graphics.ExecuteCommandBuffer(cb);
                    cb.Release();
                    Dump(recon.Output, "sc-" + view + "-" + variant);
                    done++;
                    yield return null;
                }
                original.Restore(job.layers[0]);
            }
            recon.Dispose();
            rig.Dispose();
            Cam.targetTexture = Target;
            rt.Release();
            Sink.Info("sun_colour_frames", done);
            Sink.Flag("sun_colour_completed", true);
        }
    }
}
