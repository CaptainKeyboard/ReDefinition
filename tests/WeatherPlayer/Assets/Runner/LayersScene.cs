using System;
using System.Collections;
using System.IO;
using System.Text;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // The layer core in the player (package T01, -mode scene -scene layers -in <folder>): draws the jobs of <folder>/jobs.json with the
    // mod's own LayerRenderer (the shell shader, the type table, the maps) and writes, per job, the measurement arrays (mode aux:
    // <id>-alpha.npy, -tau.npy, -steps.npy, -first.npy, plus <id>-depth.npy where the job has an occluder; mode lum: <id>-r.npy, -g, -b,
    // -a of the march), float32 with row 0 at the top, or the pictures (mode color: <id>.png natural, <id>-aid.png optical depth | layer
    // colours). The judging is tools/weather_metrics/layers.py, which also wrote the jobs: this scene knows nothing of the checks. The
    // scene needs no upscaler and no proxy.
    [WeatherScene("layers")]
    public sealed class LayersScene : WeatherSceneBase
    {
        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            Sink.Flag("layers_input", input != null && File.Exists(Path.Combine(input, "jobs.json")));
            if (input == null || !File.Exists(Path.Combine(input, "jobs.json"))) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            Shader shader = Shader.Find(LayerRenderer.ShaderName);
            Shader depthShader = Shader.Find("Hidden/ReDefinition/LayersDepth");
            Sink.Flag("layers_shader", shader != null);
            Sink.Flag("layers_depth_shader", depthShader != null);
            if (shader == null || depthShader == null) yield break;
            var renderer = new LayerRenderer();
            bool ok = renderer.Init(shader);
            Sink.Flag("layers_compiled", ok);
            if (!ok) yield break;
            var depthMaterial = new Material(depthShader);
            Directory.CreateDirectory(Output);
            bool finite = true;
            int done = 0;
            foreach (LayersJob job in file.jobs)
            {
                LayerFrame frame = LayersSetup.Load(job, input, renderer);
                if (done == 0)
                {
                    Sink.Info("noise_generation_ms", renderer.Noise.GenerationMs);
                    Sink.Info("noise_volume_bytes", renderer.Noise.VolumeBytes);
                }
                float tan = Mathf.Tan(job.fovV * Mathf.Deg2Rad * 0.5f);
                Vector3 right = LayersSetup.V(job.right) * tan * (job.width / (float)job.height), up = LayersSetup.V(job.up) * tan;
                renderer.SetCamera(LayersSetup.V(job.position), LayersSetup.V(job.forward), right, up, Vector2.zero);
                var target = new RenderTexture(job.width, job.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point };
                target.Create();
                RenderTexture depth = null;
                if (frame.UseSceneDepth)
                {
                    depth = new RenderTexture(job.width, job.height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point };
                    depth.Create();
                    depthMaterial.SetVector("_CamPos", LayersSetup.V(job.position));
                    depthMaterial.SetVector("_CamForward", LayersSetup.V(job.forward));
                    depthMaterial.SetVector("_CamRight", right);
                    depthMaterial.SetVector("_CamUp", up);
                    depthMaterial.SetVector("_Occ", new Vector4(job.occluder[0], job.occluder[1], job.occluder[2], job.occluder[3]));
                    Graphics.Blit(null, depth, depthMaterial);
                    renderer.Material.SetTexture("_SceneDepth", depth);
                }
                renderer.Apply(frame);
                if (job.mode == "aux")
                {
                    Graphics.Blit(null, target, renderer.Material, LayerRenderer.PassAux);
                    yield return new WaitForEndOfFrame();
                    finite &= WritePlanes(job, Read(target), new[] { "alpha", "tau", "steps", "first" });
                    if (depth != null)
                        WriteNpy(Path.Combine(Output, job.id + "-depth.npy"), Plane(Read(depth), job.width, job.height, 0), job.width, job.height);
                }
                else if (job.mode == "lum")
                {
                    Graphics.Blit(null, target, renderer.Material, LayerRenderer.PassMarch);
                    yield return new WaitForEndOfFrame();
                    finite &= WritePlanes(job, Read(target), new[] { "r", "g", "b", "a" });
                }
                else
                {
                    // The natural picture: the march, composited over the sky and the ground on the CPU; the aid pictures: optical depth, layer colours.
                    if (job.resolution < 0.999f)
                    {
                        // The resolution policy: the march at a smaller size, then the depth-aware composite up to the job's size.
                        var small = new RenderTexture(Mathf.Max(8, Mathf.RoundToInt(job.width * job.resolution)), Mathf.Max(8, Mathf.RoundToInt(job.height * job.resolution)),
                                                      0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point };
                        small.Create();
                        Graphics.Blit(null, small, renderer.Material, LayerRenderer.PassMarch);
                        renderer.Material.SetTexture("_MarchTex", small);
                        renderer.SetMarchTexel(small);
                        RenderTexture.active = target;
                        GL.Clear(false, true, Color.clear);
                        RenderTexture.active = null;
                        Graphics.Blit(small, target, renderer.Material, LayerRenderer.PassComposite);
                        yield return new WaitForEndOfFrame();
                        small.Release();
                    }
                    else
                    {
                        frame.FrameNumber = 0;
                        renderer.Apply(frame);
                        Graphics.Blit(null, target, renderer.Material, LayerRenderer.PassMarch);
                        yield return new WaitForEndOfFrame();
                    }
                    float[] natural = Read(target);
                    // Accumulation: the mean of the frames 0 .. N - 1 (premultiplied radiance and alpha average linearly).
                    for (int accum = 1; accum < job.accumulate; accum++)
                    {
                        frame.FrameNumber = accum;
                        renderer.Apply(frame);
                        Graphics.Blit(null, target, renderer.Material, LayerRenderer.PassMarch);
                        yield return new WaitForEndOfFrame();
                        float[] more = Read(target);
                        for (int i = 0; i < natural.Length; i++) natural[i] += more[i];
                    }
                    if (job.accumulate > 1)
                    {
                        float inv = 1f / job.accumulate;
                        for (int i = 0; i < natural.Length; i++) natural[i] *= inv;
                    }
                    frame.FrameNumber = 0;
                    frame.AidMode = 1;
                    renderer.Apply(frame);
                    Graphics.Blit(null, target, renderer.Material, LayerRenderer.PassMarch);
                    yield return new WaitForEndOfFrame();
                    float[] heat = Read(target);
                    frame.AidMode = 2;
                    renderer.Apply(frame);
                    Graphics.Blit(null, target, renderer.Material, LayerRenderer.PassMarch);
                    yield return new WaitForEndOfFrame();
                    float[] layerColours = Read(target);
                    foreach (float v in natural) if (float.IsNaN(v) || float.IsInfinity(v)) finite = false;
                    WritePictures(job, natural, heat, layerColours);
                    double alphaSum = 0;
                    for (int i = 3; i < natural.Length; i += 4) alphaSum += natural[i];
                    Sink.Info(job.id + ".mean_alpha", alphaSum / (job.width * job.height));
                }
                target.Release();
                if (depth != null) depth.Release();
                done++;
                yield return Frames(1);
            }
            Sink.Flag("layers_finite", finite);
            Sink.Info("layers_jobs", done);
        }

        // Writes the four channels of a read target as <id>-<name>.npy; false where one holds a NaN or infinity.
        private bool WritePlanes(LayersJob job, float[] pixels, string[] names)
        {
            bool finite = true;
            for (int c = 0; c < names.Length; c++)
            {
                float[] plane = Plane(pixels, job.width, job.height, c);
                for (int i = 0; i < plane.Length; i++)
                    if (float.IsNaN(plane[i]) || float.IsInfinity(plane[i])) finite = false;
                WriteNpy(Path.Combine(Output, job.id + "-" + names[c] + ".npy"), plane, job.width, job.height);
            }
            return finite;
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

        // One channel as an array of rows from the top (the texture's row 0 is the bottom).
        private static float[] Plane(float[] pixels, int w, int h, int channel)
        {
            var plane = new float[w * h];
            for (int row = 0; row < h; row++)
            {
                int src = (h - 1 - row) * w * 4;
                for (int c = 0; c < w; c++) plane[row * w + c] = pixels[src + c * 4 + channel];
            }
            return plane;
        }

        private static void WriteNpy(string path, float[] data, int w, int h)
        {
            string header = "{'descr': '<f4', 'fortran_order': False, 'shape': (" + h + ", " + w + "), }";
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

        // The sky behind the clouds: a gradient from the horizon to the zenith by the ray's elevation over the local horizontal, the ground
        // below the sea-level sphere lit by the sun.
        private static CloudLightSettings DiscAir;

        private static Color Backdrop(LayersJob job, Vector3 origin, Vector3 d)
        {
            Vector3 up = origin.normalized;
            float s = Vector3.Dot(d, up);
            Color horizon = LayersSetup.Rgb(job.skyHorizon, new Color(0.62f, 0.68f, 0.76f)), zenith = LayersSetup.Rgb(job.skyZenith, new Color(0.26f, 0.42f, 0.75f));
            Color sky = s >= 0f ? Color.Lerp(horizon, zenith, Mathf.Pow(Mathf.Clamp01(s), 0.5f)) : horizon * 0.5f;
            if (job.space != 0)
            {
                // From orbit: black sky, a thin blue glow over the limb by the ray's closest approach to the surface.
                float closest = Vector3.Cross(origin, d).magnitude - job.planetRadius;
                float glow = Mathf.Exp(-Mathf.Max(0f, closest) / 25000f) * 0.55f;
                sky = new Color(0.30f, 0.50f, 1.0f) * glow * 0.6f;
            }
            // The sun's (or, at night, the moon's) disc, its aureole and the stars (T03v2; off where discGain and stars are 0). The disc is the frame's
            // sun colour through the air path (the shader's white-balanced law) times discGain, so that it saturates the tone map; the angular radius is the real one.
            if (job.space == 0 && job.discGain > 0f)
            {
                Vector3 sd = LayersSetup.V(job.sun);
                float cosA = Mathf.Clamp(Vector3.Dot(d, sd), -1f, 1f);
                float theta = Mathf.Acos(cosA);
                float sinElev = Vector3.Dot(sd, up);
                CloudLightSettings air = DiscAir ?? (DiscAir = new CloudLightSettings());
                Color sc = LayersSetup.Rgb(job.sunColour, new Color(3.0f, 2.9f, 2.7f));
                float alt = Mathf.Max(origin.magnitude - job.planetRadius, 0f);
                Color through = new Color(sc.r * (float)CloudLightMath.SunAir(sinElev, air.SunAirTau.x, alt, air.SunAirHeightM, true),
                                          sc.g * (float)CloudLightMath.SunAir(sinElev, air.SunAirTau.y, alt, air.SunAirHeightM, true),
                                          sc.b * (float)CloudLightMath.SunAir(sinElev, air.SunAirTau.z, alt, air.SunAirHeightM, true));
                const float DiscRadius = 0.00465f;   // 0.2666 degrees
                if (theta < DiscRadius) sky += through * job.discGain;
                // The aureole: forward-scattering haze around the disc, falling off with the angle.
                float a = theta / 0.06f;
                sky += through * (0.04f / Mathf.Pow(1f + a * a, 1.5f)) * job.aureole;
            }
            if (job.space == 0 && job.stars > 0f && s > 0.02f)
            {
                // Point stars on a lattice of directions (a hash per cell), dimmer near the horizon (the air's extinction).
                var cell = new Vector3Int(Mathf.FloorToInt(d.x * 260f), Mathf.FloorToInt(d.y * 260f), Mathf.FloorToInt(d.z * 260f));
                uint h = (uint)(cell.x * 73856093) ^ (uint)(cell.y * 19349663) ^ (uint)(cell.z * 83492791);
                h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
                float r1 = (h & 0xFFFFu) / 65535f, r2 = ((h >> 16) & 0xFFFFu) / 65535f;
                if (r1 > 0.985f) sky += new Color(0.85f, 0.92f, 1.0f) * job.stars * Mathf.Pow(r2, 3f) * Mathf.Clamp01(s * 3f);
            }
            Color groundAlbedo = LayersSetup.Rgb(job.groundColour, new Color(0.20f, 0.24f, 0.16f));
            if (job.hasSurface != 0)
            {
                float b = Vector3.Dot(origin, d), c = origin.sqrMagnitude - job.planetRadius * job.planetRadius;
                float disc = b * b - c;
                if (disc > 0f && -b - Mathf.Sqrt(disc) > 0f)
                {
                    Vector3 p = origin + d * (-b - Mathf.Sqrt(disc));
                    Vector3 n = p.normalized;
                    float lit = Mathf.Max(0f, Vector3.Dot(n, LayersSetup.V(job.sun)));
                    float floor = job.space != 0 ? 0.02f : 0.25f;
                    return groundAlbedo * (floor + 0.9f * lit);
                }
            }
            return sky;
        }

        private static bool OccluderHit(LayersJob job, Vector3 origin, Vector3 d)
        {
            if (job.occluder == null || job.occluder.Length != 4 || job.occluder[3] <= 0f) return false;
            Vector3 oc = origin - new Vector3(job.occluder[0], job.occluder[1], job.occluder[2]);
            float b = Vector3.Dot(oc, d), c = oc.sqrMagnitude - job.occluder[3] * job.occluder[3];
            return b * b - c > 0f && -b - Mathf.Sqrt(b * b - c) > 0f;
        }

        private void WritePictures(LayersJob job, float[] natural, float[] heat, float[] layerColours)
        {
            int w = job.width, h = job.height;
            var picture = new Texture2D(w, h, TextureFormat.RGB24, false);
            var aid = new Texture2D(w * 2 + 2, h, TextureFormat.RGB24, false);
            float tan = Mathf.Tan(job.fovV * Mathf.Deg2Rad * 0.5f), aspect = w / (float)h;
            Vector3 origin = LayersSetup.V(job.position), fwd = LayersSetup.V(job.forward), right = LayersSetup.V(job.right), up = LayersSetup.V(job.up);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    float u = ((x + 0.5f) / w * 2f - 1f) * tan * aspect, v = ((y + 0.5f) / h * 2f - 1f) * tan;
                    Vector3 d = (fwd + u * right + v * up).normalized;
                    Color back = Backdrop(job, origin, d);
                    // The occluder of the job (a hill) is the backdrop where the ray hits it; the cloud march ended at its depth.
                    if (OccluderHit(job, origin, d)) back = new Color(0.16f, 0.17f, 0.13f);
                    float alpha = natural[i + 3];
                    Color c = new Color(natural[i] + (1f - alpha) * back.r, natural[i + 1] + (1f - alpha) * back.g, natural[i + 2] + (1f - alpha) * back.b);
                    picture.SetPixel(x, y, Tone(c, job.exposure));
                    // The aid picture: optical depth on the left (false colour), layer colours on the right, both over the backdrop in grey.
                    Color grey = Tone(back * 0.6f, job.exposure);
                    float ha = heat[i + 3];
                    aid.SetPixel(x, y, ha > 0.002f ? new Color(heat[i], heat[i + 1], heat[i + 2]) : grey);
                    float la = layerColours[i + 3];
                    aid.SetPixel(w + 2 + x, y, la > 0.002f ? new Color(layerColours[i], layerColours[i + 1], layerColours[i + 2]) / Mathf.Max(la, 1e-3f) : grey);
                }
            picture.Apply();
            aid.Apply();
            File.WriteAllBytes(Path.Combine(Output, job.id + ".png"), picture.EncodeToPNG());
            File.WriteAllBytes(Path.Combine(Output, job.id + "-aid.png"), aid.EncodeToPNG());
            UnityEngine.Object.Destroy(picture);
            UnityEngine.Object.Destroy(aid);
        }

        private static Color Tone(Color c, float exposure = 0f)
        {
            float k = exposure > 0f ? exposure : 1.2f;
            return new Color(Mathf.Pow(1f - Mathf.Exp(-c.r * k), 1f / 2.2f), Mathf.Pow(1f - Mathf.Exp(-c.g * k), 1f / 2.2f), Mathf.Pow(1f - Mathf.Exp(-c.b * k), 1f / 2.2f));
        }
    }
}
