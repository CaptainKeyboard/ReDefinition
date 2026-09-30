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

    // The raytracing test player's run (docs/development/raytracing.md, "Testing
    // without the game"). It builds a scene, renders it without raytracing and with
    // it, and checks the images: a shadow the shadow maps do not reach, one they do,
    // light that does not change. Its lines start with RTPLAYER in the player's log;
    // the images go to -out.
    //
    //   RaytracingPlayer.exe -logFile player.log -out <folder> [-deferred <folder with
    //     Deferred's "deferredshaders" bundle>]
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

        private void Start()
        {
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
        private bool LoadDeferred(string folder)
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
            raytracing.SkyColor = () => new Color(0.4f, 0.5f, 0.7f);
            raytracing.Environment = () => probe.texture;
            raytracing.Scene.AddSource(new RtUnitySource());
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
            Check(nearWith < 0.95f && Mathf.Abs(nearWith - nearWithout) < 0.1f, "the near box's shadow, raytraced",
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
            if (deferredMod)
                Check(RtLighting.ReflectionsReplaced, "Deferred's reflections shader replaced",
                      "the raytraced diffuse light and reflections enter through it");
            if (deferredMod)
            {
                yield return Moving(without);
                yield return Night();
            }
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

        private Vector3 ShadowOfPublic(Vector3 centre)
        {
            return centre - towardsSun * (centre.y / towardsSun.y);
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
        private float Brightness(Texture2D image, Vector3 world)
        {
            Vector3 screen = cam.WorldToScreenPoint(world);
            int x = Mathf.RoundToInt(screen.x * image.width / Screen.width);
            int y = Mathf.RoundToInt(screen.y * image.height / Screen.height);
            float sum = 0f;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    Color c = image.GetPixel(Mathf.Clamp(x + dx, 0, image.width - 1), Mathf.Clamp(y + dy, 0, image.height - 1));
                    sum += (c.r + c.g + c.b) / 3f;
                }
            return sum / 25f;
        }
    }
}
