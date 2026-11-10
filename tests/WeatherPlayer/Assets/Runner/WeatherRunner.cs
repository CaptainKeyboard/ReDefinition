using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ReDefinition.Proxy;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // The weather player (tools/build_weather_player.py): a fake plain with a slope and a ridge under a fake overcast sky, a
    // storm cell of the size and place of the game's stored frames (radius 6 km, base 600 m above the ground), drawn by ReDefinition's
    // own cloud slab and the storm curtain, and a camera at the four views of the spike (25 km, 8 km, inside the cell at the
    // ground, inside the slab). Its lines in the log start with WEATHERPLAYER. Modes (option -mode):
    //
    //   stills  each view without the cell, with the curtain, with the slab, and with both, as 1280x720 frames (colour, depth,
    //           motion) in -out, for tools/analyze_weather_player.py; with -sweep name=a,b,c the slab's field name takes each value
    //   time    the GPU milliseconds of the slab's march, its composite and the curtain per view, with the steps and the slab's
    //           render scale varied, at the render size of DLSS Quality at 2560x1440 and at 2560x1440 itself
    //   turn    the camera turns at the 25 km view (2 degrees a frame, then 12) as the upscaler -upscale (native, fsr3, dlss, amd,
    //           xess) renders it at -scale, with its references; the analytic motion and depth of the slab are on unless -analytic 0
    //
    // Common options: -view far|mid|inside|slabin, -set name=value;name=value (CellSettings), -slab 0|1, -curtain 0|1.
    public sealed class WeatherRunner : MonoBehaviour
    {
        private const int Width = 1280, Height = 720;
        private static readonly string[] Views = { "far", "mid", "inside", "slabin" };

        private string output, mode, viewName = "far";
        private Camera cam;
        private WeatherSlab slab;
        private WeatherPass pass;
        private RenderTexture target;
        private Vector2Int display = new Vector2Int(Width, Height), render = new Vector2Int(Width, Height);
        private string upscaler;
        private float pitchDown = 4.95f;
        private Vector3 position;

        private static void Log(string text)
        {
            Debug.Log("WEATHERPLAYER " + text);
        }

        // A metric line: {name,value,limit,pass}; limit "-" and pass "info" where the metric has none.
        private static void Metric(string name, float value, string limit, string pass)
        {
            Log("{" + name + "," + value.ToString("0.####", CultureInfo.InvariantCulture) + "," + limit + "," + pass + "}");
        }

        private void Start()
        {
            mode = WeatherPass.Argument("-mode") ?? "stills";
            viewName = WeatherPass.Argument("-view") ?? "far";
            upscaler = WeatherPass.Argument("-upscale");
            output = WeatherPass.Argument("-out") ?? ".";
            Directory.CreateDirectory(output);
            if (mode == "turn" && upscaler != null && upscaler != "native")
            {
                float scale = float.Parse(WeatherPass.Argument("-scale") ?? "0.5", CultureInfo.InvariantCulture);
                render = new Vector2Int(Mathf.RoundToInt(Width * scale), Mathf.RoundToInt(Height * scale));
            }
            if (mode == "time")
            {
                // DLSS Quality at 2560x1440 renders 1707x960; the second size is the display's own.
                display = render = new Vector2Int(Argument("-width", 1707), Argument("-height", 960));
            }
            if (mode == "time") WeatherGpuTimer.Start();
            Log(mode + ", " + SystemInfo.graphicsDeviceName + ", " + SystemInfo.graphicsDeviceVersion);
            BuildScene();
            StartCoroutine(mode == "turn" ? Turn() : mode == "time" ? Time_() : Stills());
        }

        private static int Argument(string name, int fallback)
        {
            string v = WeatherPass.Argument(name);
            return v == null ? fallback : int.Parse(v, CultureInfo.InvariantCulture);
        }

        private static Texture2D Checker(Color a, Color b)
        {
            var texture = new Texture2D(64, 64, TextureFormat.RGBA32, true);
            var pixels = new Color32[64 * 64];
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                    pixels[y * 64 + x] = ((x / 32 + y / 32) & 1) == 0 ? (Color32)a : (Color32)b;
            texture.SetPixels32(pixels);
            texture.Apply(true);
            texture.wrapMode = TextureWrapMode.Repeat;
            return texture;
        }

        // The scene: a plain, a hill and a ridge, the sky, the sun, and the camera with the slab and the pass.
        private void BuildScene()
        {
            QualitySettings.shadows = ShadowQuality.Disable;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.30f, 0.32f, 0.36f);

            var sky = new Material(Shader.Find("Hidden/ReDefinition/WeatherPlayerSky"));
            sky.SetColor("_Horizon", new Color(0.60f, 0.63f, 0.64f));
            sky.SetColor("_Zenith", new Color(0.575f, 0.585f, 0.595f));
            sky.SetFloat("_Curve", 0.6f);
            RenderSettings.skybox = sky;

            Material template = Resources.Load<Material>("WeatherStandard");
            Material Surface(Color colour, float tiling)
            {
                var material = new Material(template) { color = colour };
                material.SetFloat("_Glossiness", 0.05f);
                material.mainTexture = Checker(Color.white, new Color(0.82f, 0.82f, 0.82f));
                material.mainTextureScale = new Vector2(tiling, tiling);
                return material;
            }
            GameObject Thing(PrimitiveType type, Vector3 at, Vector3 scale, Material material)
            {
                GameObject thing = GameObject.CreatePrimitive(type);
                thing.transform.position = at;
                thing.transform.localScale = scale;
                thing.GetComponent<Renderer>().sharedMaterial = material;
                return thing;
            }
            // A plain 100 km wide (a Plane is 10 m), checkered with 500 m squares; a hill and a ridge for the slope.
            Thing(PrimitiveType.Plane, Vector3.zero, new Vector3(10000f, 1f, 10000f), Surface(new Color(0.34f, 0.36f, 0.33f), 100f));
            Thing(PrimitiveType.Sphere, new Vector3(-9000f, -150f, 4000f), new Vector3(6000f, 700f, 5000f), Surface(new Color(0.40f, 0.41f, 0.39f), 6f));
            Thing(PrimitiveType.Cube, new Vector3(15000f, 120f, 9000f), new Vector3(24000f, 320f, 700f), Surface(new Color(0.42f, 0.42f, 0.42f), 40f));

            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            var settings = new CellSettings();
            light.transform.rotation = Quaternion.LookRotation(-settings.TowardsSun);
            light.shadows = LightShadows.None;
            light.intensity = 0.8f;

            target = new RenderTexture(render.x, render.y, 24, RenderTextureFormat.DefaultHDR) { name = "weather target" };
            target.Create();
            cam = new GameObject("Camera").AddComponent<Camera>();
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 1f;
            cam.farClipPlane = 120000f;
            cam.renderingPath = RenderingPath.Forward;
            cam.allowHDR = true;
            cam.allowMSAA = false;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.targetTexture = target;
            cam.depthTextureMode = DepthTextureMode.Depth | DepthTextureMode.MotionVectors;

            slab = cam.gameObject.AddComponent<WeatherSlab>();
            slab.Init(cam);
            if (slab.Problem != null) Log("FAIL  " + slab.Problem);
            slab.SlabOn = WeatherPass.Argument("-slab") != "0";
            slab.CurtainOn = WeatherPass.Argument("-curtain") != "0";
            ApplySet(WeatherPass.Argument("-set"));
            pass = cam.gameObject.AddComponent<WeatherPass>();
            pass.Init(cam, slab, output, upscaler, display, render);

            var screen = new GameObject("Screen").AddComponent<Camera>();
            screen.clearFlags = CameraClearFlags.Nothing;
            screen.cullingMask = 0;
            screen.depth = 10;
            screen.gameObject.AddComponent<WeatherShow>().Source = target;
            SetView(viewName);
        }

        private void ApplySet(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (string pair in text.Split(';'))
            {
                string[] kv = pair.Split('=');
                float v;
                if (kv.Length != 2 || !float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v) || !slab.Cell.Set(kv[0], v))
                    Log("FAIL  -set " + pair + " is not a field");
            }
        }

        // The camera's place for a view; its look is set by Pose.
        private void SetView(string name)
        {
            viewName = name;
            pitchDown = 4.95f;
            switch (name)
            {
                case "mid": position = new Vector3(0f, 2f, -8000f); break;
                case "inside": position = new Vector3(0f, 2f, 0f); break;
                case "slabin":
                    position = new Vector3(0f, slab.Cell.BaseM + 0.4f * (slab.Cell.TopM - slab.Cell.BaseM), -3000f);
                    pitchDown = 0f;
                    break;
                default: position = new Vector3(0f, 2f, -25000f); break;
            }
            // From inside the cell at the ground the game's storm brings its fog (exponential squared, 60 m of visibility).
            RenderSettings.fog = name == "inside";
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.02887f;
            RenderSettings.fogColor = new Color(0.34f, 0.36f, 0.40f);
            Pose(0f);
        }

        private void Pose(float yaw)
        {
            cam.transform.position = position;
            cam.transform.rotation = Quaternion.Euler(pitchDown, yaw, 0f);
        }

        // Counts the frames of a wait.
        private static IEnumerator Frames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }

        // One capture: the view with the variant's switches, settled for a few frames, written as name_*.bin.
        private IEnumerator Capture(string name, bool slabOn, bool curtainOn)
        {
            slab.SlabOn = slabOn;
            slab.CurtainOn = curtainOn;
            pass.Reset();
            yield return Frames(6);
            pass.Capture = name;
            yield return null;
            while (pass.Pending > 0) yield return null;
        }

        private IEnumerator Stills()
        {
            string sweep = WeatherPass.Argument("-sweep");
            string sweepName = null;
            string[] sweepValues = null;
            if (sweep != null)
            {
                string[] kv = sweep.Split('=');
                sweepName = kv[0];
                sweepValues = kv[1].Split(',');
            }
            string only = WeatherPass.Argument("-views");
            foreach (string view in only != null ? only.Split(',') : Views)
            {
                SetView(view);
                // Fog belongs to the view; the clear frame has none, as in the game's stored clear frames.
                bool fog = RenderSettings.fog;
                RenderSettings.fog = false;
                yield return Capture(view + "_clear", false, false);
                RenderSettings.fog = fog;
                if (sweepName == null)
                {
                    yield return Capture(view + "_curtain", false, true);
                    yield return Capture(view + "_slab", true, false);
                    yield return Capture(view + "_both", true, true);
                }
                else
                {
                    foreach (string value in sweepValues)
                    {
                        slab.Cell.Set(sweepName, float.Parse(value, CultureInfo.InvariantCulture));
                        yield return Capture(view + "_slab_" + sweepName + value, true, false);
                        yield return Capture(view + "_both_" + sweepName + value, true, true);
                    }
                }
            }
            Finish(false);
        }

        // The GPU time of each pass per view, with the steps and the slab's scale varied.
        private IEnumerator Time_()
        {
            if (!WeatherGpuTimer.Start())
            {
                Log("FAIL  the proxy's GPU timestamps are not available (is dxgi.dll beside the player with enabled=1?)");
                Finish(true);
                yield break;
            }
            string only = WeatherPass.Argument("-views");
            var segments = new float[8];
            foreach (string view in only != null ? only.Split(',') : Views)
            {
                SetView(view);
                // The reference without any cell, then the curtain alone, then the slab at each setting.
                yield return Measure(view, "base", false, false, segments);
                yield return Measure(view, "curtain", false, true, segments);
                foreach (float scale in new[] { 1f, 0.5f })
                    foreach (float steps in new[] { 16f, 24f, 32f, 48f, 64f })
                    {
                        slab.Cell.Scale = scale;
                        slab.Cell.Steps = steps;
                        yield return Measure(view, "slab.s" + steps.ToString("0") + ".r" + scale.ToString("0.##", CultureInfo.InvariantCulture),
                                             true, false, segments);
                    }
                slab.Cell.Scale = 0.5f;
                slab.Cell.Steps = 32f;
                yield return Measure(view, "both.s32.r0.5", true, true, segments);
            }
            Finish(false);
        }

        // Warms up, then averages 150 frames of the proxy's segments and prints them.
        private IEnumerator Measure(string view, string label, bool slabOn, bool curtainOn, float[] segments)
        {
            slab.SlabOn = slabOn;
            slab.CurtainOn = curtainOn;
            yield return Frames(40);
            double frame = 0, march = 0, composite = 0, curtain = 0;
            int n = 0;
            while (n < 150)
            {
                yield return null;
                float total;
                int number;
                if (!WeatherGpuTimer.Read(segments, out total, out number)) continue;
                frame += total;
                if (segments[WeatherGpuTimer.SlabMarched - WeatherGpuTimer.SlabStart] >= 0f) march += segments[WeatherGpuTimer.SlabMarched - WeatherGpuTimer.SlabStart];
                if (segments[WeatherGpuTimer.SlabComposited - WeatherGpuTimer.SlabStart] >= 0f) composite += segments[WeatherGpuTimer.SlabComposited - WeatherGpuTimer.SlabStart];
                if (segments[WeatherGpuTimer.CurtainEnd - WeatherGpuTimer.SlabStart] >= 0f) curtain += segments[WeatherGpuTimer.CurtainEnd - WeatherGpuTimer.SlabStart];
                n++;
            }
            string size = "." + render.x + "x" + render.y;
            Metric("gpu_ms.frame." + view + "." + label + size, (float)(frame / n), "-", "info");
            if (slabOn)
            {
                Metric("gpu_ms.slab_march." + view + "." + label + size, (float)(march / n), "-", "info");
                Metric("gpu_ms.slab_composite." + view + "." + label + size, (float)(composite / n), "-", "info");
                Metric("gpu_ms.slab_total." + view + "." + label + size, (float)((march + composite) / n), "1.5", ((march + composite) / n) <= 1.5 ? "true" : "false");
            }
            if (curtainOn) Metric("gpu_ms.curtain." + view + "." + label + size, (float)(curtain / n), "-", "info");
        }

        // The camera turning at the view as the upscaler renders it, then the references of each pose.
        private IEnumerator Turn()
        {
            slab.SlabOn = WeatherPass.Argument("-slab") != "0";
            slab.CurtainOn = WeatherPass.Argument("-curtain") != "0";
            var path = new List<float>();
            float yaw = -26f;
            for (int i = 0; i < 6; i++) path.Add(yaw);
            for (int i = 0; i < 26; i++) path.Add(yaw += 2f);
            for (int i = 0; i < 2; i++) path.Add(yaw -= 12f);
            for (int i = 0; i < 30; i++) path.Add(yaw);

            Pose(path[0]);
            pass.ResetUpscaler = true;
            yield return Frames(120);
            for (int i = 0; i < path.Count; i++)
            {
                Pose(path[i]);
                pass.Capture = "turn_" + i.ToString("000", CultureInfo.InvariantCulture);
                yield return null;
            }

            // The references at twice the display size, without jitter or upscaler.
            RenderTexture referenceTarget = new RenderTexture(display.x * 2, display.y * 2, 24, RenderTextureFormat.DefaultHDR);
            referenceTarget.Create();
            cam.targetTexture = referenceTarget;
            pass.SetJitter(false);
            for (int i = 0; i < path.Count; i += (i >= 6 && i < 40) ? 1 : 3)
            {
                Pose(path[i]);
                pass.ResetUpscaler = true;
                int turnFrame;
                pass.FrameOverride = pass.FrameOf.TryGetValue("turn_" + i.ToString("000", CultureInfo.InvariantCulture), out turnFrame) ? turnFrame : -1;
                yield return Frames(60);
                pass.Capture = "ref_" + i.ToString("000", CultureInfo.InvariantCulture);
                yield return null;
            }
            float waited = 0f;
            while (pass.Pending > 0 && waited < 20f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            File.WriteAllText(Path.Combine(output, "meta.txt"), pass.Meta.ToString());
            File.WriteAllText(Path.Combine(output, "path.txt"), string.Join("\n", path.ConvertAll(
                p => p.ToString("0.###", CultureInfo.InvariantCulture)).ToArray()));
            Log("turn written: " + pass.Written + " frames, " + viewName + ", " + (upscaler ?? "native") + " at " + render.x + "x" + render.y
                + (pass.Upscaling ? ": " + pass.UpscalerState() : ""));
            string failure = pass.Failure();
            Log(failure == null ? "all checks passed" : "FAIL  " + failure);
            Finish(false);
        }

        private void Finish(bool failed)
        {
            if (mode != "turn")
            {
                File.WriteAllText(Path.Combine(output, "meta.txt"), pass.Meta.ToString());
                Log(failed ? "FAIL  run failed" : "all checks passed");
            }
            Application.Quit();
        }
    }

    // Shows the camera's target in the window.
    internal sealed class WeatherShow : MonoBehaviour
    {
        internal RenderTexture Source;

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            Graphics.Blit(Source, destination);
        }
    }
}
