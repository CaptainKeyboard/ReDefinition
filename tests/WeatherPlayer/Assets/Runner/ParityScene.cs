using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // The synthetic scenes of the parity harness (package T11, -mode scene -scene parity): the seven fixed scenes of the harness drawn by
    // the GPU reference path (Hidden/ReDefinition/AtmospheresParityRef: a brute-force march of the same analytic density fields as the
    // offline renderer tools/weather_metrics/parity_ref.py, single scattering, 64 light steps per layer). The scenes have a fixed
    // camera, sun and field; the numbers are the table of tests/data/parity/scenes.json (the Python side builds its fields from that
    // file; this table mirrors it and tools/weather_metrics/parity_player.py compares the two).
    //
    // Per view it writes, into the output folder, <id>-tau.npy, <id>-cloud.npy, <id>-lum.npy, <id>-steps.npy (float32, row 0 at the
    // top) and two pictures: <id>.png (natural: tone-mapped radiance) and <id>-aid.png (aid: false-colour optical depth on the left,
    // heat map of the steps that hit density on the right). The metric lines are the means; the series T11-scene compares the arrays with
    // the offline renderer on the same field. The scene needs no upscaler and no proxy.
    [WeatherScene("parity")]
    public sealed class ParityScene : WeatherSceneBase
    {
        private const int W = 96, H = 72;
        private const float KerbinRadius = 600000f;
        private const int ViewSteps = 128, LightSteps = 64;

        private struct View
        {
            public string Id;
            public bool Flat;
            public Vector3 Position;
            public float Pitch, Fov, SunAzimuth, SunElevation;
            // Layers: base, top, sigma, shape mode (0 none, 1 cell, 2 strip edge); sigma 0 = unused.
            public Vector4 L0, L1;
            // The cell (centre altitude, horizontal radius, vertical radius) or the strip's edge position in x.
            public Vector4 Cell;
            // The occluder sphere (centre, radius), radius 0 = none.
            public Vector4 Occluder;
        }

        private static List<View> Views()
        {
            var v = new List<View>();
            v.Add(new View { Id = "uniform-deck", Flat = false, Position = new Vector3(0, 0, KerbinRadius + 2f), Pitch = 25f, Fov = 60f, SunAzimuth = 200f, SunElevation = 38f, L0 = new Vector4(1700f, 2500f, 0.01f, 0f) });
            v.Add(new View { Id = "slab", Flat = true, Position = new Vector3(0, 0, 2f), Pitch = 40f, Fov = 60f, SunAzimuth = 200f, SunElevation = 45f, L0 = new Vector4(2000f, 3000f, 0.006f, 0f) });
            v.Add(new View { Id = "single-cloud", Flat = true, Position = new Vector3(0, -8000f, 2000f), Pitch = 0f, Fov = 16f, SunAzimuth = 90f, SunElevation = 50f, L0 = new Vector4(1412f, 2588f, 0.02f, 1f), Cell = new Vector4(2000f, 800f, 560f, 0f) });
            v.Add(new View { Id = "plane-terrain", Flat = true, Position = new Vector3(0, 0, 600f), Pitch = -45f, Fov = 50f, SunAzimuth = 90f, SunElevation = 40f, L0 = new Vector4(2000f, 2200f, 0.02f, 2f), Cell = new Vector4(2503f, 0f, 0f, 0f) });
            v.Add(new View { Id = "two-slab", Flat = true, Position = new Vector3(0, 0, 2f), Pitch = 40f, Fov = 60f, SunAzimuth = 200f, SunElevation = 45f, L0 = new Vector4(1500f, 1900f, 0.005f, 0f), L1 = new Vector4(3000f, 3400f, 0.0075f, 0f) });
            v.Add(new View { Id = "mountain-silhouette", Flat = true, Position = new Vector3(0, 0, 100f), Pitch = 4f, Fov = 50f, SunAzimuth = 160f, SunElevation = 35f, L0 = new Vector4(1500f, 2100f, 5f / 600f, 0f), Occluder = new Vector4(0f, 30000f, -30000f + 900f, 30000f) });
            foreach (float altitude in new[] { 8000f, 50000f, 100000f, 200000f, 260000f })
                v.Add(new View { Id = "altitude-" + ((int)altitude).ToString(CultureInfo.InvariantCulture), Flat = false, Position = new Vector3(0, 0, KerbinRadius + altitude), Pitch = -45f, Fov = 50f, SunAzimuth = 200f, SunElevation = 30f, L0 = new Vector4(2200f, 2500f, 0.02f, 0f) });
            return v;
        }

        public override IEnumerator Run()
        {
            Shader shader = Shader.Find("Hidden/ReDefinition/AtmospheresParityRef");
            Sink.Flag("parity_shader", shader != null);
            if (shader == null) yield break;
            var material = new Material(shader);
            var target = new RenderTexture(W, H, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            target.Create();
            Directory.CreateDirectory(Output);
            bool finite = true;
            foreach (View view in Views())
            {
                Apply(material, view);
                Graphics.Blit(null, target, material);
                yield return new WaitForEndOfFrame();
                float[] pixels = Read(target);
                float[] tau = new float[W * H], cloud = new float[W * H], lum = new float[W * H], steps = new float[W * H];
                double sumTau = 0, sumLum = 0;
                for (int row = 0; row < H; row++)
                {
                    int src = (H - 1 - row) * W * 4;
                    for (int c = 0; c < W; c++)
                    {
                        int i = row * W + c, s = src + c * 4;
                        tau[i] = pixels[s];
                        cloud[i] = pixels[s + 1];
                        lum[i] = pixels[s + 2];
                        steps[i] = pixels[s + 3];
                        if (float.IsNaN(lum[i]) || float.IsInfinity(lum[i]) || float.IsNaN(tau[i]) || float.IsInfinity(tau[i])) finite = false;
                        sumTau += tau[i];
                        sumLum += lum[i];
                    }
                }
                WriteNpy(Path.Combine(Output, view.Id + "-tau.npy"), tau);
                WriteNpy(Path.Combine(Output, view.Id + "-cloud.npy"), cloud);
                WriteNpy(Path.Combine(Output, view.Id + "-lum.npy"), lum);
                WriteNpy(Path.Combine(Output, view.Id + "-steps.npy"), steps);
                WritePictures(Output, view.Id, tau, lum, steps);
                Sink.Info(view.Id + ".mean_tau", sumTau / (W * H));
                Sink.Info(view.Id + ".mean_lum", sumLum / (W * H));
                yield return Frames(1);
            }
            Sink.Flag("parity_finite", finite);
        }

        // Sets the uniforms of one view: the camera basis and the sun as the offline renderer builds them (yaw 0: north is +y, east is +x).
        private static void Apply(Material m, View v)
        {
            Vector3 upAxis = new Vector3(0f, 0f, 1f), east = new Vector3(1f, 0f, 0f), north = new Vector3(0f, 1f, 0f);
            float p = v.Pitch * Mathf.Deg2Rad;
            Vector3 forward = Mathf.Cos(p) * north + Mathf.Sin(p) * upAxis;
            Vector3 right = Vector3.Cross(forward, upAxis).normalized;
            Vector3 camUp = Vector3.Cross(right, forward);
            float az = v.SunAzimuth * Mathf.Deg2Rad, el = v.SunElevation * Mathf.Deg2Rad;
            Vector3 sun = Mathf.Cos(el) * (Mathf.Cos(az) * north + Mathf.Sin(az) * east) + Mathf.Sin(el) * upAxis;
            m.SetFloat("_PlanetR", KerbinRadius);
            m.SetFloat("_Flat", v.Flat ? 1f : 0f);
            m.SetVector("_CamPos", new Vector4(v.Position.x, v.Position.y, v.Position.z, 0f));
            m.SetVector("_Fwd", forward);
            m.SetVector("_Right", right);
            m.SetVector("_UpC", camUp);
            m.SetFloat("_HalfV", Mathf.Tan(v.Fov * Mathf.Deg2Rad * 0.5f));
            m.SetFloat("_Aspect", W / (float)H);
            m.SetVector("_Sun", sun);
            m.SetVector("_L0", v.L0);
            m.SetVector("_L1", v.L1);
            m.SetVector("_Cell", v.Cell);
            m.SetVector("_Occ", v.Occluder);
            m.SetFloat("_ViewSteps", ViewSteps);
            m.SetFloat("_LightSteps", LightSteps);
            // The reference's dual-lobe phase function (forward g 0.85, back g -0.3, forward weight 0.92) and albedo 0.999.
            m.SetVector("_Phase", new Vector4(0.85f, -0.30f, 0.92f, 0.999f));
            // The reference's environment: zenith 0.30, horizon 0.55, below the horizon 0.12, ground albedo 0.3.
            m.SetVector("_Env", new Vector4(0.30f, 0.55f, 0.12f, 0.30f));
            m.SetFloat("_GroundAlbedoAmbient", 0.425f);
        }

        private static float[] Read(RenderTexture texture)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = texture;
            var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false, true);
            copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            copy.Apply(false);
            RenderTexture.active = previous;
            Color[] colours = copy.GetPixels();
            UnityEngine.Object.Destroy(copy);
            var raw = new float[colours.Length * 4];
            for (int i = 0; i < colours.Length; i++)
            {
                raw[i * 4] = colours[i].r;
                raw[i * 4 + 1] = colours[i].g;
                raw[i * 4 + 2] = colours[i].b;
                raw[i * 4 + 3] = colours[i].a;
            }
            return raw;
        }

        // A float32 array of H by W as a NumPy .npy file (format 1.0, little endian, C order).
        private static void WriteNpy(string path, float[] data)
        {
            string header = "{'descr': '<f4', 'fortran_order': False, 'shape': (" + H + ", " + W + "), }";
            int total = 10 + header.Length + 1;
            header = header + new string(' ', (64 - total % 64) % 64) + "\n";
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new byte[] { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0 });
                writer.Write((ushort)header.Length);
                writer.Write(Encoding.ASCII.GetBytes(header));
                var raw = new byte[data.Length * 4];
                Buffer.BlockCopy(data, 0, raw, 0, raw.Length);
                writer.Write(raw);
            }
        }

        // The natural picture (tone-mapped radiance) and the aid picture (false-colour optical depth | step heat map).
        private static void WritePictures(string output, string id, float[] tau, float[] lum, float[] steps)
        {
            var natural = new Texture2D(W, H, TextureFormat.RGB24, false);
            var aid = new Texture2D(W * 2 + 2, H, TextureFormat.RGB24, false);
            float maxSteps = 1f;
            for (int i = 0; i < steps.Length; i++) maxSteps = Mathf.Max(maxSteps, steps[i]);
            for (int row = 0; row < H; row++)
                for (int c = 0; c < W; c++)
                {
                    int i = row * W + c, y = H - 1 - row;
                    float g = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Exp(-lum[i] * 1.3f)), 1f / 2.2f);
                    natural.SetPixel(c, y, new Color(g, g, g));
                    float t = Mathf.Clamp01((Mathf.Log10(Mathf.Max(tau[i], 1e-4f)) + 3f) / 4.5f);
                    aid.SetPixel(c, y, Color.Lerp(new Color(0.05f, 0.05f, 0.3f), t < 0.5f ? new Color(0.2f, 0.8f, 0.5f) : new Color(0.95f, 0.2f, 0.1f), t));
                    float s = steps[i] / maxSteps;
                    aid.SetPixel(W + 2 + c, y, Color.Lerp(Color.black, new Color(1f, 0.9f, 0.4f), s));
                }
            natural.Apply();
            aid.Apply();
            File.WriteAllBytes(Path.Combine(output, id + ".png"), natural.EncodeToPNG());
            File.WriteAllBytes(Path.Combine(output, id + "-aid.png"), aid.EncodeToPNG());
            UnityEngine.Object.Destroy(natural);
            UnityEngine.Object.Destroy(aid);
        }
    }
}
