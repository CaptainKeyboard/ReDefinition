using System;
using System.Collections;
using System.IO;
using System.Text;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // The cloud light in the player (package T03, -mode scene -scene light -in <folder>): draws the jobs of <folder>/jobs.json with the mod's own
    // LayerRenderer and the light's settings of the job (the phase, the octaves, powder, ambient, the extra lights, the light cache), and writes
    // each job's four channels as <id>-r.npy, -g, -b, -a (float32, row 0 at the top). What the channels hold depends on the job's aid number
    // (LightJob). The judging, the pictures and the sweeps are tools/weather_metrics/light.py, which also wrote the jobs. No upscaler, no proxy.
    [WeatherScene("light")]
    public sealed class LightScene : WeatherSceneBase
    {
        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            bool have = input != null && File.Exists(Path.Combine(input, "jobs.json"));
            Sink.Flag("light_input", have);
            if (!have) yield break;
            LightJobFile file = JsonUtility.FromJson<LightJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            Shader shader = Shader.Find(LayerRenderer.ShaderName);
            Shader depthShader = Shader.Find("Hidden/ReDefinition/LayersDepth");
            Sink.Flag("light_shader", shader != null && depthShader != null);
            if (shader == null || depthShader == null) yield break;
            var renderer = new LayerRenderer();
            bool ok = renderer.Init(shader);
            Sink.Flag("light_compiled", ok);
            if (!ok) yield break;
            var depthMaterial = new Material(depthShader);
            Directory.CreateDirectory(Output);
            var cache = new LightCache();
            bool finite = true;
            int done = 0;
            foreach (LightJob job in file.jobs)
            {
                LayersJob lj = job.layerJob;
                LayerFrame frame = LayersSetup.Load(lj, input, renderer);
                Configure(job, frame.Light);
                frame.AidMode = job.aid;
                float tan = Mathf.Tan(lj.fovV * Mathf.Deg2Rad * 0.5f);
                Vector3 right = LayersSetup.V(lj.right) * tan * (lj.width / (float)lj.height), up = LayersSetup.V(lj.up) * tan;
                renderer.SetCamera(LayersSetup.V(lj.position), LayersSetup.V(lj.forward), right, up, Vector2.zero);
                var target = new RenderTexture(lj.width, lj.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point };
                target.Create();
                RenderTexture depth = null;
                if (frame.UseSceneDepth)
                {
                    depth = new RenderTexture(lj.width, lj.height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point };
                    depth.Create();
                    depthMaterial.SetVector("_CamPos", LayersSetup.V(lj.position));
                    depthMaterial.SetVector("_CamForward", LayersSetup.V(lj.forward));
                    depthMaterial.SetVector("_CamRight", right);
                    depthMaterial.SetVector("_CamUp", up);
                    depthMaterial.SetVector("_Occ", new Vector4(lj.occluder[0], lj.occluder[1], lj.occluder[2], lj.occluder[3]));
                    Graphics.Blit(null, depth, depthMaterial);
                    renderer.Material.SetTexture("_SceneDepth", depth);
                }
                if (job.cache != 0)
                {
                    cache.Min = LayersSetup.V(job.cacheMin);
                    cache.Size = LayersSetup.V(job.cacheSize);
                    cache.Enabled = false;
                    frame.Light.Cache = cache;
                    renderer.Apply(frame);
                    cache.Fill(renderer.Material);
                    yield return new WaitForEndOfFrame();
                    cache.Enabled = true;
                }
                renderer.Apply(frame);
                Graphics.Blit(null, target, renderer.Material, LayerRenderer.PassMarch);
                yield return new WaitForEndOfFrame();
                finite &= WritePlanes(job, lj, Read(target));
                target.Release();
                if (depth != null) depth.Release();
                done++;
                yield return Frames(1);
            }
            cache.Dispose();
            Sink.Flag("light_finite", finite);
            Sink.Info("light_jobs", done);
        }

        internal static void Configure(LightJob job, CloudLightSettings s)
        {
            if (job.useDefaults == 0)
            {
                s.ForwardG = job.forwardG; s.BackG = job.backG; s.ForwardWeight = job.forwardWeight; s.Albedo = job.albedo;
                s.Octaves = job.octaves; s.OctaveA = job.octaveA; s.OctaveB = job.octaveB; s.OctaveC = job.octaveC; s.MsBrightness = job.msBrightness;
                s.Diffusion = job.diffusion; s.DiffusionTau = job.diffusionTau;
                s.PowderStrength = job.powder; s.PowderLengthM = job.powderLength;
                s.AmbientFloor = job.ambientFloor; s.AmbientOcclusion = job.ambientOcclusion; s.SkyTint = job.skyTint; s.AmbientGain = job.ambientGain;
                s.SunStepsOverride = job.sunSteps; s.MoonSteps = job.moonSteps; s.UpSteps = job.upSteps;
                s.ConeSpread = job.coneSpread; s.TapRatio = job.tapRatio; s.CacheFillSteps = job.cacheFillSteps;
                if (job.layerScale != null && job.layerScale.Length >= 3) s.LayerScale[0] = new Vector3(job.layerScale[0], job.layerScale[1], job.layerScale[2]);
            }
            SetLight(s, 0, job.moonDir0, job.moonCol0);
            SetLight(s, 1, job.moonDir1, job.moonCol1);
            int bolts = job.boltRange == null ? 0 : Math.Min(job.boltRange.Length, CloudLightSettings.MaxBolts);
            s.BoltCount = bolts;
            for (int i = 0; i < bolts; i++)
                s.Bolts[i] = new CloudBolt
                {
                    Position = new Vector3(job.boltPos[i * 3], job.boltPos[i * 3 + 1], job.boltPos[i * 3 + 2]),
                    RangeM = job.boltRange[i],
                    Colour = new Color(job.boltCol[i * 3], job.boltCol[i * 3 + 1], job.boltCol[i * 3 + 2])
                };
            if (job.cellRadius > 0f && job.cellCentre != null)
            {
                s.CellCentre = LayersSetup.V(job.cellCentre);
                s.CellRadiusM = job.cellRadius; s.CellVerticalM = job.cellVertical; s.CellSigma = job.cellSigma;
            }
        }

        private static void SetLight(CloudLightSettings s, int i, float[] dir, float[] col)
        {
            if (dir == null || col == null || dir.Length < 3 || col.Length < 3) return;
            s.Lights[i] = new CloudLight { Towards = LayersSetup.V(dir), Colour = new Color(col[0], col[1], col[2]) };
        }

        private bool WritePlanes(LightJob job, LayersJob lj, float[] pixels)
        {
            bool finite = true;
            string[] names = { "r", "g", "b", "a" };
            for (int c = 0; c < 4; c++)
            {
                var plane = new float[lj.width * lj.height];
                for (int row = 0; row < lj.height; row++)
                {
                    int src = (lj.height - 1 - row) * lj.width * 4;
                    for (int x = 0; x < lj.width; x++)
                    {
                        float v = pixels[src + x * 4 + c];
                        if (float.IsNaN(v) || float.IsInfinity(v)) finite = false;
                        plane[row * lj.width + x] = v;
                    }
                }
                WriteNpy(Path.Combine(Output, job.id + "-" + names[c] + ".npy"), plane, lj.width, lj.height);
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
    }
}
