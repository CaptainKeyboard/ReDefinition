using System;
using System.Collections.Generic;
using System.Text;
using ReDefinition.Bridges;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Raytracing
{
    // What the raytracing of a camera does, as the settings choose it.
    internal struct RtSettings
    {
        public bool Shadows;
        public bool Occlusion;
        public bool Diffuse;
        public bool Reflections;
        public bool Denoise;
        public int Bounces;
        public float OcclusionDistance;
        public float Clamp;

        internal static RtSettings Full()
        {
            return new RtSettings
            {
                Shadows = true, Occlusion = true, Diffuse = true, Reflections = true, Denoise = true,
                Bounces = 2, OcclusionDistance = 2f, Clamp = 20f,
            };
        }
    }

    // The raytracing of one camera (docs/development/raytracing.md, "How the frame
    // goes"). On the scene camera: in OnPreCull the scene's instances of this frame;
    // in OnRenderImage, where a render event sees the current frame, depth, G-buffer
    // and motion copied and the raytracing started on the proxy, its results back in
    // the same frame; in the next frame's lighting, those results carried over to it.
    //
    // What depends on KSP comes in through the fields below: the sources of the scene,
    // the sun, the sky, how far the world moved between two frames, and the projection
    // the frame was rendered with.
    internal sealed class RtCamera : MonoBehaviour
    {
        internal readonly RtScene Scene = new RtScene();
        internal RtSettings Settings = RtSettings.Full();

        // The shaders: the capture, the screen-space shadows, the deferred reflections;
        // the compute shader that gathers the GPU's instances.
        internal Func<string, Shader> ShaderSource = Shader.Find;
        internal Func<string, ComputeShader> ComputeSource = name => Resources.Load<ComputeShader>(name);
        // Towards the sun, and its angular radius in w; its colour; the sky's.
        internal Func<Vector4> Sun = () => new Vector4(0.3f, 0.8f, 0.5f, 0.0047f);
        internal Func<Color> SunColor = () => Color.white;
        // The sun's light, whose screen-space mask alone takes the raytraced visibility;
        // null for every directional light's.
        internal Func<Light> SunLight;
        internal Func<Color> SkyColor = () => RenderSettings.ambientSkyColor;
        // The sky as a cube map, or null for the sky's colour.
        internal Func<Texture> Environment = () => null;
        // Where a point of this frame's world was in the previous frame's: previous = this +
        // shift, for what is near the camera, and for the bodies and what stands on them,
        // which KSP moves apart under Krakensbane.
        internal Func<RtPosition> WorldShift = () => new RtPosition(0, 0, 0);
        internal Func<RtPosition> WorldShiftFar;
        // The projection this frame was rendered with, jitter and all.
        internal Func<Matrix4x4> RenderedProjection;
        // The main body's centre in the world and its radius, which the sun's rays test
        // as a sphere; radius 0 for none.
        internal Func<Vector4> Body = () => Vector4.zero;
        // Unity's ambient light, which its shading adds by itself: the raytraced light
        // added to the frame is this much less. Handed over facing up, sideways and down.
        // The probe Unity shades with: RenderSettings.ambientProbe holds only a baked one.
        internal Func<Vector3, SphericalHarmonicsL2> AmbientProbe = at =>
        {
            SphericalHarmonicsL2 probe;
            LightProbes.GetInterpolatedProbe(at, null, out probe);
            return probe;
        };
        private static readonly Vector3[] AmbientDirections = { Vector3.up, Vector3.right, Vector3.down };
        private readonly Color[] ambientValues = new Color[3];
        // Unity's frame count at which the upscaler last asked for DLSS Ray
        // Reconstruction's frames: until the frame after it, they go undenoised, as Ray
        // Reconstruction denoises the finished frame, and make its guides from their
        // surfaces (UpscalerRig.Reconstruction.cs). A lease, so that nothing holds it on
        // once the upscaler stops asking.
        internal int ReconstructionLease = -1;
        private bool reconstructedBefore;
        // The last frame that made the guides: Unity's frame count, the packet's index,
        // its camera-relative clip-to-world.
        internal int GuidesFrame { get; private set; } = -1;
        internal uint GuidesIndex { get; private set; }
        internal readonly float[] GuidesClipToWorld = new float[16];
        // The raytraced camera, while one is on.
        internal static RtCamera Current { get; private set; }
        internal Camera Camera { get { return cam; } }
        // Whether this frame is a cut: the camera jumped, the history is not its.
        internal Func<bool> Cut = () => false;
        // The camera's motion vectors, where they are rendered and correct.
        internal bool MotionVectors;

        internal string Problem { get; private set; }
        internal uint LastSignals { get; private set; }
        internal float[] PassTimings = new float[8];

        private Camera cam;
        private Material capture;
        private Shader shadows;
        private Shader reflections;
        private CommandBuffer command;
        private CommandBuffer gbufferCopy;
        private CommandBuffer forwardLight;
        private CommandBuffer maskOn, maskOff;
        private Light maskedLight;
        // The sun's colour while this camera draws: Scatterer changes it for the local
        // cameras only, by the eclipse and the atmosphere's extinction, and sets it back
        // after them (SunlightModulator).
        private Color drawnSunColour;
        private int drawnSunFrame = -1;
        private CameraEvent forwardEvent;
        private Material forwardMaterial;
        private Mesh screenQuad;
        private CommandBuffer flagOn;
        private CommandBuffer flagOff;
        private PacketRing ring;
        private RtNative.FramePacket packet;
        private RenderTexture depth, gbuffer0, gbuffer1, gbuffer2, motion, environment;
        private RenderTexture sun, diffuse, specular, forward;
        // The targets' native textures, taken once as they are made: GetNativeTexturePtr
        // waits for the render thread.
        private IntPtr depthHandle, gbuffer0Handle, gbuffer1Handle, gbuffer2Handle, motionHandle, environmentHandle;
        private IntPtr sunHandle, diffuseHandle, specularHandle, forwardHandle;
        // The GPU's instances gathered for the proxy (ReDefinitionRtScatter.compute), and
        // the append buffers' counts.
        private ComputeShader gather;
        private int gatherKernel = -1;
        private bool gatherLooked;
        // The buffers each read-back of the counts belongs to, one set per request in
        // flight.
        private readonly List<ComputeBuffer[]> countedBuffers = new List<ComputeBuffer[]>();
        private RenderTexture gpuInstances;
        private IntPtr gpuInstancesHandle;
        private ComputeBuffer gpuCounts;
        private int width, height;
        private uint frameIndex;
        private bool haveHistory;
        private bool gbufferReady;
        private Matrix4x4 previousWorldToClip;     // camera-relative, of the previous frame
        private RtPosition previousPosition;
        private Matrix4x4 renderedProjection;
        private RtPosition position;
        private readonly StringBuilder statusText = new StringBuilder(512);
        private int generation = -1;
        private int statusFrame;

        private static readonly int RtOn = Shader.PropertyToID("_ReDefinitionRtOn");
        private static readonly int RtSunMask = Shader.PropertyToID("_ReDefinitionRtSunMask");
        private static readonly int RtLight = Shader.PropertyToID("_ReDefinitionRtLight");
        private static readonly int RtPrevViewProj = Shader.PropertyToID("_ReDefinitionRtPrevViewProj");
        private static readonly int RtOffsetNear = Shader.PropertyToID("_ReDefinitionRtOffsetNear");
        private static readonly int RtOffsetFar = Shader.PropertyToID("_ReDefinitionRtOffsetFar");
        private static readonly int RtSun = Shader.PropertyToID("_ReDefinitionRtSun");
        private static readonly int RtDiffuse = Shader.PropertyToID("_ReDefinitionRtDiffuse");
        private static readonly int RtSpecular = Shader.PropertyToID("_ReDefinitionRtSpecular");
        private static readonly int RtCube = Shader.PropertyToID("_ReDefinitionRtCube");
        private static readonly int RtForward = Shader.PropertyToID("_ReDefinitionRtForward");
        private static readonly int RtCubeHdr = Shader.PropertyToID("_ReDefinitionRtCube_HDR");

        private void Awake()
        {
            cam = GetComponent<Camera>();
            packet = RtNative.NewFramePacket();
            ring = new PacketRing(System.Runtime.InteropServices.Marshal.SizeOf(typeof(RtNative.FramePacket)));
        }

        private void OnEnable()
        {
            string why;
            if (!RtNative.Ready(out why))
            {
                Problem = "raytracing does not run: " + why;
                enabled = false;
                return;
            }
            Shader captureShader = ShaderSource("Hidden/ReDefinition/RtCapture");
            shadows = ShaderSource("Hidden/ReDefinition/ScreenSpaceShadows");
            reflections = ShaderSource("ReDefinition/Internal-DeferredReflections");
            if (captureShader == null || shadows == null)
            {
                Problem = "raytracing's shaders are missing from the shader bundle";
                enabled = false;
                return;
            }
            capture = new Material(captureShader) { hideFlags = HideFlags.HideAndDontSave };
            command = new CommandBuffer { name = "ReDefinition raytracing" };

            // The flag the lighting shaders read: on around this camera's rendering,
            // off for every other camera. Forward rendering collects the sun's shadow
            // after the depth texture, deferred rendering after the G-buffer.
            flagOn = new CommandBuffer { name = "ReDefinition raytracing on" };
            flagOn.SetGlobalFloat(RtOn, 1f);
            flagOff = new CommandBuffer { name = "ReDefinition raytracing off" };
            flagOff.SetGlobalFloat(RtOn, 0f);
            cam.AddCommandBuffer(CameraEvent.BeforeDepthTexture, flagOn);
            cam.AddCommandBuffer(CameraEvent.BeforeGBuffer, flagOn);
            cam.AddCommandBuffer(CameraEvent.AfterEverything, flagOff);
            // The G-buffer is gone by OnRenderImage: copied as soon as it is filled, in the
            // camera's own command stream, where the copy belongs to this frame.
            gbufferCopy = new CommandBuffer { name = "ReDefinition raytracing G-buffer" };
            cam.AddCommandBuffer(CameraEvent.AfterGBuffer, gbufferCopy);
            // Without Deferred's reflections pass to take it, the raytraced light goes into
            // the frame after the opaque geometry's light, before the transparent and the
            // atmosphere: after the forward opaque pass, or after Unity's deferred lighting,
            // where the forward opaque pass is left out when nothing is drawn forward.
            Shader forwardShader = ShaderSource("Hidden/ReDefinition/RtForwardLight");
            if (forwardShader != null)
            {
                forwardMaterial = new Material(forwardShader) { hideFlags = HideFlags.HideAndDontSave };
                screenQuad = new Mesh { name = "ReDefinition raytracing screen" };
                screenQuad.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f),
                                              new Vector3(1f, -1f, 0f) };
                screenQuad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                screenQuad.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
                forwardLight = new CommandBuffer { name = "ReDefinition raytracing forward light" };
                forwardEvent = CameraEvent.AfterForwardOpaque;
                cam.AddCommandBuffer(forwardEvent, forwardLight);
            }
            maskOn = new CommandBuffer { name = "ReDefinition raytracing sun mask on" };
            maskOn.SetGlobalFloat(RtSunMask, 1f);
            maskOff = new CommandBuffer { name = "ReDefinition raytracing sun mask off" };
            maskOff.SetGlobalFloat(RtSunMask, 0f);
            cam.depthTextureMode |= DepthTextureMode.Depth;
            haveHistory = false;
            Problem = null;
            Current = this;
        }

        private void OnDisable()
        {
            if (Current == this) Current = null;
            ReconstructionLease = -1;
            if (cam != null)
            {
                if (flagOn != null)
                {
                    cam.RemoveCommandBuffer(CameraEvent.BeforeDepthTexture, flagOn);
                    cam.RemoveCommandBuffer(CameraEvent.BeforeGBuffer, flagOn);
                }
                if (flagOff != null) cam.RemoveCommandBuffer(CameraEvent.AfterEverything, flagOff);
                if (gbufferCopy != null) cam.RemoveCommandBuffer(CameraEvent.AfterGBuffer, gbufferCopy);
                if (forwardLight != null) cam.RemoveCommandBuffer(forwardEvent, forwardLight);
            }
            MaskSun(null);
            maskOn?.Release();
            maskOff?.Release();
            maskOn = maskOff = null;
            Shader.SetGlobalFloat(RtOn, 0f);
            Shader.SetGlobalFloat(RtSunMask, 0f);
            RtLighting.Restore();
            Scene.Clear();
            ReleaseTargets();
            ReleaseGpuInstances();
            if (capture != null) Destroy(capture);
            capture = null;
            command?.Release();
            gbufferCopy?.Release();
            forwardLight?.Release();
            forwardLight = null;
            if (forwardMaterial != null) Destroy(forwardMaterial);
            if (screenQuad != null) Destroy(screenQuad);
            forwardMaterial = null;
            screenQuad = null;
            flagOn?.Release();
            flagOff?.Release();
            command = gbufferCopy = flagOn = flagOff = null;
        }

        private void OnPreCull()
        {
            if (capture == null) return;
            RtLighting.Keep(Settings.Shadows ? shadows : null, Settings.Diffuse || Settings.Reflections ? reflections : null);
            if (SunLight != null) MaskSun(SunLight());
            else Shader.SetGlobalFloat(RtSunMask, 1f);

            // The service's generation, which changes with its device, now and then.
            if (generation < 0 || ++statusFrame >= 30)
            {
                statusFrame = 0;
                statusText.Length = 0;
                generation = RtNative.KspRtStatus(statusText, statusText.Capacity);
            }
            position = RtPosition.From(transform.position);
            Scene.Update(position, generation, frameIndex);

            // The raytraced light into the frame, where Deferred's reflections pass does not
            // take it.
            if (forwardLight != null)
            {
                CameraEvent wanted = cam.actualRenderingPath == RenderingPath.DeferredShading
                    ? CameraEvent.AfterLighting : CameraEvent.AfterForwardOpaque;
                if (wanted != forwardEvent)
                {
                    cam.RemoveCommandBuffer(forwardEvent, forwardLight);
                    forwardEvent = wanted;
                    cam.AddCommandBuffer(forwardEvent, forwardLight);
                }
                forwardLight.Clear();
                bool forwardFrame = !RtLighting.ReflectionsReplaced;
                if (forwardFrame && haveHistory && !Cut() && forward != null && (Settings.Diffuse || Settings.Reflections))
                {
                    Shader.SetGlobalTexture(RtForward, forward);
                    int first = forwardEvent == CameraEvent.AfterLighting && !cam.allowHDR ? 2 : 0;
                    forwardLight.DrawMesh(screenQuad, Matrix4x4.identity, forwardMaterial, 0, first);
                    forwardLight.DrawMesh(screenQuad, Matrix4x4.identity, forwardMaterial, 0, first + 1);
                }
            }

            // The G-buffer's copy at this frame's size, which the previous frame's
            // OnRenderImage found; at a new size, from the next frame on.
            gbufferCopy.Clear();
            if (gbuffer0 != null && cam.actualRenderingPath == RenderingPath.DeferredShading)
            {
                gbufferCopy.Blit(BuiltinRenderTextureType.GBuffer0, gbuffer0);
                gbufferCopy.Blit(BuiltinRenderTextureType.GBuffer1, gbuffer1);
                gbufferCopy.Blit(BuiltinRenderTextureType.GBuffer2, gbuffer2);
            }

            // The previous frame's results, carried to this frame's world.
            bool valid = haveHistory && !Cut();
            Shader.SetGlobalVector(RtLight, new Vector4(valid && Settings.Diffuse ? 1f : 0f,
                                                        valid && Settings.Reflections ? 1f : 0f, 0f, 0f));
            if (valid)
            {
                RtPosition near = WorldShift();
                RtPosition far = WorldShiftFar != null ? WorldShiftFar() : near;
                Shader.SetGlobalMatrix(RtPrevViewProj, previousWorldToClip);
                Shader.SetGlobalVector(RtOffsetNear, new Vector4((float)(near.x - previousPosition.x),
                                                                 (float)(near.y - previousPosition.y),
                                                                 (float)(near.z - previousPosition.z), 0f));
                Shader.SetGlobalVector(RtOffsetFar, new Vector4((float)(far.x - previousPosition.x),
                                                                (float)(far.y - previousPosition.y),
                                                                (float)(far.z - previousPosition.z), 0f));
                if (sun != null) Shader.SetGlobalTexture(RtSun, sun);
                if (diffuse != null) Shader.SetGlobalTexture(RtDiffuse, diffuse);
                if (specular != null) Shader.SetGlobalTexture(RtSpecular, specular);
            }
            else
            {
                // Nothing to carry: fully lit, no raytraced light until the first frame.
                Shader.SetGlobalMatrix(RtPrevViewProj, Matrix4x4.zero);
            }
        }

        private void OnPreRender()
        {
            renderedProjection = cam.projectionMatrix;
        }

        // After this camera's scene, before its OnPostRender, where the sun's colour is
        // still the one it drew with.
        private void OnRenderObject()
        {
            if (Camera.current != cam) return;
            drawnSunColour = SunColor();
            drawnSunFrame = Time.frameCount;
        }

        // The mask flag on the sun's light, moved when the sun is another light now.
        private void MaskSun(Light light)
        {
            if (light == maskedLight) return;
            if (maskedLight != null)
            {
                maskedLight.RemoveCommandBuffer(LightEvent.BeforeScreenspaceMask, maskOn);
                maskedLight.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, maskOff);
            }
            maskedLight = light;
            Shader.SetGlobalFloat(RtSunMask, 0f);
            if (light != null && maskOn != null)
            {
                light.AddCommandBuffer(LightEvent.BeforeScreenspaceMask, maskOn);
                light.AddCommandBuffer(LightEvent.AfterScreenspaceMask, maskOff);
            }
        }

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (capture == null || source == null)
            {
                Graphics.Blit(source, destination);
                return;
            }
            EnsureTargets(source.width, source.height);
            // The jittered projection where an upscaler set one, read once the frame is drawn.
            if (RenderedProjection != null) renderedProjection = RenderedProjection();

            command.Clear();
            command.Blit(source, depth, capture, 0);
            // The G-buffer was copied after it was filled; at a new size, not yet.
            bool deferred = cam.actualRenderingPath == RenderingPath.DeferredShading && gbufferReady;
            gbufferReady = cam.actualRenderingPath == RenderingPath.DeferredShading;
            bool withMotion = MotionVectors && (cam.depthTextureMode & DepthTextureMode.MotionVectors) != 0;
            if (withMotion) command.Blit(source, motion, capture, 4);
            Texture cube = Environment();
            if (cube != null)
            {
                command.SetGlobalTexture(RtCube, cube);
                command.SetGlobalVector(RtCubeHdr, new Vector4(1f, 1f, 0f, 0f));
                command.Blit(source, environment, capture, 5);
            }

            bool withGpu = GatherGpuInstances();
            FillPacket(deferred, withMotion, cube != null);
            if (withGpu) packet.Inputs[RtNative.InputInstances] = gpuInstancesHandle;
            ring.Issue(command, RtNative.RenderEvent, RtNative.FrameEvent, ref packet);
            Graphics.ExecuteCommandBuffer(command);
            Graphics.Blit(source, destination);

            previousWorldToClip = WorldToClipRelative();
            previousPosition = position;
            haveHistory = true;
            frameIndex++;
        }

        // The GPU's instances of this frame into the texture the proxy reads them from,
        // relative to the camera; false where there are none.
        private bool GatherGpuInstances()
        {
            int total = Scene.GpuTotal;
            if (total == 0) return false;
            if (!gatherLooked)
            {
                // Once: a kernel that did not compile for this device throws each time.
                gatherLooked = true;
                gather = ComputeSource("ReDefinitionRtScatter");
                try
                {
                    gatherKernel = gather != null ? gather.FindKernel("Gather") : -1;
                }
                catch (Exception)
                {
                    gatherKernel = -1;
                }
            }
            if (gatherKernel < 0) return false;
            int rows = (total + RtNative.InstancesPerRow - 1) / RtNative.InstancesPerRow;
            if (gpuInstances == null || gpuInstances.height < rows)
            {
                if (gpuInstances != null)
                {
                    gpuInstances.Release();
                    Destroy(gpuInstances);
                }
                gpuInstances = new RenderTexture(RtNative.InstancesPerRow * 3, Math.Max(rows, 16), 0, RenderTextureFormat.ARGBFloat,
                                                 RenderTextureReadWrite.Linear)
                {
                    name = "ReDefinition raytracing GPU instances",
                    enableRandomWrite = true,
                    filterMode = FilterMode.Point,
                };
                gpuInstances.Create();
                // Once: GetNativeTexturePtr waits for the render thread.
                gpuInstancesHandle = gpuInstances.GetNativeTexturePtr();
            }
            int items = Scene.GpuItems.Count;
            if (gpuCounts == null || gpuCounts.count < items)
            {
                gpuCounts?.Release();
                gpuCounts = new ComputeBuffer(Math.Max(items, 64), 4, ComputeBufferType.Raw);
            }
            Vector3 eye = transform.position;
            command.SetComputeVectorParam(gather, "_Eye", new Vector4(eye.x, eye.y, eye.z, 0f));
            command.SetComputeTextureParam(gather, gatherKernel, "_Out", gpuInstances);
            command.SetComputeBufferParam(gather, gatherKernel, "_Counts", gpuCounts);
            var counted = new ComputeBuffer[items];
            for (int i = 0; i < items; i++)
            {
                RtGpuItem item = Scene.GpuItems[i];
                counted[i] = item.Transforms;
                int places = Scene.GpuPlaces[i];
                command.CopyCounterValue(item.Transforms, gpuCounts, (uint)(i * 4));
                command.SetComputeBufferParam(gather, gatherKernel, "_Source", item.Transforms);
                command.SetComputeIntParam(gather, "_CountIndex", i);
                command.SetComputeIntParam(gather, "_Capacity", places);
                command.SetComputeIntParam(gather, "_First", Scene.GpuFirst[i]);
                command.DispatchCompute(gather, gatherKernel, (places + 63) / 64, 1, 1);
            }
            // How many places each buffer took, for the next frames' reservations.
            if (SystemInfo.supportsAsyncGPUReadback && countedBuffers.Count < 4)
            {
                countedBuffers.Add(counted);
                command.RequestAsyncReadback(gpuCounts, items * 4, 0, request =>
                {
                    countedBuffers.Remove(counted);
                    if (request.hasError) return;
                    Unity.Collections.NativeArray<uint> taken = request.GetData<uint>();
                    for (int i = 0; i < counted.Length && i < taken.Length; i++)
                        Scene.NoteGpuTaken(counted[i], (int)taken[i]);
                });
            }
            return true;
        }

        // The projection as the GPU used it, into a render texture, times the camera's
        // rotation: camera-relative world to clip.
        private Matrix4x4 WorldToClipRelative()
        {
            Matrix4x4 view = cam.worldToCameraMatrix;
            view.m03 = 0f;
            view.m13 = 0f;
            view.m23 = 0f;
            return GL.GetGPUProjectionMatrix(renderedProjection, true) * view;
        }

        private void FillPacket(bool deferred, bool withMotion, bool withEnvironment)
        {
            Matrix4x4 worldToClip = WorldToClipRelative();
            Write(worldToClip, packet.WorldToClip);
            Write(StreamlineCamera.Invert(worldToClip), packet.ClipToWorld);
            if (haveHistory)
            {
                RtPosition shift = WorldShift();
                Matrix4x4 toPrevious = Translation(position.x + shift.x - previousPosition.x,
                                                   position.y + shift.y - previousPosition.y,
                                                   position.z + shift.z - previousPosition.z);
                Write(previousWorldToClip * toPrevious, packet.PrevWorldToClip);
            }
            else
                Write(worldToClip, packet.PrevWorldToClip);

            Vector4 towards = Sun();
            Vector3 direction = new Vector3(towards.x, towards.y, towards.z).normalized;
            packet.SunDirection[0] = direction.x;
            packet.SunDirection[1] = direction.y;
            packet.SunDirection[2] = direction.z;
            packet.SunDirection[3] = towards.w;
            Color sunColour = drawnSunFrame == Time.frameCount ? drawnSunColour : SunColor();
            packet.SunColor[0] = sunColour.r;
            packet.SunColor[1] = sunColour.g;
            packet.SunColor[2] = sunColour.b;
            Color sky = SkyColor();
            packet.SkyColor[0] = sky.r;
            packet.SkyColor[1] = sky.g;
            packet.SkyColor[2] = sky.b;
            bool composite = !RtLighting.ReflectionsReplaced;
            for (int i = 0; i < 12; i++) packet.Ambient[i] = 0f;
            if (composite)
            {
                AmbientProbe(transform.position).Evaluate(AmbientDirections, ambientValues);
                for (int i = 0; i < 3; i++)
                {
                    packet.Ambient[i * 4] = ambientValues[i].r;
                    packet.Ambient[i * 4 + 1] = ambientValues[i].g;
                    packet.Ambient[i * 4 + 2] = ambientValues[i].b;
                }
                packet.Ambient[3] = QualitySettings.activeColorSpace == ColorSpace.Gamma ? 1f : 0f;
            }

            packet.Params[0] = Settings.OcclusionDistance;
            packet.Params[1] = Settings.Bounces;
            packet.Params[2] = 100000f;
            packet.Params[3] = 100000f;
            packet.Params[4] = Settings.Clamp;
            packet.Params[5] = withEnvironment ? 1f : 0f;
            packet.Params[6] = 1f;
            packet.Params[7] = deferred ? 1f : 0f;
            Vector4 body = Body();
            packet.BodySphere[0] = (float)(body.x - position.x);
            packet.BodySphere[1] = (float)(body.y - position.y);
            packet.BodySphere[2] = (float)(body.z - position.z);
            packet.BodySphere[3] = body.w;

            // The sun's pass always: its output keeps the view depth the next frame finds
            // its pixels by.
            uint signals = RtNative.SignalSun;
            if (Settings.Diffuse) signals |= RtNative.SignalDiffuse;
            if (Settings.Reflections) signals |= RtNative.SignalSpecular;
            // Where Deferred's reflections pass does not take it -- forward rendering, or
            // Unity's own deferred lighting --, the light to add to the frame; it reads
            // the diffuse light, which replaces the flat ambient light.
            if (composite && (Settings.Diffuse || Settings.Reflections))
                signals |= RtNative.SignalForwardLight | RtNative.SignalDiffuse;
            packet.Signals = signals;
            LastSignals = signals;
            packet.FrameIndex = frameIndex;
            packet.Width = (uint)width;
            packet.Height = (uint)height;
            // The denoiser's history stood still while Ray Reconstruction ran: afresh when
            // it comes back.
            bool reconstruct = ReconstructionLease >= Time.frameCount - 1;
            bool switched = reconstruct != reconstructedBefore;
            reconstructedBefore = reconstruct;
            packet.Flags = RtNative.FlagSameFrame | (Settings.Denoise && !reconstruct ? RtNative.FlagDenoise : 0u)
                           | (!haveHistory || Cut() || switched ? RtNative.FlagReset : 0u)
                           | (withMotion ? RtNative.FlagMotionVectors : 0u)
                           | (reconstruct ? RtNative.FlagGuides : 0u);
            if (reconstruct)
            {
                GuidesFrame = Time.frameCount;
                GuidesIndex = frameIndex;
                Array.Copy(packet.ClipToWorld, GuidesClipToWorld, 16);
            }

            for (int i = 0; i < 8; i++)
            {
                packet.Inputs[i] = IntPtr.Zero;
                packet.Outputs[i] = IntPtr.Zero;
            }
            packet.Inputs[RtNative.InputDepth] = depthHandle;
            if (deferred)
            {
                // Without a G-buffer the proxy takes each pixel's surface from its rays.
                packet.Inputs[RtNative.InputGBuffer0] = gbuffer0Handle;
                packet.Inputs[RtNative.InputGBuffer1] = gbuffer1Handle;
                packet.Inputs[RtNative.InputGBuffer2] = gbuffer2Handle;
            }
            if (withMotion) packet.Inputs[RtNative.InputMotion] = motionHandle;
            if (withEnvironment) packet.Inputs[RtNative.InputEnvironment] = environmentHandle;
            if ((signals & RtNative.SignalSun) != 0) packet.Outputs[RtNative.OutputSun] = sunHandle;
            if ((signals & RtNative.SignalDiffuse) != 0) packet.Outputs[RtNative.OutputDiffuse] = diffuseHandle;
            if ((signals & RtNative.SignalSpecular) != 0)
                packet.Outputs[RtNative.OutputSpecular] = specularHandle;
            if ((signals & RtNative.SignalForwardLight) != 0)
                packet.Outputs[RtNative.OutputForwardLight] = forwardHandle;
        }

        private static Matrix4x4 Translation(double x, double y, double z)
        {
            Matrix4x4 m = Matrix4x4.identity;
            m.m03 = (float)x;
            m.m13 = (float)y;
            m.m23 = (float)z;
            return m;
        }

        // Rows first, as the proxy takes them: Unity's [row, column].
        internal static void Write(Matrix4x4 m, float[] target)
        {
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                    target[row * 4 + column] = m[row, column];
        }

        private void EnsureTargets(int newWidth, int newHeight)
        {
            // Made again where Unity let a texture go, as its native handle goes with it.
            if (depth != null && width == newWidth && height == newHeight && depth.IsCreated() && sun.IsCreated()) return;
            ReleaseTargets();
            width = newWidth;
            height = newHeight;
            depth = Target(RenderTextureFormat.RFloat);
            gbuffer0 = Target(RenderTextureFormat.ARGB32);
            gbuffer1 = Target(RenderTextureFormat.ARGB32);
            gbuffer2 = Target(RenderTextureFormat.ARGB2101010);
            motion = Target(RenderTextureFormat.RGHalf);
            environment = new RenderTexture(256, 128, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = "ReDefinition raytracing sky",
                wrapMode = TextureWrapMode.Repeat,
            };
            environment.Create();
            sun = Target(RenderTextureFormat.ARGBHalf);
            diffuse = Target(RenderTextureFormat.ARGBHalf);
            specular = Target(RenderTextureFormat.ARGBHalf);
            forward = Target(RenderTextureFormat.ARGBHalf);
            depthHandle = depth.GetNativeTexturePtr();
            gbuffer0Handle = gbuffer0.GetNativeTexturePtr();
            gbuffer1Handle = gbuffer1.GetNativeTexturePtr();
            gbuffer2Handle = gbuffer2.GetNativeTexturePtr();
            motionHandle = motion.GetNativeTexturePtr();
            environmentHandle = environment.GetNativeTexturePtr();
            sunHandle = sun.GetNativeTexturePtr();
            diffuseHandle = diffuse.GetNativeTexturePtr();
            specularHandle = specular.GetNativeTexturePtr();
            forwardHandle = forward.GetNativeTexturePtr();
            haveHistory = false;
            gbufferReady = false;
        }

        private RenderTexture Target(RenderTextureFormat format)
        {
            var texture = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear)
            {
                name = "ReDefinition raytracing",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.Create();
            return texture;
        }

        private void ReleaseGpuInstances()
        {
            if (gpuInstances != null)
            {
                gpuInstances.Release();
                Destroy(gpuInstances);
            }
            gpuInstances = null;
            gpuInstancesHandle = IntPtr.Zero;
            gpuCounts?.Release();
            gpuCounts = null;
        }

        private void ReleaseTargets()
        {
            foreach (RenderTexture texture in new[] { depth, gbuffer0, gbuffer1, gbuffer2, motion, environment, sun, diffuse,
                                                      specular, forward })
                if (texture != null)
                {
                    texture.Release();
                    Destroy(texture);
                }
            depth = gbuffer0 = gbuffer1 = gbuffer2 = motion = environment = sun = diffuse = specular = forward = null;
            depthHandle = gbuffer0Handle = gbuffer1Handle = gbuffer2Handle = motionHandle = environmentHandle = IntPtr.Zero;
            sunHandle = diffuseHandle = specularHandle = forwardHandle = IntPtr.Zero;
        }

        // The textures of the last frame, for the test player's and the diagnostics' look.
        internal RenderTexture DepthTexture { get { return depth; } }
        internal RenderTexture SunTexture { get { return sun; } }
        internal RenderTexture DiffuseTexture { get { return diffuse; } }
        internal RenderTexture NormalTexture { get { return gbuffer2; } }
        internal RenderTexture EnvironmentTexture { get { return environment; } }
        internal RenderTexture ForwardTexture { get { return forward; } }

        // The service's line and each pass's time, for the diagnostics.
        internal string Status()
        {
            statusText.Length = 0;
            RtNative.KspRtStatus(statusText, statusText.Capacity);
            RtNative.KspRtPassTimings(PassTimings, PassTimings.Length);
            return statusText.ToString();
        }
    }
}
