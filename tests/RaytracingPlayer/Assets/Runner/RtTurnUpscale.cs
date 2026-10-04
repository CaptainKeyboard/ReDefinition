using System.Collections.Generic;
using System.IO;
using FidelityFX;
using FidelityFX.FSR3;
using ReDefinition.Bridges;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.RaytracingPlayer
{
    // The turn through one of the upscalers, as UpscalerRig sends each: FSR 3 through its
    // Unity port, DLSS and AMD's upscaler through the proxy (DlssBridge,
    // AmdUpscalerBridge). The camera renders at the render size with the upscaler's
    // jitter; the frame kept is the upscaled one, at the display size.
    internal sealed partial class RtTurnPass
    {
        private string upscaler;
        private Vector2Int displaySize, renderSize;
        private int jitterPhases = 8;
        private RenderTexture upscaled;
        private System.IntPtr upscaledHandle;
        private Fsr3UpscalerContext fsr;
        private readonly Fsr3Upscaler.DispatchDescription fsrDispatch = new Fsr3Upscaler.DispatchDescription();
        private int lastUpscaledFrame = -1;
        private int upscaledFrames;
        internal bool ResetUpscaler;
        // Counter-checks of the conventions UpscalerRig hands on (-jitterSign, -motionSign:
        // "x", "y" or "xy" negated): the right ones measure best.
        private Vector2 jitterSign = Vector2.one, motionSign = Vector2.one;

        private static Vector2 Signs(string axes)
        {
            if (axes == null) return Vector2.one;
            return new Vector2(axes.Contains("x") ? -1f : 1f, axes.Contains("y") ? -1f : 1f);
        }
        internal string UpscalerProblem;

        internal bool Upscaling { get { return upscaler != null && upscaler != "native"; } }

        // -masks: empty reactive and transparency masks for AMD's upscaler, as the rig
        // hands its own; empty, they leave the image as it is without them.
        private System.IntPtr reactiveHandle, transparencyHandle;

        private static System.IntPtr EmptyMask(Vector2Int size, string name)
        {
            var mask = new RenderTexture(size.x, size.y, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear) { name = name };
            mask.Create();
            RenderTexture active = RenderTexture.active;
            RenderTexture.active = mask;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = active;
            return mask.GetNativeTexturePtr();
        }

        private ReDefinition.Upscaler.RcasSharpener sharpener;
        private System.IntPtr sharpenInputHandle;
        private float sharpness = -1f;
        private bool sharpenerLooked;

        // RCAS after Ray Reconstruction where -sharpness is given, into the result.
        private ReDefinition.Upscaler.RcasSharpener Sharpener()
        {
            if (sharpenerLooked) return sharpener;
            sharpenerLooked = true;
            string value = RtTurnRunner.Argument("-sharpness");
            if (value == null) return null;
            sharpness = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            string problem;
            Fsr3UpscalerShaders shaders = FsrShaders(out problem);
            if (shaders == null)
            {
                UpscalerProblem = problem;
                return null;
            }
            sharpener = new ReDefinition.Upscaler.RcasSharpener(shaders.sharpenPass, result);
            sharpenInputHandle = sharpener.Input.GetNativeTexturePtr();
            return sharpener;
        }

        internal void SetJitter(bool on)
        {
            jitter = on;
        }

        internal void InitUpscaler(string name, Vector2Int display, Vector2Int render)
        {
            upscaler = name;
            jitterSign = Signs(RtTurnRunner.Argument("-jitterSign"));
            motionSign = Signs(RtTurnRunner.Argument("-motionSign"));
            displaySize = display;
            renderSize = render;
            float ratio = (float)display.x / render.x;
            // Fsr3Upscaler.GetJitterPhaseCount, as the rig takes it.
            jitterPhases = Mathf.Max(8, (int)(8f * ratio * ratio));
            jitter = name != "native";
            if (!Upscaling) return;
            upscaled = new RenderTexture(display.x, display.y, 0, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Default)
            {
                enableRandomWrite = true,
                name = "turn upscaled",
            };
            upscaled.Create();
            upscaledHandle = upscaled.GetNativeTexturePtr();
            if (name == "amd" && RtTurnRunner.Argument("-masks") != null)
            {
                reactiveHandle = EmptyMask(render, "turn reactive");
                transparencyHandle = EmptyMask(render, "turn transparency");
            }
        }

        // FSR 3's compute shaders from ReDefinition's bundle, as ShaderBundle loads them
        // in the game: the HDR set, accumulation without sharpening.
        private static Fsr3UpscalerShaders FsrShaders(out string problem)
        {
            string missing = null;
            string path = RtTurnRunner.Argument("-bundle")
                          ?? @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program\GameData\ReDefinition\Shaders\redefinition.shaders";
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

        // DLSS's quality value for the render scale, as DlssBridge.Quality maps the modes:
        // performance 0, balanced 1, quality 2, ultra performance 3, DLAA 5.
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
        private void Upscale(CommandBuffer command, float verticalFovRadians)
        {
            bool reset = ResetUpscaler || lastUpscaledFrame != Time.frameCount - 1;
            ResetUpscaler = false;
            lastUpscaledFrame = Time.frameCount;
            uint flags = NativeUpscalerLink.FlagHdr | NativeUpscalerLink.FlagAutoExposure
                         | (SystemInfo.usesReversedZBuffer ? NativeUpscalerLink.FlagDepthInverted : 0u)
                         | (reset ? NativeUpscalerLink.FlagReset : 0u);
            // Reversed depth: the planes swapped, as the rig hands them on (SetupDispatch).
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
                fsr.Dispatch(fsrDispatch, command);
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
                    Preset = 0,
                    Flags = flags,
                    JitterX = JitterPixels.x * jitterSign.x,
                    JitterY = JitterPixels.y * jitterSign.y,
                    MotionVectorScaleX = -renderSize.x * motionSign.x,
                    MotionVectorScaleY = -renderSize.y * motionSign.y,
                };
                if (DlssBridge.Submit(command, ref packet)) upscaledFrames++;
                else UpscalerProblem = DlssBridge.Link.Describe();
            }
            else if (upscaler == "amd")
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
                    Reactive = reactiveHandle,
                    TransparencyAndComposition = transparencyHandle,
                };
                if (AmdUpscalerBridge.Submit(command, ref packet)) upscaledFrames++;
                else UpscalerProblem = AmdUpscalerBridge.Link.Describe();
            }
        }

        // Why the upscaler did not upscale the turn, or null where it did or none is asked for.
        internal string UpscalerFailure()
        {
            if (!Upscaling) return null;
            if (upscaledFrames == 0) return upscaler + " upscaled no frame: " + (UpscalerProblem ?? "nothing dispatched");
            if (upscaler == "dlss" && DlssBridge.Link.State() <= 0) return "DLSS not running: " + UpscalerState();
            if (upscaler == "amd" && AmdUpscalerBridge.Link.State() <= 0) return "AMD's upscaler not running: " + UpscalerState();
            return null;
        }

        // The upscaler's state as the proxy reports it: above 0 running, below stopped.
        internal string UpscalerState()
        {
            if (upscaler == "dlss") return DlssBridge.Link.State() + " " + DlssBridge.Link.Describe();
            if (upscaler == "amd") return AmdUpscalerBridge.Link.State() + " " + AmdUpscalerBridge.Link.Describe();
            return upscaler + (fsr != null ? " running" : " not made: " + UpscalerProblem);
        }
    }
}
