using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ReDefinition.Bridges;
using ReDefinition.Raytracing;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.RaytracingPlayer
{
    // The camera turning around a rocket on its pad, as in the game's recordings, for
    // tools/analyze_rt_turn.py: each frame of the turn as it comes out, and the same
    // poses after the camera stood still, as references.
    //
    //   RaytracingPlayer.exe -turn <rr|denoiser|plain> -out <folder> [-jitter 1]
    //     [-deferred <folder with Deferred's "deferredshaders" bundle>]
    //
    //   RaytracingPlayer.exe -turn plain -upscale <fsr3|dlss|amd|native> -scale <render
    //     scale, 0.5 for half the display's width> -out <folder> [-bundle <shader bundle>]
    //
    // -upscale renders at the render size with the upscaler's jitter and keeps the
    // upscaled frame (RtTurnUpscale.cs); its references are rendered at twice the display
    // size without jitter, for tools/analyze_upscale_turn.py. native renders at the
    // display size without jitter or upscaler.
    //
    // -jitter offsets the projection each frame as the upscaler does (CameraRedirect,
    // UpscalerRig: FSR's Halton sequence, 8 phases at the render size); -deferred renders
    // with Deferred's lighting shaders, as KSP with Deferred does.
    //
    // rr: raytraced, through DLSS Ray Reconstruction as the game's upscaler sends it
    // (UpscalerRig.Reconstruction.cs), at the render size (DLAA); denoiser: raytraced,
    // ReDefinition's denoiser, no upscaler; plain: Unity's lighting alone. Each frame
    // writes its colour (RGBA half), its depth (Direct3D's z, float) and its camera
    // into the folder; meta.txt lists them.
    public sealed class RtTurnRunner : MonoBehaviour
    {
        // The display size, the circle the camera turns on and its eye height, the point it looks at, and the
        // direction towards the sun.
        private const int Width = 1280;
        private const int Height = 720;
        private const float Radius = 18f;
        private const float EyeHeight = 6f;
        private static readonly Vector3 Target = new Vector3(0f, 6f, 0f);
        private readonly Vector3 towardsSun = new Vector3(0.3f, 0.6f, 0.74f).normalized;

        // The output folder, the -turn mode, jitter, whether Deferred is loaded, the upscaler and its render size;
        // the camera, its raytracing, the reflection probe, the camera's target and the turn's pass.
        private string output;
        private string mode;
        private bool jitter;
        private bool deferredMod;
        private string upscaler;
        private Vector2Int renderSize = new Vector2Int(Width, Height);
        private Camera cam;
        private RtCamera raytracing;
        private ReflectionProbe probe;
        private RenderTexture target;
        private RtTurnPass pass;

        // The value after the named command-line argument, or null without one.
        internal static string Argument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        // A line in the player's log, prefixed RTPLAYER for the tools that read it.
        private static void Log(string text)
        {
            Debug.Log("RTPLAYER " + text);
        }

        // Reads the arguments, builds the scene and starts the run.
        private void Start()
        {
            mode = Argument("-turn");
            jitter = Argument("-jitter") != null;
            upscaler = Argument("-upscale");
            if (upscaler != null && upscaler != "native")
            {
                float scale = float.Parse(Argument("-scale") ?? "0.5", CultureInfo.InvariantCulture);
                renderSize = new Vector2Int(Mathf.RoundToInt(Width * scale), Mathf.RoundToInt(Height * scale));
            }
            deferredMod = RtPlayerRunner.LoadDeferred(Argument("-deferred"));
            output = Argument("-out") ?? Path.Combine(Application.dataPath, "..");
            Directory.CreateDirectory(output);
            Log("turn " + mode + ", " + SystemInfo.graphicsDeviceName);
            BuildScene();
            StartCoroutine(Run());
        }

        // A checkerboard texture of the two colours, cells squares a side, mipmapped and repeating.
        private static Texture2D Checker(Color a, Color b, int cells)
        {
            var texture = new Texture2D(256, 256, TextureFormat.RGBA32, true);
            var pixels = new Color32[256 * 256];
            int cell = 256 / cells;
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                    pixels[y * 256 + x] = ((x / cell + y / cell) & 1) == 0 ? (Color32)a : (Color32)b;
            texture.SetPixels32(pixels);
            texture.Apply(true);
            texture.wrapMode = TextureWrapMode.Repeat;
            return texture;
        }

        // The scene: grass and a concrete pad in squares, a banded rocket with boosters, a pole and buildings, the
        // sun and a reflection probe, and the camera on its target with the raytracing and the turn's pass.
        private void BuildScene()
        {
            QualitySettings.shadows = ShadowQuality.All;
            QualitySettings.shadowDistance = 150f;
            QualitySettings.shadowCascades = 4;
            QualitySettings.shadowResolution = ShadowResolution.VeryHigh;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.12f, 0.13f, 0.15f);
            Material sky = Resources.Load<Material>("RtSky");
            if (sky != null) RenderSettings.skybox = sky;
            Material template = Resources.Load<Material>("RtStandard");

            Material Surface(Color colour, Texture texture, float tiling)
            {
                var material = new Material(template) { color = colour };
                material.SetFloat("_Glossiness", 0.2f);
                if (texture != null)
                {
                    material.mainTexture = texture;
                    material.mainTextureScale = new Vector2(tiling, tiling);
                }
                return material;
            }

            GameObject Thing(PrimitiveType type, Vector3 position, Vector3 scale, Material material)
            {
                GameObject thing = GameObject.CreatePrimitive(type);
                thing.transform.position = position;
                thing.transform.localScale = scale;
                thing.GetComponent<Renderer>().sharedMaterial = material;
                return thing;
            }

            // Grass and a pad of concrete in squares, so that a smear shows.
            Thing(PrimitiveType.Plane, Vector3.zero, new Vector3(60f, 1f, 60f),
                  Surface(new Color(0.45f, 0.6f, 0.3f), Checker(Color.white, new Color(0.7f, 0.7f, 0.7f), 8), 60f));
            Thing(PrimitiveType.Cube, new Vector3(0f, 0.1f, 0f), new Vector3(24f, 0.2f, 24f),
                  Surface(new Color(0.65f, 0.65f, 0.63f), Checker(Color.white, new Color(0.55f, 0.55f, 0.55f), 4), 1f));
            // The rocket: 20 m, banded.
            Thing(PrimitiveType.Cylinder, new Vector3(0f, 10.2f, 0f), new Vector3(2.4f, 10f, 2.4f),
                  Surface(new Color(0.85f, 0.85f, 0.85f), Checker(Color.white, new Color(0.25f, 0.25f, 0.25f), 2), 4f));
            for (int i = 0; i < 4; i++)
            {
                float angle = i * Mathf.PI / 2f;
                Thing(PrimitiveType.Cylinder, new Vector3(Mathf.Cos(angle) * 1.8f, 5.2f, Mathf.Sin(angle) * 1.8f),
                      new Vector3(1.2f, 5f, 1.2f), Surface(new Color(0.8f, 0.8f, 0.8f), null, 1f));
            }
            // A flag pole and buildings in the distance.
            Thing(PrimitiveType.Cylinder, new Vector3(9f, 7f, 5f), new Vector3(0.2f, 7f, 0.2f), Surface(Color.grey, null, 1f));
            Thing(PrimitiveType.Cube, new Vector3(-60f, 20f, 70f), new Vector3(30f, 40f, 30f),
                  Surface(new Color(0.6f, 0.62f, 0.66f), Checker(Color.white, new Color(0.3f, 0.3f, 0.35f), 8), 2f));
            Thing(PrimitiveType.Cube, new Vector3(70f, 8f, 60f), new Vector3(50f, 16f, 25f),
                  Surface(new Color(0.75f, 0.72f, 0.65f), Checker(Color.white, new Color(0.4f, 0.4f, 0.4f), 8), 3f));

            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.LookRotation(-towardsSun);
            light.shadows = LightShadows.Soft;
            light.intensity = 1.1f;

            probe = new GameObject("Probe").AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime;
            probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.size = new Vector3(1e6f, 1e6f, 1e6f);
            probe.resolution = 128;

            target = new RenderTexture(renderSize.x, renderSize.y, 24, RenderTextureFormat.DefaultHDR) { name = "turn target" };
            target.Create();
            cam = new GameObject("Camera").AddComponent<Camera>();
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 5000f;
            cam.renderingPath = deferredMod ? RenderingPath.DeferredShading : RenderingPath.Forward;
            cam.allowHDR = true;
            cam.allowMSAA = false;
            cam.targetTexture = target;
            cam.depthTextureMode = DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
            Pose(0f);

            if (mode != "plain")
            {
                raytracing = cam.gameObject.AddComponent<RtCamera>();
                raytracing.Sun = () => new Vector4(towardsSun.x, towardsSun.y, towardsSun.z, 0.0047f);
                raytracing.SunColor = () => light.color * light.intensity;
                raytracing.SunLight = () => light;
                raytracing.SkyColor = () => new Color(0.4f, 0.5f, 0.7f);
                raytracing.Environment = () => probe.texture;
                raytracing.MotionVectors = true;
                // -early: the trace before the lighting of the same frame (RtCamera.TraceBeforeLighting).
                raytracing.TraceBeforeLighting = Argument("-early") != null;
                raytracing.Scene.AddSource(new RtUnitySource());
            }
            pass = cam.gameObject.AddComponent<RtTurnPass>();
            pass.Init(cam, raytracing, mode == "rr", jitter);
            if (upscaler != null) pass.InitUpscaler(upscaler, new Vector2Int(Width, Height), renderSize);
            if (raytracing != null)
            {
                raytracing.RenderedProjection = () => pass.RenderedProjection;
                raytracing.UnjitteredProjection = () => pass.UnjitteredProjection;
                raytracing.Jitter = () => pass.JitterPixels;
            }

            // The window shows the frame.
            var screen = new GameObject("Screen").AddComponent<Camera>();
            screen.clearFlags = CameraClearFlags.Nothing;
            screen.cullingMask = 0;
            screen.depth = 10;
            screen.gameObject.AddComponent<RtTurnShow>().Source = target;
        }

        // -tilt: the camera stands and tilts up from the rocket into the sky instead.
        private bool tilt;

        // On the circle around the rocket at angle degrees; with -tilt, at angle 0 and
        // looking that many degrees above the rocket.
        private void Pose(float degrees)
        {
            if (tilt)
            {
                cam.transform.position = new Vector3(0f, EyeHeight, -Radius);
                cam.transform.rotation = Quaternion.LookRotation(Target - cam.transform.position) * Quaternion.Euler(-degrees, 0f, 0f);
                return;
            }
            float a = degrees * Mathf.Deg2Rad;
            cam.transform.position = new Vector3(Mathf.Sin(a) * Radius, EyeHeight, -Mathf.Cos(a) * Radius);
            cam.transform.LookAt(Target);
        }

        // The run: renders the probe, checks Ray Reconstruction where asked, settles, flies the turn capturing
        // each frame, then the references at each pose and the poses halfway, writes meta.txt and path.txt, logs
        // the result and quits.
        private IEnumerator Run()
        {
            probe.RenderProbe();
            if (pass.WantsReconstruction && !pass.ReconstructionReady())
            {
                Log("FAIL  DLSS Ray Reconstruction not available: " + pass.Why);
                Finish();
                yield break;
            }

            // The turn: settled, a slow turn, a fast one as the recordings show, a jump
            // as after a hitch, then still.
            // Tilted, a quarter of the steps: 70 degrees up in all.
            tilt = Argument("-tilt") != null;
            float step = tilt ? 0.25f : 1f;
            // -stepScale: the same turn in larger steps, as at a lower frame rate (2.5: 24
            // frames a second where the turn was drawn at 60).
            string scaleArgument = Argument("-stepScale");
            if (scaleArgument != null) step *= float.Parse(scaleArgument, CultureInfo.InvariantCulture);
            var path = new List<float>();
            float angle = 0f;
            for (int i = 0; i < 6; i++) path.Add(angle);
            for (int i = 0; i < 20; i++) path.Add(angle += 2f * step);
            for (int i = 0; i < 10; i++) path.Add(angle += 12f * step);
            path.Add(angle += 40f * step);
            // Still long enough, upscaled, to see whether the frame settles and flickers.
            for (int i = 0; i < (upscaler != null ? 30 : 12); i++) path.Add(angle);

            Pose(path[0]);
            pass.Reset = true;
            for (int i = 0; i < 120; i++) yield return null;

            for (int i = 0; i < path.Count; i++)
            {
                Pose(path[i]);
                pass.Capture = "turn_" + i.ToString("000", CultureInfo.InvariantCulture);
                yield return null;
            }

            // Upscaled, the references at twice the display size, without jitter or upscaler.
            RenderTexture referenceTarget = null;
            if (upscaler != null)
            {
                referenceTarget = new RenderTexture(Width * 2, Height * 2, 24, RenderTextureFormat.DefaultHDR);
                referenceTarget.Create();
                cam.targetTexture = referenceTarget;
                pass.SetJitter(false);
            }

            // The references: each pose of the turn after the camera stood there a while.
            for (int i = 0; i < path.Count; i += (i >= 6 && i < 40) ? 1 : 3)
            {
                Pose(path[i]);
                pass.Reset = true;
                for (int f = 0; f < 60; f++) yield return null;
                pass.Capture = "ref_" + i.ToString("000", CultureInfo.InvariantCulture);
                yield return null;
            }

            // The poses halfway between two frames of the turn, where frame generation puts
            // the frames it makes.
            for (int i = 1; i < path.Count && upscaler == null; i++)
            {
                if (path[i] == path[i - 1]) continue;
                Pose((path[i - 1] + path[i]) * 0.5f);
                pass.Reset = true;
                for (int f = 0; f < 60; f++) yield return null;
                pass.Capture = "mid_" + i.ToString("000", CultureInfo.InvariantCulture);
                yield return null;
            }

            float waited = 0f;
            while (pass.Pending > 0 && waited < 10f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            File.WriteAllText(Path.Combine(output, "meta.txt"), pass.Meta.ToString());
            File.WriteAllText(Path.Combine(output, "path.txt"), string.Join("\n", path.ConvertAll(
                p => p.ToString("0.###", CultureInfo.InvariantCulture)).ToArray()));
            Log("turn written: " + pass.Written + " frames, reconstructed " + pass.Reconstructed + ", "
                + (raytracing != null ? raytracing.Status() : "plain")
                + (upscaler != null ? "; " + upscaler + " at " + renderSize.x + "x" + renderSize.y + ": " + pass.UpscalerState() : ""));
            string failure = pass.Failure();
            Log(failure == null ? "all checks passed" : "FAIL  " + failure);
            Finish();
        }

        // Quits the player once the run is over.
        private void Finish()
        {
            Application.Quit();
        }

        // The folder the turn is written to.
        internal string Output { get { return output; } }
    }

    // Shows the camera's target in the window.
    internal sealed class RtTurnShow : MonoBehaviour
    {
        // The camera's target to show.
        internal RenderTexture Source;

        // Draws the target into the window.
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            Graphics.Blit(Source, destination);
        }
    }

    // After the raytracing (RtCamera, the component before it): DLSS Ray
    // Reconstruction of the finished frame as UpscalerRig.Reconstruction.cs sends it,
    // and the frames to keep read back.
    internal sealed partial class RtTurnPass : MonoBehaviour
    {
        // The camera and its raytracing, the capture material, the frame's copies and their native handles, the
        // command buffer, Ray Reconstruction's packet ring and packet, the last frame's view projection and number,
        // and the output folder.
        private Camera cam;
        private RtCamera raytracing;
        private Material capture;
        private RenderTexture colour, depth, motion, result;
        private IntPtr colourHandle, depthHandle, motionHandle, resultHandle;
        private CommandBuffer command;
        private PacketRing ring;
        private RtNative.ReconstructPacket packet;
        private Matrix4x4 lastViewProjection;
        private int lastFrame = -1;
        private string folder;
        // The frame before's, for frame generation's camera in meta.txt.
        private Matrix4x4 previousViewProjection;
        private bool havePrevious;
        private readonly float[][] fgCamera = { new float[16], new float[16], new float[16], new float[16] };

        // Whether Ray Reconstruction is asked for (-turn rr).
        internal bool WantsReconstruction { get; private set; }
        // A reset of the history asked for the next frame, the capture's name asked for it, why Ray
        // Reconstruction is not available, the read-backs pending and written, the frames reconstructed, and
        // meta.txt's lines.
        internal bool Reset;
        internal string Capture;
        internal string Why = "";
        internal int Pending;
        internal int Written;
        internal ulong Reconstructed;
        internal readonly StringBuilder Meta = new StringBuilder();

        // The upscaler's jitter (CameraRedirect, UpscalerRig): the offset in pixels as the
        // upscalers take it, the projection without it and the one rendered with it.
        private bool jitter;
        internal Vector2 JitterPixels;
        internal Matrix4x4 UnjitteredProjection;
        internal Matrix4x4 RenderedProjection;

        // Sets the pass up for the camera and the raytracing: with or without Ray Reconstruction and jitter, the
        // output folder, the capture material, the command buffer, the packet ring and meta.txt's header.
        internal void Init(Camera camera, RtCamera rt, bool reconstruct, bool withJitter)
        {
            jitter = withJitter;
            cam = camera;
            raytracing = rt;
            WantsReconstruction = reconstruct;
            folder = RtTurnRunner.Argument("-out") ?? ".";
            capture = new Material(Shader.Find("Hidden/ReDefinition/RtCapture"));
            command = new CommandBuffer { name = "turn reconstruction" };
            ring = new PacketRing(System.Runtime.InteropServices.Marshal.SizeOf(typeof(RtNative.ReconstructPacket)));
            packet = RtNative.NewReconstructPacket();
            Meta.Append("# name width height reconstructed worldToCamera(16) gpuProjection(16), Unity's [row, column] row by"
                        + " row; then frame generation's camera as its packet takes it (StreamlineCamera): viewToClip,"
                        + " clipToView, clipToPrevClip, prevClipToClip (16 each, row-major), position, up, right,"
                        + " forward (3 each)\n");
        }

        // Why the run does not count, or null: nothing written, or Ray Reconstruction or
        // the upscaler asked for and not running.
        internal string Failure()
        {
            if (Written == 0) return "nothing written";
            if (WantsReconstruction && Reconstructed == 0) return "DLSS Ray Reconstruction reconstructed no frame";
            return UpscalerFailure();
        }

        // Whether the proxy can run Ray Reconstruction with this packet's size, keeping its reason in Why.
        internal bool ReconstructionReady()
        {
            var why = new StringBuilder(256);
            bool ready = RtNative.KspRtReconstructPacketSize() == System.Runtime.InteropServices.Marshal.SizeOf(typeof(RtNative.ReconstructPacket))
                         && RtNative.KspRtReconstructionAvailable(why, why.Capacity) == 1;
            Why = why.ToString();
            return ready;
        }

        // A point-filtered linear texture of the size and format, writable by compute where random is set.
        private RenderTexture Make(int w, int h, RenderTextureFormat format, bool random)
        {
            var texture = new RenderTexture(w, h, 0, format, RenderTextureReadWrite.Linear) { enableRandomWrite = random };
            texture.filterMode = FilterMode.Point;
            texture.Create();
            return texture;
        }

        // Each frame, with Ray Reconstruction wanted, renews the raytracing's lease on its guide buffers.
        private void Update()
        {
            if (raytracing != null && WantsReconstruction) raytracing.ReconstructionLease = Time.frameCount;
        }

        // The radical inverse of the index in the radix: Halton's sequence, from 0 to 1.
        private static float Halton(int index, int radix)
        {
            float f = 1f, result = 0f;
            for (int i = index; i > 0; i /= radix)
            {
                f /= radix;
                result += f * (i % radix);
            }
            return result;
        }

        // Before the frame is culled: the unjittered projection, and with jitter on the frame's Halton offset
        // applied to it, as the upscaler jitters.
        private void OnPreCull()
        {
            cam.ResetProjectionMatrix();
            UnjitteredProjection = cam.projectionMatrix;
            JitterPixels = Vector2.zero;
            if (jitter)
            {
                // Fsr3Upscaler.GetJitterOffset with the phases InitUpscaler counted, 8 without an upscaler.
                int phase = Time.frameCount % jitterPhases + 1;
                JitterPixels = new Vector2(Halton(phase, 2) - 0.5f, Halton(phase, 3) - 0.5f);
                cam.nonJitteredProjectionMatrix = UnjitteredProjection;
                cam.projectionMatrix = Matrix4x4.Translate(new Vector3(2f * JitterPixels.x / cam.pixelWidth,
                                                                       2f * JitterPixels.y / cam.pixelHeight, 0f))
                                       * UnjitteredProjection;
                cam.useJitteredProjectionMatrixForTransparentRendering = true;
            }
            RenderedProjection = cam.projectionMatrix;
        }

        // Puts the unjittered projection back once the frame is rendered.
        private void OnPostRender()
        {
            if (jitter) cam.ResetProjectionMatrix();
        }

        // The upscaled turn's reference frame, copied at its own size to be read back.
        private RenderTexture referenceCopy;

        // Copies the frame's colour, depth and motion, reconstructs or upscales it, shows it, and where a capture
        // is asked for writes it with its camera to meta.txt and reads its images back; references at twice the
        // display size are kept as rendered.
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            // A reference of the upscaled turn: rendered at twice the display size, kept as
            // it is.
            if (colour != null && source.width != colour.width)
            {
                Graphics.Blit(source, destination);
                if (Capture == null) return;
                if (referenceCopy == null || referenceCopy.width != source.width)
                    referenceCopy = Make(source.width, source.height, RenderTextureFormat.ARGBHalf, false);
                Graphics.Blit(source, referenceCopy);
                string referenceName = Capture;
                Capture = null;
                Meta.Append(referenceName).Append(' ').Append(source.width).Append(' ').Append(source.height).Append(" 0");
                AppendMatrix(cam.worldToCameraMatrix);
                AppendMatrix(GL.GetGPUProjectionMatrix(cam.projectionMatrix, true));
                Meta.Append('\n');
                Read(referenceCopy, referenceName + "_colour.bin");
                return;
            }
            if (colour == null)
            {
                colour = Make(source.width, source.height, RenderTextureFormat.ARGBHalf, false);
                depth = Make(source.width, source.height, RenderTextureFormat.RFloat, false);
                motion = Make(source.width, source.height, RenderTextureFormat.RGHalf, false);
                result = Make(source.width, source.height, RenderTextureFormat.DefaultHDR, true);
                colourHandle = colour.GetNativeTexturePtr();
                depthHandle = depth.GetNativeTexturePtr();
                motionHandle = motion.GetNativeTexturePtr();
                resultHandle = result.GetNativeTexturePtr();
            }
            command.Clear();
            command.Blit(source, colour);
            command.Blit(source, depth, capture, 0);
            command.Blit(source, motion, capture, 4);
            bool reconstructed = WantsReconstruction && raytracing != null && raytracing.GuidesFrame == Time.frameCount;
            if (reconstructed)
            {
                Fill(source.width, source.height);
                // -sharpness: RCAS after Ray Reconstruction, as the rig sharpens DLSS's output
                // (RcasSharpener): Ray Reconstruction writes its input, RCAS the result.
                if (Sharpener() != null) packet.Output = sharpenInputHandle;
                ring.Issue(command, RtNative.RenderEvent, RtNative.ReconstructEvent, ref packet);
                if (sharpener != null) sharpener.Schedule(command, sharpness);
            }
            if (Upscaling && !reconstructed) Upscale(command, cam.fieldOfView * Mathf.Deg2Rad);
            Graphics.ExecuteCommandBuffer(command);
            RenderTexture shown = reconstructed ? result : Upscaling ? upscaled : colour;
            Graphics.Blit(shown, destination);
            Reconstructed = WantsReconstruction ? RtNative.KspRtReconstructedFrames() : 0;

            Matrix4x4 projectionNow = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            Matrix4x4 viewProjectionNow = projectionNow * cam.worldToCameraMatrix;
            if (Capture != null)
            {
                string name = Capture;
                Capture = null;
                Meta.Append(name).Append(' ').Append(shown.width).Append(' ').Append(shown.height).Append(' ')
                    .Append(reconstructed ? 1 : 0);
                AppendMatrix(cam.worldToCameraMatrix);
                AppendMatrix(projectionNow);
                StreamlineCamera.Fill(projectionNow, viewProjectionNow, havePrevious ? previousViewProjection : viewProjectionNow,
                                      fgCamera[0], fgCamera[1], fgCamera[2], fgCamera[3]);
                foreach (float[] m in fgCamera)
                    foreach (float v in m)
                        Meta.Append(' ').Append(v.ToString("R", CultureInfo.InvariantCulture));
                Transform t = cam.transform;
                foreach (Vector3 v in new[] { t.position, t.up, t.right, t.forward })
                    Meta.Append(' ').Append(v.x.ToString("R", CultureInfo.InvariantCulture))
                        .Append(' ').Append(v.y.ToString("R", CultureInfo.InvariantCulture))
                        .Append(' ').Append(v.z.ToString("R", CultureInfo.InvariantCulture));
                Meta.Append('\n');
                Read(shown, name + "_colour.bin");
                Read(depth, name + "_depth.bin");
                Read(motion, name + "_motion.bin");
                // The raytracing's sun output, its w the view depth it traced from.
                if (raytracing != null && raytracing.SunTexture != null) Read(raytracing.SunTexture, name + "_sun.bin");
            }
            previousViewProjection = viewProjectionNow;
            havePrevious = true;
        }

        // Appends the matrix to meta.txt's line, Unity's [row, column] row by row.
        private void AppendMatrix(Matrix4x4 m)
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    Meta.Append(' ').Append(m[r, c].ToString("R", CultureInfo.InvariantCulture));
        }

        // Reads the texture back without waiting and writes its bytes to the file in the output folder, counting
        // it as pending until then and as written after.
        private void Read(RenderTexture texture, string file)
        {
            Pending++;
            string path = Path.Combine(folder, file);
            AsyncGPUReadback.Request(texture, 0, request =>
            {
                Pending--;
                if (request.hasError) return;
                File.WriteAllBytes(path, request.GetData<byte>().ToArray());
                Written++;
            });
        }

        // As UpscalerRig.FillReconstruction: unjittered matrices as Streamline takes them,
        // Unity's motion vectors scaled to Streamline's units, DLAA.
        private void Fill(int width, int height)
        {
            RtNative.ReconstructPacket p = packet;
            p.FrameIndex = raytracing.GuidesIndex;
            p.Flags = Reset || lastFrame != Time.frameCount - 1 ? RtNative.FlagReset : 0u;
            Reset = false;
            p.RenderWidth = p.OutputWidth = (uint)width;
            p.RenderHeight = p.OutputHeight = (uint)height;
            p.Mode = 6;   // sl::DLSSMode::eDLAA
            p.Preset = 0;
            Array.Copy(raytracing.GuidesClipToWorld, p.ClipToWorld, 16);
            Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            Matrix4x4 view = cam.worldToCameraMatrix;
            Matrix4x4 viewProjection = gpuProjection * view;
            bool previous = (p.Flags & RtNative.FlagReset) == 0;
            StreamlineCamera.Fill(gpuProjection, viewProjection, previous ? lastViewProjection : viewProjection,
                                  p.ViewToClip, p.ClipToView, p.ClipToPrevClip, p.PrevClipToClip);
            lastViewProjection = viewProjection;
            lastFrame = Time.frameCount;
            Matrix4x4 viewPlusZ = StreamlineCamera.FlipZ() * view;
            StreamlineCamera.WriteRowMajor(viewPlusZ, p.WorldToView);
            StreamlineCamera.WriteRowMajor(StreamlineCamera.Invert(viewPlusZ), p.ViewToWorld);
            p.Jitter[0] = JitterPixels.x;
            p.Jitter[1] = JitterPixels.y;
            p.MotionScale[0] = -1f;
            p.MotionScale[1] = -1f;
            Transform t = cam.transform;
            p.Up[0] = t.up.x; p.Up[1] = t.up.y; p.Up[2] = t.up.z;
            p.Right[0] = t.right.x; p.Right[1] = t.right.y; p.Right[2] = t.right.z;
            p.Forward[0] = t.forward.x; p.Forward[1] = t.forward.y; p.Forward[2] = t.forward.z;
            p.NearPlane = cam.nearClipPlane;
            p.FarPlane = cam.farClipPlane;
            p.VerticalFov = cam.fieldOfView * Mathf.Deg2Rad;
            p.Colour = colourHandle;
            p.Depth = depthHandle;
            p.Motion = motionHandle;
            p.GBuffer0 = p.GBuffer1 = p.GBuffer2 = p.Specular = IntPtr.Zero;
            p.Output = resultHandle;
            packet = p;
        }
    }
}
