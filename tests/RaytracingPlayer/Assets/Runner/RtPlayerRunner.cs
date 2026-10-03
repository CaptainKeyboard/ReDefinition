using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ReDefinition.Raytracing;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.RaytracingPlayer
{
    // Every renderer of the scene, as the rays' scene.
    internal sealed class RtUnitySource : IRtSource
    {
        public void Collect(List<RtItem> into)
        {
            foreach (MeshRenderer renderer in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                Material[] materials = renderer.sharedMaterials;
                for (int s = 0; s < filter.sharedMesh.subMeshCount; s++)
                    into.Add(new RtItem
                    {
                        Mesh = filter.sharedMesh,
                        SubMesh = s,
                        Material = s < materials.Length ? materials[s] : null,
                        LocalToWorld = renderer.localToWorldMatrix,
                        Mask = 0xFF,
                    });
            }
        }
    }

    // Instances only the GPU knows, as Parallax's scatter: an append buffer of
    // transforms with a count, no renderer.
    internal sealed class RtGpuBoxes : IRtGpuSource
    {
        internal ComputeBuffer Transforms;
        internal Mesh Mesh;
        internal Material Material;

        public void CollectGpu(List<RtGpuItem> into)
        {
            into.Add(new RtGpuItem { Transforms = Transforms, Mesh = Mesh, SubMesh = 0, Material = Material, Mask = 0xFF });
        }
    }

    // The raytracing test player's run (docs/development/raytracing.md, "Testing
    // without the game"). It builds a scene, renders it without raytracing and with
    // it, and checks the images: a shadow the shadow maps do not reach, one they do,
    // light that does not change. Its lines start with RTPLAYER in the player's log;
    // the images go to -out.
    //
    //   RaytracingPlayer.exe -logFile player.log -out <folder> [-deferred <folder with
    //     Deferred's "deferredshaders" bundle>] [-builtinDeferred 1] [-budget 1]
    //
    // With -budget it measures instead: a scene of the size KSP's flight holds near the
    // ground, every signal on, against the GPU time budget. With -turn it records a
    // camera turn instead (RtTurnRunner).
    public sealed class RtPlayerRunner : MonoBehaviour
    {
        private readonly Vector3 towardsSun = new Vector3(0.3f, 0.6f, 0.74f).normalized;
        private string output;
        private bool deferred;
        private bool deferredMod;
        private int failures;
        private Camera cam;
        private RtCamera raytracing;
        private ReflectionProbe probe;
        // A box only the rays see, from the GPU's instances.
        private readonly Vector3 gpuBox = new Vector3(8f, 1f, -12f);
        private ComputeBuffer gpuTransforms;

        private bool budget;

        private void Start()
        {
            if (Argument("-turn") != null)
            {
                // The camera turning around a rocket instead (RtTurnRunner).
                gameObject.AddComponent<RtTurnRunner>();
                enabled = false;
                return;
            }
            budget = Argument("-budget") != null;
            output = Argument("-out") ?? Path.Combine(Application.dataPath, "..");
            Directory.CreateDirectory(output);
            Log("start, " + SystemInfo.graphicsDeviceName + ", " + SystemInfo.graphicsDeviceVersion);
            deferredMod = LoadDeferred(Argument("-deferred"));
            deferred = deferredMod || Argument("-builtinDeferred") != null;
            BuildScene();
            StartCoroutine(Run());
        }

        private static string Argument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        private static void Log(string text)
        {
            Debug.Log("RTPLAYER " + text);
        }

        private void Check(bool passed, string what, string detail)
        {
            Log((passed ? "PASS  " : "FAIL  ") + what + " -- " + detail);
            if (!passed) failures++;
        }

        // Deferred's own lighting shaders, set as Deferred sets them (Deferred.cs,
        // Deferred 1.3.5), with the globals its reflections pass reads.
        internal static bool LoadDeferred(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            string path = Path.Combine(folder, "deferredshaders");
            if (!File.Exists(path))
            {
                Log("no Deferred bundle at " + path + ": forward rendering");
                return false;
            }
            AssetBundle bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
            {
                Log("Deferred's bundle did not load: forward rendering");
                return false;
            }
            Shader shading = null, reflections = null;
            foreach (Shader shader in bundle.LoadAllAssets<Shader>())
            {
                if (shader.name == "Deferred/Internal-DeferredShading") shading = shader;
                if (shader.name == RtLighting.DeferredReflectionsName) reflections = shader;
            }
            if (shading == null || reflections == null)
            {
                Log("Deferred's lighting shaders are not in its bundle: forward rendering");
                return false;
            }
            GraphicsSettings.SetShaderMode(BuiltinShaderType.DeferredShading, BuiltinShaderMode.UseCustom);
            GraphicsSettings.SetCustomShader(BuiltinShaderType.DeferredShading, shading);
            GraphicsSettings.SetShaderMode(BuiltinShaderType.DeferredReflections, BuiltinShaderMode.UseCustom);
            GraphicsSettings.SetCustomShader(BuiltinShaderType.DeferredReflections, reflections);
            Shader.SetGlobalInt("useReflectionProbeOnCurrentCamera", 1);
            Shader.SetGlobalInt("disableProbeReflectionsOnCurrentCamera", 0);
            Shader.SetGlobalInt("useSSROnCurrentCamera", 0);
            Shader.SetGlobalFloat("deferredAmbientBrightness", 1f);
            Shader.SetGlobalFloat("deferredAmbientTint", 1f);
            Shader.SetGlobalVector("legacyAmbientColor", new Vector4(0.2f, 0.22f, 0.25f, 0f));
            Shader.SetGlobalMatrix("internalSpaceToWorld", Matrix4x4.identity);
            Log("Deferred's lighting shaders set from " + path);
            return true;
        }

        private void BuildScene()
        {
            QualitySettings.shadows = ShadowQuality.All;
            QualitySettings.shadowDistance = 25f;
            QualitySettings.shadowCascades = 1;
            QualitySettings.shadowResolution = ShadowResolution.High;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;
            RenderSettings.ambientSkyColor = new Color(0.4f, 0.5f, 0.7f);
            Material sky = Resources.Load<Material>("RtSky");
            if (sky != null) RenderSettings.skybox = sky;

            Material template = Resources.Load<Material>("RtStandard");
            Material Surface(Color colour)
            {
                var material = new Material(template);
                material.color = colour;
                material.SetFloat("_Glossiness", 0.2f);
                return material;
            }

            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.localScale = new Vector3(40f, 1f, 40f);
            ground.GetComponent<Renderer>().sharedMaterial = Surface(new Color(0.6f, 0.6f, 0.58f));
            Box(new Vector3(0f, 5f, 60f), new Vector3(10f, 10f, 10f), Surface(new Color(0.8f, 0.3f, 0.2f)));
            Box(new Vector3(-6f, 1f, -10f), new Vector3(2f, 2f, 2f), Surface(new Color(0.3f, 0.5f, 0.8f)));

            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.LookRotation(-towardsSun);
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 1f;
            light.intensity = 1f;

            probe = new GameObject("Probe").AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime;
            probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.size = new Vector3(1e6f, 1e6f, 1e6f);
            probe.resolution = 128;

            cam = new GameObject("Camera").AddComponent<Camera>();
            cam.transform.position = new Vector3(0f, 12f, -30f);
            cam.transform.LookAt(new Vector3(0f, 0f, 40f));
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 5000f;
            cam.renderingPath = deferred ? RenderingPath.DeferredShading : RenderingPath.Forward;
            // Deferred's LDR path keeps only surfaces whose emission alpha is 1, which its own
            // shaders write and Unity's standard shader does not: HDR, where it has no such pass.
            cam.allowHDR = deferredMod;
            cam.allowMSAA = false;

            raytracing = cam.gameObject.AddComponent<RtCamera>();
            raytracing.enabled = false;
            raytracing.Sun = () => new Vector4(towardsSun.x, towardsSun.y, towardsSun.z, 0.0047f);
            raytracing.SunColor = () => light.color * light.intensity;
            raytracing.SunLight = () => light;
            raytracing.SkyColor = () => new Color(0.4f, 0.5f, 0.7f);
            raytracing.Environment = () => probe.texture;
            raytracing.Scene.AddSource(new RtUnitySource());
            // As the game traces: before the lighting of the same frame; -late after it.
            raytracing.TraceBeforeLighting = Argument("-late") == null;

            // Eight places, two taken: the box and one far away; the rest beyond the count.
            gpuTransforms = new ComputeBuffer(8, 64, ComputeBufferType.Append);
            var places = new Matrix4x4[8];
            places[0] = Matrix4x4.TRS(gpuBox, Quaternion.identity, new Vector3(2f, 2f, 2f));
            places[1] = Matrix4x4.TRS(new Vector3(0f, 1f, 400f), Quaternion.identity, Vector3.one);
            for (int i = 2; i < 8; i++) places[i] = Matrix4x4.TRS(new Vector3(0f, 1f, -25f), Quaternion.identity, Vector3.one * 3f);
            gpuTransforms.SetData(places);
            gpuTransforms.SetCounterValue(2);
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh cubeMesh = cube.GetComponent<MeshFilter>().sharedMesh;
            Destroy(cube);
            raytracing.Scene.AddGpuSource(new RtGpuBoxes { Transforms = gpuTransforms, Mesh = cubeMesh, Material = template });
        }

        private static void Box(Vector3 centre, Vector3 size, Material material)
        {
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.transform.position = centre;
            box.transform.localScale = size;
            box.GetComponent<Renderer>().sharedMaterial = material;
        }

        private IEnumerator Run()
        {
            probe.RenderProbe();
            if (budget)
            {
                yield return Budget();
                Finish();
                yield break;
            }
            for (int i = 0; i < 30; i++) yield return null;
            Texture2D without = null;
            yield return Capture(t => without = t);

            raytracing.enabled = true;
            if (raytracing.Problem != null)
            {
                Check(false, "raytracing on", raytracing.Problem);
                Finish();
                yield break;
            }
            for (int i = 0; i < 90; i++) yield return null;
            Texture2D with = null;
            yield return Capture(t => with = t);


            Dump(raytracing.DepthTexture, "depth", 50f);
            Dump(raytracing.SunTexture, "sun", 1f);
            Dump(raytracing.NormalTexture, "normal", 1f);
            Dump(raytracing.DiffuseTexture, "diffuse", 1f);
            Dump(raytracing.EnvironmentTexture, "environment", 1f);
            Dump(raytracing.ForwardTexture, "forward", 1f);
            Log("signals 0x" + raytracing.LastSignals.ToString("X") + ", reflections replaced " + RtLighting.ReflectionsReplaced);
            if (probe.texture != null) Log("probe " + probe.texture.GetType().Name + " " + probe.texture.width);

            string status = raytracing.Status();
            Log("status " + status + ", signals 0x" + raytracing.LastSignals.ToString("X") + ", path " + cam.actualRenderingPath);
            Log("scene " + raytracing.Scene.InstanceCount + " instances, " + raytracing.Scene.MeshCount + " meshes, "
                + raytracing.Scene.MaterialCount + " materials, skipped " + raytracing.Scene.Skipped
                + (raytracing.Scene.LastSkip != null ? " (" + raytracing.Scene.LastSkip + ")" : ""));
            Log("timings sun " + raytracing.PassTimings[0].ToString("0.000") + " ms, occlusion "
                + raytracing.PassTimings[1].ToString("0.000") + ", diffuse " + raytracing.PassTimings[2].ToString("0.000")
                + ", specular " + raytracing.PassTimings[3].ToString("0.000") + ", denoiser "
                + raytracing.PassTimings[6].ToString("0.000"));

            // Where the boxes' shadows fall on the ground: their centres along the sun's ray.
            Vector3 ShadowOf(Vector3 centre)
            {
                return centre - towardsSun * (centre.y / towardsSun.y);
            }
            float Ratio(Texture2D image, Vector3 shaded, Vector3 lit)
            {
                return Brightness(image, shaded) / Mathf.Max(Brightness(image, lit), 1e-3f);
            }
            Vector3 near = ShadowOf(new Vector3(-6f, 1f, -10f));
            Vector3 nearLit = near + new Vector3(4f, 0f, 0f);
            float nearWithout = Ratio(without, near, nearLit);
            float nearWith = Ratio(with, near, nearLit);
            Check(nearWith < 0.95f, "the near box's shadow, raytraced",
                  (nearWith * 100f).ToString("0") + " % of the light beside it, where the shadow maps give "
                  + (nearWithout * 100f).ToString("0") + " %");

            Vector3 far = ShadowOf(new Vector3(0f, 5f, 60f));
            Vector3 farLit = far + new Vector3(15f, 0f, 0f);
            float farWith = Ratio(with, far, farLit);
            float farWithout = Ratio(without, far, farLit);
            float litChange = Brightness(with, farLit) / Mathf.Max(Brightness(without, farLit), 1e-3f);
            if (deferredMod)
                // Against the same two points without raytracing: the ground's own
                // brightness changes across them, the shadow is the drop on top of that.
                Check(farWith < 0.8f * farWithout, "the far box's shadow, beyond the shadow maps' 25 m",
                      (farWith * 100f).ToString("0") + " % of the light beside it, without raytracing "
                      + (farWithout * 100f).ToString("0") + " %");
            else
                Log("the far box's shadow at " + (farWith * 100f).ToString("0")
                    + " % of the light beside it: Unity fades shadows out at the shadow distance, Deferred does not");
            Check(litChange > 0.8f && litChange < 1.3f, "light where the sun reaches",
                  "a lit point at " + (litChange * 100f).ToString("0") + " % of its brightness without raytracing");
            Check(raytracing.Scene.InstanceCount >= 3, "the scene handed over",
                  raytracing.Scene.InstanceCount + " instances");
            // The GPU's box casts its shadow in the rays alone; the places beyond its
            // buffer's count cast none.
            Vector3 gpuShadow = ShadowOf(gpuBox);
            Vector3 gpuLit = gpuShadow + new Vector3(4f, 0f, 0f);
            float gpuWith = Ratio(with, gpuShadow, gpuLit);
            float gpuWithout = Ratio(without, gpuShadow, gpuLit);
            Vector3 beyond = ShadowOf(new Vector3(0f, 1f, -25f));
            float beyondWith = Ratio(with, beyond, beyond + new Vector3(4f, 0f, 0f));
            Check(gpuWith < 0.9f * gpuWithout && beyondWith > 0.9f, "instances from the GPU",
                  "a box only in the GPU's buffer shades the ground to " + (gpuWith * 100f).ToString("0") + " % (without raytracing "
                  + (gpuWithout * 100f).ToString("0") + " %); past its count, " + (beyondWith * 100f).ToString("0") + " %");
            if (deferredMod)
                Check(RtLighting.ReflectionsReplaced, "Deferred's reflections shader replaced",
                      "the raytraced diffuse light and reflections enter through it");
            yield return Uncovered(near, nearLit, nearWithout, nearWith);
            yield return CutoutCoverage();
            yield return SmallTextureRows();
            if (deferredMod)
            {
                yield return Moving(without);
                yield return Night();
            }
            else
                yield return AmbientReplaced(near);
            Finish();
        }

        // The camera gliding sideways while the world jumps as KSP's floating origin makes
        // it: everything moved by minus an offset, reported as the frame's shift. The far
        // box's shadow stays where it is, carried over the jump.
        private IEnumerator Moving(Texture2D without)
        {
            Vector3 shift = Vector3.zero;
            raytracing.WorldShift = () => new RtPosition(shift.x, shift.y, shift.z);
            Vector3 total = Vector3.zero;
            for (int i = 0; i < 40; i++)
            {
                shift = Vector3.zero;
                if (i == 38)
                {
                    // Just before the last frame: a jump of 600 m, as KSP's origin makes one.
                    shift = new Vector3(600f, 0f, -300f);
                    foreach (GameObject root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                        if (root != gameObject && root.GetComponent<Light>() == null)
                            root.transform.position -= shift;
                    total -= shift;
                }
                cam.transform.position += new Vector3(0.05f, 0f, 0f);
                yield return null;
            }
            Texture2D moved = null;
            yield return Capture(t => moved = t, "moved");
            Vector3 far = ShadowOfPublic(new Vector3(0f, 5f, 60f)) + total;
            Vector3 farLit = far + new Vector3(15f, 0f, 0f);
            float ratio = Brightness(moved, far) / Mathf.Max(Brightness(moved, farLit), 1e-3f);
            float before = Brightness(without, far - total) / Mathf.Max(Brightness(without, farLit - total), 1e-3f);
            Check(ratio < 0.8f * before, "the far shadow over a floating origin jump",
                  (ratio * 100f).ToString("0") + " % of the light beside it after the camera moved 2 m and the world 670 m");
        }

        // The sun under the horizon: the ground gets no sun and a dark sky. With the
        // raytraced light it must be as bright as KSP's own night light keeps it.
        private IEnumerator Night()
        {
            Light sun = FindObjectOfType<Light>();
            Vector3 below = new Vector3(0.3f, -0.4f, 0.74f).normalized;
            sun.transform.rotation = Quaternion.LookRotation(-below);
            raytracing.Sun = () => new Vector4(below.x, below.y, below.z, 0.0047f);
            RenderSettings.skybox = null;
            probe.RenderProbe();
            Vector3 ground = new Vector3(8f, 0f, 20f);
            raytracing.enabled = false;
            for (int i = 0; i < 20; i++) yield return null;
            Texture2D dark = null;
            yield return Capture(t => dark = t, "night_without_raytracing");
            raytracing.enabled = true;
            for (int i = 0; i < 60; i++) yield return null;
            Texture2D darkWith = null;
            yield return Capture(t => darkWith = t, "night_with_raytracing");
            float plain = Brightness(dark, ground);
            float traced = Brightness(darkWith, ground);
            Check(plain > 0.02f && traced > 0.8f * plain, "the night side not black",
                  "the ground at " + traced.ToString("0.000") + " with raytracing, " + plain.ToString("0.000")
                  + " with KSP's night light alone");
        }

        // A point the raytraced frame before did not see: from the sun's side the near box
        // hides its own shadow; turned back, the first frame finds no raytraced texel of
        // that ground and takes Unity's shadow maps, not the box's lit top in front of it.
        // The first frame after the camera turned from the sun's side, where the raytraced
        // frame before did not see the near box's shadow: the sun's shadow from the shadow
        // maps, not carried from the box's top it saw there (which left the shadow at 100 %);
        // the indirect light from the raytraced light beside it where the fill finds it,
        // else Unity's. Between what the shadow maps and the raytracing give, settled.
        private IEnumerator Uncovered(Vector3 shaded, Vector3 lit, float shadowMaps, float raytraced)
        {
            Vector3 position = cam.transform.position;
            Quaternion rotation = cam.transform.rotation;
            cam.transform.position = shaded + towardsSun * 14f;
            cam.transform.LookAt(shaded);
            for (int i = 0; i < 20; i++) yield return null;
            cam.transform.position = position;
            cam.transform.rotation = rotation;
            Texture2D shot = null;
            yield return Capture(t => shot = t, "uncovered");
            float ratio = Brightness(shot, shaded) / Mathf.Max(Brightness(shot, lit), 1e-3f);
            Check(ratio > Mathf.Min(shadowMaps, raytraced) - 0.04f && ratio < Mathf.Max(shadowMaps, raytraced) + 0.04f,
                  "ground the frame before did not see",
                  "the near box's shadow at " + (ratio * 100f).ToString("0") + " % of the light beside it in the first frame"
                  + " after the camera turned from the sun's side, between the shadow maps' "
                  + (shadowMaps * 100f).ToString("0") + " % and the raytracing's " + (raytraced * 100f).ToString("0") + " %");
            for (int i = 0; i < 20; i++) yield return null;
        }

        // Two cut-out cards in the sun, both covering a quarter: one in stripes 4 of 16
        // texels wide, which the small texture keeps as they are, one in stripes 1 of 4,
        // finer than its texels. The small texture keeps the share the cut-out covers,
        // not the averaged alpha (RtScene.ReadTexture), so both shade the ground alike.
        // Point-filtered, so that the cut-out covers exactly a quarter, and within the
        // shadow maps' 25 m, beyond which Unity's forward and deferred lighting show no
        // shadow at all.
        private IEnumerator CutoutCoverage()
        {
            Material template = Resources.Load<Material>("RtStandard");
            var cards = new List<GameObject>();
            Vector3[] centres = { new Vector3(-14f, 3f, -6f), new Vector3(-19f, 3f, -6f) };
            int[] periods = { 16, 4 };
            for (int c = 0; c < 2; c++)
            {
                var texture = new Texture2D(256, 256, TextureFormat.RGBA32, true);
                var pixels = new Color32[256 * 256];
                for (int y = 0; y < 256; y++)
                    for (int x = 0; x < 256; x++)
                        pixels[y * 256 + x] = new Color32(255, 255, 255, (byte)(x % periods[c] < periods[c] / 4 ? 255 : 0));
                texture.SetPixels32(pixels);
                texture.filterMode = FilterMode.Point;
                texture.Apply(true);
                var material = new Material(template) { mainTexture = texture, renderQueue = 2450 };
                material.EnableKeyword("_ALPHATEST_ON");
                material.SetFloat("_Cutoff", 0.25f);
                GameObject card = GameObject.CreatePrimitive(PrimitiveType.Quad);
                card.transform.position = centres[c];
                card.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                card.transform.localScale = new Vector3(4f, 4f, 1f);
                card.GetComponent<Renderer>().sharedMaterial = material;
                cards.Add(card);
            }
            for (int i = 0; i < 90; i++) yield return null;
            Texture2D shot = null;
            yield return Capture(t => shot = t, "cutout_coverage");
            float lit = Brightness(shot, new Vector3(0f, 0f, -8f), 20);
            float coarse = Brightness(shot, centres[0] - towardsSun * (3f / towardsSun.y), 20) / Mathf.Max(lit, 1e-3f);
            float fine = Brightness(shot, centres[1] - towardsSun * (3f / towardsSun.y), 20) / Mathf.Max(lit, 1e-3f);
            foreach (GameObject card in cards) Destroy(card);
            Check(coarse < 0.97f && Mathf.Abs(fine - coarse) < 0.03f, "cut-out finer than the small texture",
                  "the ground under a card a quarter covered in coarse stripes at " + (coarse * 100f).ToString("0")
                  + " % of the light beside it, in stripes finer than the small texture's texels at " + (fine * 100f).ToString("0") + " %");
            for (int i = 0; i < 20; i++) yield return null;
        }

        // A material's small texture as the scene reads it back (RtScene.ReadTexture,
        // AsyncGPUReadback) and packs it for the proxy: the first row the texture's top,
        // and a world-mapped one its average colour. The texture is red below, blue above.
        private IEnumerator SmallTextureRows()
        {
            int size = RtNative.MaterialTextureSize;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = i < pixels.Length / 2 ? new Color32(255, 0, 0, 255) : new Color32(0, 0, 255, 255);
            texture.SetPixels32(pixels);
            texture.Apply(false);
            var target = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            target.Create();
            Graphics.Blit(texture, target);
            var rows = new byte[size * size * 4];
            var average = new byte[size * size * 4];
            bool done = false, failed = false;
            AsyncGPUReadback.Request(target, 0, TextureFormat.RGBA32, request =>
            {
                done = true;
                failed = request.hasError;
                if (failed) return;
                RtScene.Pack(request.GetData<byte>(), rows, size, false);
                RtScene.Pack(request.GetData<byte>(), average, size, true);
            });
            for (int i = 0; i < 30 && !done; i++) yield return null;
            Destroy(texture);
            target.Release();
            Destroy(target);
            int last = (size * size - 1) * 4;
            Check(done && !failed && rows[2] > 200 && rows[0] < 50 && rows[last] > 200 && rows[last + 2] < 50,
                  "small texture's rows",
                  done ? "first texel " + rows[0] + "/" + rows[1] + "/" + rows[2] + " (blue expected), last "
                         + rows[last] + "/" + rows[last + 1] + "/" + rows[last + 2] + " (red expected)"
                       : "no readback within 30 frames");
            // Half full red, half full blue: 0.5 of each, as sRGB 188 (the proxy's array).
            Check(done && !failed && Mathf.Abs(average[0] - 188) <= 2 && Mathf.Abs(average[2] - 188) <= 2
                  && average[0] == average[last] && average[2] == average[last + 2] && average[1] < 3,
                  "world-mapped texture as its average",
                  "texels " + average[0] + "/" + average[1] + "/" + average[2] + " and " + average[last] + "/"
                  + average[last + 1] + "/" + average[last + 2]);
        }

        // Without Deferred: the raytraced light added to the frame takes the place of
        // Unity's ambient light. Where only indirect light reaches, in the near box's
        // shadow, a dim flat ambient light and a bright gradient one, as KSP's editors
        // have, differ without raytracing, and hardly with it.
        private IEnumerator AmbientReplaced(Vector3 shaded)
        {
            AmbientMode mode = RenderSettings.ambientMode;
            Color sky = RenderSettings.ambientSkyColor, equator = RenderSettings.ambientEquatorColor,
                  groundColour = RenderSettings.ambientGroundColor;
            float[] plain = new float[2], traced = new float[2], uncovered = new float[2];
            float[] behindPlain = new float[2], behindTraced = new float[2];
            // Ground just behind the near box: seen from the camera, hidden by the box from
            // a metre lower, where the box's edge is 19 % nearer and the ground beyond it
            // within a few texels.
            Vector3 behind = new Vector3(-7.2f, 0f, -4.6f);
            for (int pass = 0; pass < 2; pass++)
            {
                raytracing.enabled = pass == 1;
                for (int a = 0; a < 2; a++)
                {
                    if (a == 0)
                    {
                        RenderSettings.ambientMode = AmbientMode.Flat;
                        RenderSettings.ambientLight = new Color(0.1f, 0.1f, 0.1f);
                    }
                    else
                    {
                        RenderSettings.ambientMode = AmbientMode.Trilight;
                        RenderSettings.ambientSkyColor = new Color(0.45f, 0.45f, 0.45f);
                        RenderSettings.ambientEquatorColor = new Color(0.55f, 0.55f, 0.55f);
                        RenderSettings.ambientGroundColor = new Color(0.3f, 0.3f, 0.3f);
                    }
                    for (int i = 0; i < 60; i++) yield return null;
                    Texture2D shot = null;
                    yield return Capture(t => shot = t, "ambient_" + (a == 0 ? "dim" : "bright") + "_"
                                                        + (pass == 1 ? "with" : "without") + "_raytracing");
                    (pass == 1 ? traced : plain)[a] = Brightness(shot, shaded);
                    (pass == 1 ? behindTraced : behindPlain)[a] = Brightness(shot, behind);
                    if (pass == 1)
                    {
                        // The first frame after the camera rose by a metre, where the
                        // raytraced frame before did not see that ground: its light from
                        // the raytraced light beside it, not Unity's ambient light.
                        Vector3 position = cam.transform.position;
                        cam.transform.position = position - new Vector3(0f, 1f, 0f);
                        for (int i = 0; i < 20; i++) yield return null;
                        cam.transform.position = position;
                        yield return Capture(t => shot = t, "uncovered_ambient_" + (a == 0 ? "dim" : "bright"));
                        uncovered[a] = Brightness(shot, behind);
                        for (int i = 0; i < 20; i++) yield return null;
                    }
                }
            }
            RenderSettings.ambientMode = mode;
            RenderSettings.ambientSkyColor = sky;
            RenderSettings.ambientEquatorColor = equator;
            RenderSettings.ambientGroundColor = groundColour;
            float plainChange = plain[1] - plain[0];
            float tracedChange = Mathf.Abs(traced[1] - traced[0]);
            Check(plainChange > 0.05f && tracedChange < 0.1f * plainChange, "Unity's ambient light replaced, without Deferred",
                  "the near box's shadow " + tracedChange.ToString("0.000") + " apart between a dim flat and a bright"
                  + " gradient ambient light with raytracing (" + traced[0].ToString("0.000") + ", " + traced[1].ToString("0.000")
                  + "), " + plainChange.ToString("0.000") + " without (" + plain[0].ToString("0.000") + ", " + plain[1].ToString("0.000") + ")");
            float behindChange = behindPlain[1] - behindPlain[0];
            float uncoveredChange = Mathf.Abs(uncovered[1] - uncovered[0]);
            Check(behindChange > 0.05f && uncoveredChange < 0.2f * behindChange, "uncovered ground's light from the raytraced light beside it",
                  "the ground behind the near box in the first frame after the camera rose by a metre "
                  + uncoveredChange.ToString("0.000") + " apart between the two ambient lights (" + uncovered[0].ToString("0.000")
                  + ", " + uncovered[1].ToString("0.000") + "), settled " + Mathf.Abs(behindTraced[1] - behindTraced[0]).ToString("0.000")
                  + ", " + behindChange.ToString("0.000") + " without raytracing");
        }

        private Vector3 ShadowOfPublic(Vector3 centre)
        {
            return centre - towardsSun * (centre.y / towardsSun.y);
        }

        private void OnDestroy()
        {
            gpuTransforms?.Release();
            budgetTransforms?.Release();
        }

        // The budget (docs/development/raytracing.md, "Plan"): on the RTX 4090 at 2560 by
        // 1440 with DLSS Quality, which renders 1707 by 960, everything together within
        // 4 ms of GPU time. The scene: 400 m of rolling ground in 131 072 triangles as
        // terrain quads come near the camera, 400 boxes as a vessel's parts and the
        // buildings, 20 000 rocks from the GPU as Parallax's scatter; all four signals,
        // two bounces, the denoiser.
        private ComputeBuffer budgetTransforms;

        private IEnumerator Budget()
        {
            const int cells = 256;
            const float size = 400f;
            var vertices = new Vector3[(cells + 1) * (cells + 1)];
            var triangles = new int[cells * cells * 6];
            for (int z = 0; z <= cells; z++)
                for (int x = 0; x <= cells; x++)
                {
                    float px = (x / (float)cells - 0.5f) * size, pz = (z / (float)cells - 0.5f) * size;
                    vertices[z * (cells + 1) + x] = new Vector3(px, 1.5f * Mathf.Sin(px * 0.05f) * Mathf.Cos(pz * 0.07f) - 0.2f, pz);
                }
            int t = 0;
            for (int z = 0; z < cells; z++)
                for (int x = 0; x < cells; x++)
                {
                    int i = z * (cells + 1) + x;
                    triangles[t++] = i; triangles[t++] = i + cells + 1; triangles[t++] = i + 1;
                    triangles[t++] = i + 1; triangles[t++] = i + cells + 1; triangles[t++] = i + cells + 2;
                }
            var ground = new Mesh { name = "budget ground", indexFormat = IndexFormat.UInt32 };
            ground.vertices = vertices;
            ground.triangles = triangles;
            ground.RecalculateNormals();
            var groundObject = new GameObject("Budget ground");
            groundObject.AddComponent<MeshFilter>().sharedMesh = ground;
            Material template = Resources.Load<Material>("RtStandard");
            groundObject.AddComponent<MeshRenderer>().sharedMaterial = template;

            var random = new System.Random(7);
            for (int i = 0; i < 400; i++)
            {
                Vector3 at = new Vector3((float)random.NextDouble() * 160f - 80f, 1f, (float)random.NextDouble() * 160f - 40f);
                GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.transform.position = at;
                box.transform.localScale = Vector3.one * (0.5f + (float)random.NextDouble() * 2f);
                box.GetComponent<Renderer>().sharedMaterial = template;
            }

            // A buffer as large as Parallax's are, most of it empty.
            const int rocks = 20000;
            const int capacity = 100000;
            budgetTransforms = new ComputeBuffer(capacity, 64, ComputeBufferType.Append);
            var places = new Matrix4x4[capacity];
            for (int i = 0; i < rocks; i++)
            {
                Vector3 at = new Vector3((float)random.NextDouble() * 300f - 150f, 0.1f, (float)random.NextDouble() * 300f - 100f);
                places[i] = Matrix4x4.TRS(at, Quaternion.Euler(0f, (float)random.NextDouble() * 360f, 0f),
                                          Vector3.one * (0.2f + (float)random.NextDouble() * 0.6f));
            }
            budgetTransforms.SetData(places);
            budgetTransforms.SetCounterValue(rocks);
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh cubeMesh = cube.GetComponent<MeshFilter>().sharedMesh;
            Destroy(cube);
            raytracing.Scene.AddGpuSource(new RtGpuBoxes { Transforms = budgetTransforms, Mesh = cubeMesh, Material = template });

            raytracing.Settings = RtSettings.Full();
            raytracing.enabled = true;
            // Without the vertical sync, as the game keeps the GPU busy: held to the display's
            // refresh, the GPU idles most of each frame and its driver lowers the clocks (on the
            // RTX 4090 1.1 GHz and half the memory clock, against 2.76 GHz; each pass 1.5 to 3
            // times as long, measured). Three seconds for the GPU to leave its idle clocks, then
            // the median of 300 frames: other programs share the GPU, and a mean takes their
            // spikes.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            float warmUntil = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < warmUntil) yield return null;
            var sums = new float[8];
            const int samples = 300;
            var builds = new List<float>();
            var traces = new List<float>();
            var totals = new List<float>();
            for (int i = 0; i < samples; i++)
            {
                yield return null;
                string text = raytracing.Status();
                float frameBuild = Number(text, "build="), frameTrace = Number(text, "trace=");
                builds.Add(frameBuild);
                traces.Add(frameTrace);
                totals.Add(frameBuild + frameTrace);
                for (int p = 0; p < 8; p++) sums[p] += raytracing.PassTimings[p];
            }
            builds.Sort();
            traces.Sort();
            totals.Sort();
            float build = builds[samples / 2];
            float trace = traces[samples / 2];
            float total = totals[samples / 2];
            Log("budget at " + Screen.width + "x" + Screen.height + ": " + raytracing.Scene.InstanceCount + " instances and "
                + raytracing.Scene.GpuTotal + " from the GPU; GPU ms: scene " + build.ToString("0.00") + ", sun "
                + (sums[0] / samples).ToString("0.00") + ", diffuse " + (sums[2] / samples).ToString("0.00") + ", reflections "
                + (sums[3] / samples).ToString("0.00") + ", forward light " + (sums[4] / samples).ToString("0.00")
                + ", denoiser " + (sums[6] / samples).ToString("0.00") + "; " + raytracing.Status());
            Check(total > 0f && total < 4f, "the budget", "everything together " + total.ToString("0.00") + " ms of GPU time");
            Check(raytracing.Scene.GpuTotal >= rocks && raytracing.Scene.GpuTotal < rocks * 3 / 2, "places for the GPU's instances",
                  raytracing.Scene.GpuTotal + " places handed over for " + rocks + " rocks in a buffer of " + capacity);
        }

        // The number after key in the service's status line, as its text gives it.
        private static float Number(string text, string key)
        {
            int at = text.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return 0f;
            at += key.Length;
            int end = at;
            while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) end++;
            float value;
            return float.TryParse(text.Substring(at, end - at), System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out value) ? value : 0f;
        }

        private void Finish()
        {
            Log(failures == 0 ? "all checks passed" : failures + " check(s) failed");
            Application.Quit(failures == 0 ? 0 : 1);
        }

        private IEnumerator Capture(Action<Texture2D> done, string label = null)
        {
            yield return new WaitForEndOfFrame();
            Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
            string name = label ?? (raytracing.enabled ? "with_raytracing" : "without_raytracing");
            File.WriteAllBytes(Path.Combine(output, (deferred ? "deferred_" : "forward_") + name + ".png"), shot.EncodeToPNG());
            done(shot);
        }

        // A render texture as a PNG, its values times scale; the first row of the texture
        // at the image's top.
        private void Dump(RenderTexture texture, string name, float scale)
        {
            if (texture == null) return;
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = texture;
            var read = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false, true);
            read.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
            RenderTexture.active = previous;
            Color[] pixels = read.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                Color c = pixels[i];
                pixels[i] = new Color(Mathf.Clamp01(c.r * scale), Mathf.Clamp01(c.g * scale), Mathf.Clamp01(c.b * scale), 1f);
            }
            var image = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            image.SetPixels(pixels);
            File.WriteAllBytes(Path.Combine(output, (deferred ? "deferred_" : "forward_") + "rt_" + name + ".png"),
                               image.EncodeToPNG());
            Destroy(read);
            Destroy(image);
        }

        // The mean brightness of a 5x5 pixel patch where a world point shows.
        private float Brightness(Texture2D image, Vector3 world, int reach = 2)
        {
            Vector3 screen = cam.WorldToScreenPoint(world);
            int x = Mathf.RoundToInt(screen.x * image.width / Screen.width);
            int y = Mathf.RoundToInt(screen.y * image.height / Screen.height);
            float sum = 0f;
            for (int dy = -reach; dy <= reach; dy++)
                for (int dx = -reach; dx <= reach; dx++)
                {
                    Color c = image.GetPixel(Mathf.Clamp(x + dx, 0, image.width - 1), Mathf.Clamp(y + dy, 0, image.height - 1));
                    sum += (c.r + c.g + c.b) / 3f;
                }
            return sum / ((2 * reach + 1) * (2 * reach + 1));
        }
    }
}
