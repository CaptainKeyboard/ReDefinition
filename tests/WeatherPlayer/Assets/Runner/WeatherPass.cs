using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FidelityFX;
using FidelityFX.FSR3;
using ReDefinition.Proxy;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // The weather player's camera pass: the upscaler's jitter, the frame's colour, depth and motion vectors (with the cloud slab's
    // analytic motion and depth laid over Unity's, where the slab covers a pixel and no mask is given), the upscaler, and the frames
    // to keep read back. The upscaler's dispatch is the one of the raytracing player's turn (tests/RaytracingPlayer/Assets/Runner/
    // RtTurnUpscale.cs: FSR 3 through its Unity port, DLSS and AMD's upscaler through the proxy), here with XeSS and DLSS's presets
    // added. It is also the one place that drives the slab each frame, so that the jitter is set before the slab reads it.
    internal sealed class WeatherPass : MonoBehaviour
    {
        // The command line's value after the name, or null.
        internal static string Argument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        private Camera cam;
        private WeatherSlab slab;
        private Material capture;
        private RenderTexture colour, depthRaw, depth, motionRaw, motion, upscaled, referenceCopy;
        private IntPtr colourHandle, depthHandle, motionHandle, upscaledHandle;
        private CommandBuffer command;
        private string folder;
        private Matrix4x4 previousViewProjection, previousUnjittered;
        private bool havePrevious;
        private readonly float[][] fgCamera = { new float[16], new float[16], new float[16], new float[16] };

        // The turn's output: the upscaler asked for (native, fsr3, dlss, amd, xess, or null for none), the sizes, the jitter, the
        // analytic motion and depth, the frames counted, and what the slab's pixel gate is.
        private string upscaler;
        private Vector2Int displaySize, renderSize;
        private bool jitter;
        private bool analytic = true;
        private int jitterPhases = 8;
        private int frameIndex;
        private Fsr3UpscalerContext fsr;
        private readonly Fsr3Upscaler.DispatchDescription fsrDispatch = new Fsr3Upscaler.DispatchDescription();
        private int lastUpscaledFrame = -1;
        private int upscaledFrames;
        private Vector2 jitterSign = Vector2.one, motionSign = Vector2.one;
        private uint dlssPreset;
        internal string UpscalerProblem;
        internal Vector2 JitterPixels;
        internal Matrix4x4 UnjitteredProjection, RenderedProjection;
        internal bool ResetUpscaler;
        // Draws onto the finished image of a frame (the upscaled one) and onto each reference copy; null: nothing is drawn (package D37).
        internal Action<RenderTexture> PostComposite;
        internal string Capture;
        internal int FrameOverride = -1;
        internal readonly Dictionary<string, int> FrameOf = new Dictionary<string, int>();
        internal int Pending, Written;
        internal readonly StringBuilder Meta = new StringBuilder();

        internal bool Upscaling { get { return upscaler != null && upscaler != "native"; } }

        private static Vector2 Signs(string axes)
        {
            if (axes == null) return Vector2.one;
            return new Vector2(axes.Contains("x") ? -1f : 1f, axes.Contains("y") ? -1f : 1f);
        }

        // Sets the pass up for the camera and the slab; folder is where the frames go. The upscaler named (or null for none)
        // renders at renderSize and upscales to displaySize.
        internal void Init(Camera camera, WeatherSlab weatherSlab, string output, string upscalerName, Vector2Int display, Vector2Int render)
        {
            cam = camera;
            slab = weatherSlab;
            folder = output;
            upscaler = upscalerName;
            displaySize = display;
            renderSize = render;
            jitterSign = Signs(Argument("-jitterSign"));
            motionSign = Signs(Argument("-motionSign"));
            analytic = Argument("-analytic") != "0";
            string preset = Argument("-preset");
            dlssPreset = preset == "K" ? (uint)DlssPreset.K : preset == "L" ? (uint)DlssPreset.L : preset == "M" ? (uint)DlssPreset.M : 0u;
            float ratio = (float)display.x / render.x;
            jitterPhases = Mathf.Max(8, (int)(8f * ratio * ratio));
            jitter = upscalerName != null && upscalerName != "native";
            capture = new Material(Shader.Find("Hidden/ReDefinition/RtCapture"));
            command = new CommandBuffer { name = "weather pass" };
            Meta.Append("# name width height reconstructed worldToCamera(16) gpuProjection(16), Unity's [row, column] row by"
                        + " row; then frame generation's camera as its packet takes it (StreamlineCamera): viewToClip,"
                        + " clipToView, clipToPrevClip, prevClipToClip (16 each, row-major), position, up, right,"
                        + " forward (3 each)\n");
            if (Upscaling)
            {
                upscaled = new RenderTexture(display.x, display.y, 0, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Default)
                {
                    enableRandomWrite = true,
                    name = "weather upscaled",
                };
                upscaled.Create();
                upscaledHandle = upscaled.GetNativeTexturePtr();
            }
        }

        // The next frame has no history: the upscaler and the motion pass start again.
        internal void Reset()
        {
            ResetUpscaler = true;
            havePrevious = false;
        }

        internal void SetJitter(bool on)
        {
            jitter = on;
        }

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

        // Before the frame is culled: the jitter (the upscaler's Halton offset), the slab's frame.
        private void OnPreCull()
        {
            cam.ResetProjectionMatrix();
            UnjitteredProjection = cam.projectionMatrix;
            JitterPixels = Vector2.zero;
            if (jitter)
            {
                int phase = Time.frameCount % jitterPhases + 1;
                JitterPixels = new Vector2(Halton(phase, 2) - 0.5f, Halton(phase, 3) - 0.5f);
                cam.nonJitteredProjectionMatrix = UnjitteredProjection;
                cam.projectionMatrix = Matrix4x4.Translate(new Vector3(2f * JitterPixels.x / cam.pixelWidth,
                                                                       2f * JitterPixels.y / cam.pixelHeight, 0f))
                                       * UnjitteredProjection;
                cam.useJitteredProjectionMatrixForTransparentRendering = true;
            }
            RenderedProjection = cam.projectionMatrix;
            slab.JitterPixels = JitterPixels;
            slab.JitterIndex = jitter ? Time.frameCount % jitterPhases + 1 : 0;
            // A reference is drawn at the simulation time of its turn frame, so the wind has not moved the cloud between them.
            slab.Frame = FrameOverride >= 0 ? FrameOverride : frameIndex;
            frameIndex++;
            slab.Prepare();
        }

        private void OnPostRender()
        {
            if (jitter) cam.ResetProjectionMatrix();
        }

        private RenderTexture Make(int w, int h, RenderTextureFormat format, bool random)
        {
            var texture = new RenderTexture(w, h, 0, format, RenderTextureReadWrite.Linear) { enableRandomWrite = random };
            texture.filterMode = FilterMode.Point;
            texture.Create();
            return texture;
        }

        // Copies the frame's colour, depth and motion (the slab's analytic values over them), upscales and shows it; where a
        // capture is asked for, writes the frame's camera to meta.txt and reads its images back. References at twice the display
        // size are kept as rendered.
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (colour != null && source.width != colour.width)
            {
                Graphics.Blit(source, destination);
                if (Capture == null) return;
                if (referenceCopy == null || referenceCopy.width != source.width)
                    referenceCopy = Make(source.width, source.height, RenderTextureFormat.ARGBHalf, false);
                Graphics.Blit(source, referenceCopy);
                if (PostComposite != null) PostComposite(referenceCopy);
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
                depthRaw = Make(source.width, source.height, RenderTextureFormat.RFloat, false);
                depth = Make(source.width, source.height, RenderTextureFormat.RFloat, false);
                motionRaw = Make(source.width, source.height, RenderTextureFormat.RGHalf, false);
                motion = Make(source.width, source.height, RenderTextureFormat.RGHalf, false);
                colourHandle = colour.GetNativeTexturePtr();
                depthHandle = depth.GetNativeTexturePtr();
                motionHandle = motion.GetNativeTexturePtr();
            }
            command.Clear();
            command.Blit(source, colour);
            command.Blit(source, depthRaw, capture, 0);
            command.Blit(source, motionRaw, capture, 4);
            Matrix4x4 unjittered = GL.GetGPUProjectionMatrix(UnjitteredProjection, false) * cam.worldToCameraMatrix;
            if (analytic && slab.SlabOn && slab.VolumeMode)
            {
                // The volume pass's own analytic motion and depth (its hit distance and the window's advection).
                slab.Volume.Renderer.AppendAnalytic(command, motionRaw, motion, depthRaw, depth, unjittered,
                                                    havePrevious ? previousUnjittered : unjittered, 1f / 60f, Argument("-mvFlip") != null);
            }
            else
            {
                command.Blit(motionRaw, motion);
                command.Blit(depthRaw, depth);
            }
            if (Upscaling) Upscale(command, cam.fieldOfView * Mathf.Deg2Rad);
            Graphics.ExecuteCommandBuffer(command);
            RenderTexture shown = Upscaling ? upscaled : colour;
            // A composite drawn onto the finished image, after the upscaler (the camera frost's host path), before it is shown and read back.
            if (PostComposite != null) PostComposite(shown);
            Graphics.Blit(shown, destination);

            Matrix4x4 projectionNow = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            Matrix4x4 viewProjectionNow = projectionNow * cam.worldToCameraMatrix;
            if (Capture != null)
            {
                string name = Capture;
                Capture = null;
                FrameOf[name] = slab.Frame;
                Meta.Append(name).Append(' ').Append(shown.width).Append(' ').Append(shown.height).Append(" 0");
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
                Read(motionRaw, name + "_motionraw.bin");
                if (slab.VolumeMode) Read(slab.SlabTexture, name + "_slab.bin");
            }
            previousViewProjection = viewProjectionNow;
            previousUnjittered = unjittered;
            havePrevious = true;
        }

        private void AppendMatrix(Matrix4x4 m)
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    Meta.Append(' ').Append(m[r, c].ToString("R", CultureInfo.InvariantCulture));
        }

        // Reads the texture back without waiting and writes its bytes to the file in the output folder.
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

        // FSR 3's compute shaders from ReDefinition's bundle, as ShaderBundle loads them in the game.
        private static Fsr3UpscalerShaders FsrShaders(out string problem)
        {
            string missing = null;
            string path = Argument("-bundle")
                          ?? @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program\GameData\ReDefinition\Main\Shaders\redefinition.shaders";
            AssetBundle bundle = File.Exists(path) ? AssetBundle.LoadFromFile(path) : null;
            if (bundle == null)
            {
                problem = "no shader bundle at " + path;
                return null;
            }
            var byName = new Dictionary<string, ComputeShader>();
            foreach (ComputeShader shader in bundle.LoadAllAssets<ComputeShader>()) byName[shader.name] = shader;
            ComputeShader Get(string name)
            {
                ComputeShader found;
                if (!byName.TryGetValue(name, out found) && missing == null) missing = "the bundle has no " + name;
                return found;
            }
            var shaders = new Fsr3UpscalerShaders
            {
                prepareInputsPass = Get("ffx_fsr3upscaler_prepare_inputs_pass"),
                lumaPyramidPass = Get("ffx_fsr3upscaler_luma_pyramid_pass"),
                shadingChangePyramidPass = Get("ffx_fsr3upscaler_shading_change_pyramid_pass"),
                shadingChangePass = Get("ffx_fsr3upscaler_shading_change_pass"),
                prepareReactivityPass = Get("ffx_fsr3upscaler_prepare_reactivity_pass"),
                lumaInstabilityPass = Get("ffx_fsr3upscaler_luma_instability_pass"),
                accumulatePass = Get("ffx_fsr3upscaler_accumulate_pass"),
                sharpenPass = Get("ffx_fsr3upscaler_rcas_pass"),
                autoGenReactivePass = Get("ffx_fsr3upscaler_autogen_reactive_pass"),
                tcrAutoGenPass = Get("ffx_fsr3upscaler_tcr_autogen_pass"),
                debugViewPass = Get("ffx_fsr3upscaler_debug_view_pass"),
            };
            problem = missing;
            return missing == null ? shaders : null;
        }

        // DLSS's quality value for the render scale, as DlssBridge.Quality maps the modes.
        private int DlssQuality()
        {
            float scale = (float)renderSize.x / displaySize.x;
            if (scale > 0.95f) return 5;
            if (scale > 0.62f) return 2;
            if (scale > 0.54f) return 1;
            if (scale > 0.42f) return 0;
            return 3;
        }

        // Into command: the upscaled frame from colour, depth and motion at the render size.
        private void Upscale(CommandBuffer buffer, float verticalFovRadians)
        {
            bool reset = ResetUpscaler || lastUpscaledFrame != Time.frameCount - 1;
            ResetUpscaler = false;
            lastUpscaledFrame = Time.frameCount;
            uint flags = NativeUpscalerLink.FlagHdr | NativeUpscalerLink.FlagAutoExposure
                         | (SystemInfo.usesReversedZBuffer ? NativeUpscalerLink.FlagDepthInverted : 0u)
                         | (reset ? NativeUpscalerLink.FlagReset : 0u);
            float near = cam.nearClipPlane, far = cam.farClipPlane;
            if (SystemInfo.usesReversedZBuffer)
            {
                float swap = near;
                near = far;
                far = swap;
            }
            if (upscaler == "fsr3")
            {
                if (fsr == null)
                {
                    Fsr3UpscalerShaders shaders = FsrShaders(out UpscalerProblem);
                    if (shaders == null) return;
                    fsr = Fsr3Upscaler.CreateContext(displaySize, renderSize, shaders,
                                                     Fsr3Upscaler.InitializationFlags.EnableHighDynamicRange
                                                     | Fsr3Upscaler.InitializationFlags.EnableAutoExposure);
                }
                fsrDispatch.Color = new ResourceView(colour);
                fsrDispatch.Depth = new ResourceView(depth);
                fsrDispatch.MotionVectors = new ResourceView(motion);
                fsrDispatch.Output = new ResourceView(upscaled);
                fsrDispatch.Exposure = ResourceView.Unassigned;
                fsrDispatch.Reactive = ResourceView.Unassigned;
                fsrDispatch.TransparencyAndComposition = ResourceView.Unassigned;
                fsrDispatch.JitterOffset = Vector2.Scale(JitterPixels, jitterSign);
                fsrDispatch.MotionVectorScale = Vector2.Scale(new Vector2(-renderSize.x, -renderSize.y), motionSign);
                fsrDispatch.RenderSize = renderSize;
                fsrDispatch.UpscaleSize = displaySize;
                fsrDispatch.EnableSharpening = false;
                fsrDispatch.Sharpness = 0f;
                fsrDispatch.FrameTimeDelta = 1f / 60f;
                fsrDispatch.PreExposure = 1f;
                fsrDispatch.Reset = reset;
                fsrDispatch.CameraNear = near;
                fsrDispatch.CameraFar = far;
                fsrDispatch.CameraFovAngleVertical = verticalFovRadians;
                fsrDispatch.ViewSpaceToMetersFactor = 1f;
                fsr.Dispatch(fsrDispatch, buffer);
                upscaledFrames++;
            }
            else if (upscaler == "dlss")
            {
                var packet = new DlssBridge.DlssPacket
                {
                    Colour = colourHandle,
                    Output = upscaledHandle,
                    Depth = depthHandle,
                    MotionVectors = motionHandle,
                    RenderWidth = (uint)renderSize.x,
                    RenderHeight = (uint)renderSize.y,
                    OutputWidth = (uint)displaySize.x,
                    OutputHeight = (uint)displaySize.y,
                    Quality = DlssQuality(),
                    Preset = dlssPreset,
                    Flags = flags,
                    JitterX = JitterPixels.x * jitterSign.x,
                    JitterY = JitterPixels.y * jitterSign.y,
                    MotionVectorScaleX = -renderSize.x * motionSign.x,
                    MotionVectorScaleY = -renderSize.y * motionSign.y,
                };
                if (DlssBridge.Submit(buffer, ref packet)) upscaledFrames++;
                else UpscalerProblem = DlssBridge.Link.Describe();
            }
            else if (upscaler == "amd" || upscaler == "xess")
            {
                var packet = new AmdUpscalerBridge.AmdPacket
                {
                    Colour = colourHandle,
                    Output = upscaledHandle,
                    Depth = depthHandle,
                    MotionVectors = motionHandle,
                    RenderWidth = (uint)renderSize.x,
                    RenderHeight = (uint)renderSize.y,
                    OutputWidth = (uint)displaySize.x,
                    OutputHeight = (uint)displaySize.y,
                    Flags = flags,
                    JitterX = JitterPixels.x * jitterSign.x,
                    JitterY = JitterPixels.y * jitterSign.y,
                    MotionVectorScaleX = -renderSize.x * motionSign.x,
                    MotionVectorScaleY = -renderSize.y * motionSign.y,
                    Sharpness = 0f,
                    FrameTimeDeltaMs = 1000f / 60f,
                    CameraNear = near,
                    CameraFar = far,
                    VerticalFovRadians = verticalFovRadians,
                };
                bool sent = upscaler == "xess" ? XessUpscalerBridge.Submit(buffer, ref packet) : AmdUpscalerBridge.Submit(buffer, ref packet);
                if (sent) upscaledFrames++;
                else UpscalerProblem = (upscaler == "xess" ? XessUpscalerBridge.Link : AmdUpscalerBridge.Link).Describe();
            }
        }

        // Why the upscaler did not upscale the turn, or null where it did or none is asked for.
        internal string UpscalerFailure()
        {
            if (!Upscaling) return null;
            if (upscaledFrames == 0) return upscaler + " upscaled no frame: " + (UpscalerProblem ?? "nothing dispatched");
            if (upscaler == "dlss" && DlssBridge.Link.State() <= 0) return "DLSS not running: " + UpscalerState();
            if (upscaler == "amd" && AmdUpscalerBridge.Link.State() <= 0) return "AMD's upscaler not running: " + UpscalerState();
            if (upscaler == "xess" && XessUpscalerBridge.Link.State() <= 0) return "XeSS not running: " + UpscalerState();
            return null;
        }

        internal string UpscalerState()
        {
            if (upscaler == "dlss") return DlssBridge.Link.State() + " " + DlssBridge.Link.Describe();
            if (upscaler == "amd") return AmdUpscalerBridge.Link.State() + " " + AmdUpscalerBridge.Link.Describe();
            if (upscaler == "xess") return XessUpscalerBridge.Link.State() + " " + XessUpscalerBridge.Link.Describe();
            return upscaler + (fsr != null ? " running" : " not made: " + UpscalerProblem);
        }

        // Why the run does not count, or null.
        internal string Failure()
        {
            if (Written == 0) return "nothing written";
            return UpscalerFailure();
        }
    }
}
