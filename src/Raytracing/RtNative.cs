using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ReDefinition.Raytracing
{
    // The proxy's raytracing service (src/DxgiProxy/Raytracing.h), as the managed
    // side reaches it: exports called from the main thread, and packets for the
    // render events 30 (a frame) and 31 (DLSS Ray Reconstruction); event 32 frees
    // Ray Reconstruction's resources once it is no longer used, event 33 what the
    // frames made once raytracing is no longer used.
    //
    // Free of KSP's types, like everything in this folder but the sources that
    // collect KSP's scene: the raytracing test player compiles it on its own.
    //
    // Its constants and the packets' layouts are the contract's
    // (src/DxgiProxy/RaytracingShaders/RtContract.h, RT_<NAME>), which
    // tests/ReDefinition.Tests holds them to (RtContractTests).
    internal static class RtNative
    {
        // The proxy the exports are in, the game's dxgi.dll next to KSP_x64.exe.
        private const string Proxy = "dxgi.dll";

        // The render events' ids for RenderEvent: a frame, Ray Reconstruction, and the two releases.
        internal const int FrameEvent = 30;
        internal const int ReconstructEvent = 31;
        internal const int ReconstructReleaseEvent = 32;
        internal const int ReleaseEvent = 33;

        // Each structure's second word, which the proxy checks to reject what is not one of them.
        internal const uint MeshMagic = 0x4B53524Du;        // 'KSRM', in a MeshDesc
        internal const uint MaterialMagic = 0x4B535241u;    // 'KSRA', in a MaterialDesc
        internal const uint FrameMagic = 0x4B535246u;       // 'KSRF', in a FramePacket
        internal const uint ReconstructMagic = 0x4B535252u; // 'KSRR', in a ReconstructPacket

        // A frame's flags (FramePacket.Flags, ReconstructPacket.Flags; Raytracing.h, kRtFrame*, says what each does):
        // same frame (accepted without effect), reset the history, denoise, motion vectors given, Ray
        // Reconstruction's guides, a further trace (the ocean's), water, the water's normals for the probe, the
        // G-buffer's smoothness from legacy shaders, the surfaces' intent, and the clouds' shadow alone.
        internal const uint FlagSameFrame = 1u << 0;
        internal const uint FlagReset = 1u << 1;
        internal const uint FlagDenoise = 1u << 2;
        internal const uint FlagMotionVectors = 1u << 3;
        internal const uint FlagGuides = 1u << 4;
        internal const uint FlagExtra = 1u << 5;
        internal const uint FlagWater = 1u << 6;
        internal const uint FlagWaterNormals = 1u << 7;
        internal const uint FlagLegacySmoothness = 1u << 8;
        internal const uint FlagSurfaceIntent = 1u << 9;
        internal const uint FlagCloudShadow = 1u << 10;

        // The signals a frame traces (FramePacket.Signals), each into its output slot: the sun's visibility, the
        // ambient occlusion, the diffuse and the specular light, and the light to add to a forward-rendered frame.
        internal const uint SignalSun = 1u << 0;
        internal const uint SignalOcclusion = 1u << 1;
        internal const uint SignalDiffuse = 1u << 2;
        internal const uint SignalSpecular = 1u << 3;
        internal const uint SignalForwardLight = 1u << 4;

        // The input slots of FramePacket.Inputs: the depth as Direct3D's clip-space z, the G-buffer in Unity's
        // layout, the motion vectors, and the sky as a latitude-longitude image.
        internal const int InputDepth = 0;
        internal const int InputGBuffer0 = 1;
        internal const int InputGBuffer1 = 2;
        internal const int InputGBuffer2 = 3;
        internal const int InputMotion = 4;
        internal const int InputEnvironment = 5;
        // Instances the GPU made, three texels each, InstancesPerRow to a row (Raytracing.h).
        internal const int InputInstances = 6;
        // The main body's cloud layer (RtEveClouds; RtContract.h, RT_INPUT_CLOUDS).
        internal const int InputClouds = 7;
        // Instance flags (Raytracing.h, kRtInstance*).
        internal const uint InstanceTerrain = 1u << 0;
        internal const uint InstanceScenery = 1u << 2;
        internal const int InstancesPerRow = 256;
        // Instances and groups for every frame without its own.
        internal const uint AnyFrame = 0xFFFFFFFFu;

        // The output slots of FramePacket.Outputs, one for each signal; Surface and SurfaceMode the surfaces as their
        // materials mean them and how each pixel's were found (FlagSurfaceIntent).
        internal const int OutputSun = 0;
        internal const int OutputDiffuse = 1;
        internal const int OutputSpecular = 2;
        internal const int OutputOcclusion = 3;
        internal const int OutputForwardLight = 4;
        internal const int OutputSurface = 5;
        internal const int OutputSurfaceMode = 6;
        // Without a G-buffer: how the diffuse light changes the frame's colour (Raytracing.hlsl,
        // ForwardLight).
        internal const int OutputForwardRatio = 7;

        // How a material's shine is meant (RtMaterialIntent; RtContract.h, RT_SURFACE_*).
        internal const uint SurfaceKeep = 0;
        internal const uint SurfaceDry = 1;
        internal const uint SurfaceLegacy = 2;
        internal const uint SurfaceFixed = 3;
        internal const uint SurfaceUnityLegacy = 4;
        internal const uint SurfaceWater = 5;
        internal const uint SurfaceMissed = 6;
        internal const uint SurfaceGround = 7;

        // The material textures' side in texels: each material's main texture, copied small into one layer.
        internal const int MaterialTextureSize = 64;
        // As Raytracing.h: a Direct3D 12 texture array's most layers.
        internal const int MaxMaterialTextures = 2048;

        // A mesh for KspRtSetMesh, as RtMeshDesc in Raytracing.h; the proxy copies the arrays before the call returns.
        [StructLayout(LayoutKind.Sequential)]
        internal struct MeshDesc
        {
            // Its size in bytes and MeshMagic, the id this side gives the mesh, the counts, Flags reserved (0); then
            // VertexCount positions and normals (three floats each), texture coordinates (two, or zero for none),
            // and IndexCount indices, three per triangle.
            public uint Size;
            public uint Magic;
            public int Mesh;
            public uint VertexCount;
            public uint IndexCount;
            public uint Flags;
            public IntPtr Positions;
            public IntPtr Normals;
            public IntPtr Uvs;
            public IntPtr Indices;
            // RGBA bytes a vertex, as Color32, or zero for white.
            public IntPtr Colors;
        }

        // A material for KspRtSetMaterial, as RtMaterialDesc in Raytracing.h.
        [StructLayout(LayoutKind.Sequential)]
        internal struct MaterialDesc
        {
            // Its size in bytes and MaterialMagic, the id this side gives the material, and how its shine is meant
            // (Surface*).
            public uint Size;
            public uint Magic;
            public int Material;
            public uint Flags;
            // Linear albedo, and in A the alpha below which its texture cuts the surface out (0 for none); the
            // specular colour, the smoothness as Unity's standard shading takes it, the emitted radiance, and the
            // layer of the material textures (KspRtSetMaterialTexture), -1 for none.
            public float AlbedoR, AlbedoG, AlbedoB, AlbedoA;
            public float SpecularR, SpecularG, SpecularB;
            public float Smoothness;
            public float EmissionR, EmissionG, EmissionB;
            public int Texture;
            // Above 0: the albedo is in Unity's colour space and the vertex colour times
            // this is added to it (RtTerrainColour); the texture is not used.
            public float VertexColour;
            // How its shine is meant is Flags (Surface*); for the legacy families what the
            // G-buffer's smoothness or specular colour is divided by for the gloss, and the gloss
            // where no G-buffer gives it.
            public float GlossDivisor, GlossMean;
            // What of the frame's colour the forward light keeps as diffuse (RtDeferredLook);
            // 0 for all of it.
            public float DiffuseShare;
            // The sun's highlight the material's shader draws (RtForwardHighlight).
            public float HighlightR, HighlightG, HighlightB, HighlightShape;
        }

        // One instance of a mesh in a frame (KspRtSetInstances), as RtInstance in Raytracing.h.
        [StructLayout(LayoutKind.Sequential)]
        internal struct Instance
        {
            // The 3x4 transform, rows first, from the mesh into camera-relative space.
            public float M00, M01, M02, M03;
            public float M10, M11, M12, M13;
            public float M20, M21, M22, M23;
            // The mesh's and the material's ids, the ray mask (bits 0-7), and the flags (Instance*).
            public int Mesh;
            public int Material;
            public uint Mask;
            public uint Flags;
        }

        // A run of the GPU's instances, as RtGpuInstanceGroup in Raytracing.h.
        [StructLayout(LayoutKind.Sequential)]
        internal struct GpuGroup
        {
            // The mesh and material its instances are, where the run starts in InputInstances and how long it is,
            // the ray mask and flags each instance takes; the reserved words 0.
            public int Mesh;
            public int Material;
            public uint First;
            public uint Count;
            public uint Mask;
            public uint Flags;
            public uint Reserved0;
            public uint Reserved1;
        }

        // Event 30's packet, as RtFramePacket in Raytracing.h, which says each field's meaning in full: a frame to
        // trace. Matrices rows first, for column vectors and camera-relative positions.
        [StructLayout(LayoutKind.Sequential)]
        internal struct FramePacket : Bridges.IRingPacket<FramePacket>
        {
            // Its size in bytes and FrameMagic, the frame's index (the instances' and groups' frameIndex), its
            // flags (Flag*), the size in pixels it is traced at, the signals it traces (Signal*); Reserved 0.
            public uint Size;
            public uint Magic;
            public uint FrameIndex;
            public uint Flags;
            public uint Width;
            public uint Height;
            public uint Signals;
            public uint Reserved;
            // The camera: clip to camera-relative world and back, and last frame's world to clip.
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] ClipToWorld;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] WorldToClip;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] PrevWorldToClip;
            // Towards the sun with its angular radius in w, its radiance at the scene, linear, and the sky's where
            // no environment is given.
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] SunDirection;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] SunColor;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] SkyColor;
            // The occlusion's distance, the bounces, the diffuse and specular rays' distances, the samples' clamp,
            // and 1 for each of: an environment given, material textures, a G-buffer (RtCommon.hlsli, params0/1).
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public float[] Params;
            // The main body: its centre relative to the camera and its radius, 0 for none.
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] BodySphere;
            // Unity's ambient light, which its shading added already, linear: facing up (w 1 where Unity shades in
            // gamma space), sideways, down.
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)] public float[] Ambient;
            // The camera as AMD FSR Ray Regeneration takes it: the view looking down +z and the projection without
            // the jitter, rows first for row vectors; the previous position minus this one; the jitter in pixels;
            // the near and far planes.
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] WorldToView;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] ViewToClip;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] CameraDelta;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public float[] Jitter;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public float[] DepthRange;
            // Up to two bodies lit by the sun as lights (RtReflectors): towards each with its angular radius, its
            // radiance with how far its shadow ray goes. LightParams: how many are used, 1 for gamma-space colours,
            // how strongly water mirrors the sun's disc, 1 where the sea is KSP's own.
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] Lights;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] LightParams;
            // The least diffuse light the forward light gives a point, as Unity's colours are.
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] NightLight;
            // The main body's albedo, linear, for rays that meet its sphere; w the time for KSP's own sea's waves.
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] Planet;
            // Per light, its body's surface lit by the star (albedo times the star's colour), for a reflected ray
            // that meets its disc.
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public float[] LightSurface;
            // The sea's frame, fixed to the main body (RtCamera.Sea): a point on the sea
            // relative to the camera, its east and north, each with a w; all 0 for none.
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)] public float[] Sea;
            // The main body's cloud layer (Raytracing.h, RtFramePacket.clouds; RtEveClouds).
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)] public float[] Clouds;
            // PrevWorldToClip for what the bodies carry (Raytracing.h, prevWorldToClipFar).
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] PrevWorldToClipFar;
            // How Unity fades the sun's shadow out in forward rendering (Raytracing.h,
            // RtFramePacket.shadowFade; RtCamera.ShadowFadeRead), and the meadow's shade (RtCamera.FillPacket).
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)] public float[] ShadowFade;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public IntPtr[] Inputs;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public IntPtr[] Outputs;

            // PacketRing's copy into its one boxed packet: the arrays by reference, which marshalling then copies.
            void Bridges.IRingPacket<FramePacket>.CopyFrom(ref FramePacket packet)
            {
                this = packet;
            }

            // Into `target`, whose arrays are its own: every field, the arrays' contents.
            internal void CopyTo(ref FramePacket target)
            {
                target.Size = Size;
                target.Magic = Magic;
                target.FrameIndex = FrameIndex;
                target.Flags = Flags;
                target.Width = Width;
                target.Height = Height;
                target.Signals = Signals;
                target.Reserved = Reserved;
                Array.Copy(ClipToWorld, target.ClipToWorld, ClipToWorld.Length);
                Array.Copy(WorldToClip, target.WorldToClip, WorldToClip.Length);
                Array.Copy(PrevWorldToClip, target.PrevWorldToClip, PrevWorldToClip.Length);
                Array.Copy(SunDirection, target.SunDirection, SunDirection.Length);
                Array.Copy(SunColor, target.SunColor, SunColor.Length);
                Array.Copy(SkyColor, target.SkyColor, SkyColor.Length);
                Array.Copy(Params, target.Params, Params.Length);
                Array.Copy(BodySphere, target.BodySphere, BodySphere.Length);
                Array.Copy(Ambient, target.Ambient, Ambient.Length);
                Array.Copy(WorldToView, target.WorldToView, WorldToView.Length);
                Array.Copy(ViewToClip, target.ViewToClip, ViewToClip.Length);
                Array.Copy(CameraDelta, target.CameraDelta, CameraDelta.Length);
                Array.Copy(Jitter, target.Jitter, Jitter.Length);
                Array.Copy(DepthRange, target.DepthRange, DepthRange.Length);
                Array.Copy(Lights, target.Lights, Lights.Length);
                Array.Copy(LightParams, target.LightParams, LightParams.Length);
                Array.Copy(NightLight, target.NightLight, NightLight.Length);
                Array.Copy(Planet, target.Planet, Planet.Length);
                Array.Copy(LightSurface, target.LightSurface, LightSurface.Length);
                Array.Copy(Sea, target.Sea, Sea.Length);
                Array.Copy(Clouds, target.Clouds, Clouds.Length);
                Array.Copy(PrevWorldToClipFar, target.PrevWorldToClipFar, PrevWorldToClipFar.Length);
                Array.Copy(ShadowFade, target.ShadowFade, ShadowFade.Length);
                Array.Copy(Inputs, target.Inputs, Inputs.Length);
                Array.Copy(Outputs, target.Outputs, Outputs.Length);
            }
        }

        // Event 31's packet, as RtReconstructPacket in Raytracing.h: DLSS Ray
        // Reconstruction for the frame whose raytracing made its guides.
        [StructLayout(LayoutKind.Sequential)]
        internal struct ReconstructPacket : Bridges.IRingPacket<ReconstructPacket>
        {
            // Its size in bytes and ReconstructMagic, the FrameIndex of the raytraced frame whose guides it takes,
            // FlagReset and FlagLegacySmoothness, the render and output sizes in pixels, sl::DLSSMode's and
            // sl::DLSSDPreset's values; the reserved words 0.
            public uint Size;
            public uint Magic;
            public uint FrameIndex;
            public uint Flags;
            public uint RenderWidth;
            public uint RenderHeight;
            public uint OutputWidth;
            public uint OutputHeight;
            public uint Mode;
            public uint Preset;
            public uint Reserved0;
            public uint Reserved1;
            // ClipToWorld as the raytraced frame had it, for the guides; the other matrices as Streamline's
            // constants take them, without the jitter, the view looking down +z; the jitter in pixels and the
            // motion vectors' scale to Streamline's normalised units; the camera's axes, its planes and its
            // vertical field of view.
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] ClipToWorld;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] ViewToClip;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] ClipToView;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] ClipToPrevClip;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] PrevClipToClip;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] WorldToView;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] ViewToWorld;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public float[] Jitter;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public float[] MotionScale;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] Up;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] Right;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] Forward;
            public float NearPlane;
            public float FarPlane;
            public float VerticalFov;
            public float Reserved2;
            // The textures' native pointers: the noisy frame at the render size, the depth, the motion vectors,
            // the G-buffer and the reflections for guides where the raytraced frame made none (the mod leaves them
            // zero), and the output at the output size.
            public IntPtr Colour;
            public IntPtr Depth;
            public IntPtr Motion;
            public IntPtr GBuffer0;
            public IntPtr GBuffer1;
            public IntPtr GBuffer2;
            public IntPtr Specular;
            public IntPtr Output;

            // PacketRing's copy into its one boxed packet: the arrays by reference, which marshalling then copies.
            void Bridges.IRingPacket<ReconstructPacket>.CopyFrom(ref ReconstructPacket packet)
            {
                this = packet;
            }
        }

        // A Ray Reconstruction packet with its size, magic and arrays in place, the rest for the caller to fill.
        internal static ReconstructPacket NewReconstructPacket()
        {
            return new ReconstructPacket
            {
                Size = (uint)Marshal.SizeOf(typeof(ReconstructPacket)),
                Magic = ReconstructMagic,
                ClipToWorld = new float[16],
                ViewToClip = new float[16],
                ClipToView = new float[16],
                ClipToPrevClip = new float[16],
                PrevClipToClip = new float[16],
                WorldToView = new float[16],
                ViewToWorld = new float[16],
                Jitter = new float[2],
                MotionScale = new float[2],
                Up = new float[4],
                Right = new float[4],
                Forward = new float[4],
            };
        }

        // A frame packet with its size, magic and arrays in place, the rest for the caller to fill.
        internal static FramePacket NewFramePacket()
        {
            return new FramePacket
            {
                Size = (uint)Marshal.SizeOf(typeof(FramePacket)),
                Magic = FrameMagic,
                ClipToWorld = new float[16],
                WorldToClip = new float[16],
                PrevWorldToClip = new float[16],
                SunDirection = new float[4],
                SunColor = new float[4],
                SkyColor = new float[4],
                Params = new float[8],
                BodySphere = new float[4],
                Ambient = new float[12],
                WorldToView = new float[16],
                ViewToClip = new float[16],
                CameraDelta = new float[4],
                Jitter = new float[2],
                DepthRange = new float[2],
                Lights = new float[16],
                LightParams = new float[4],
                NightLight = new float[4],
                Planet = new float[4],
                LightSurface = new float[8],
                Sea = new float[12],
                Clouds = new float[24],
                PrevWorldToClipFar = new float[16],
                ShadowFade = new float[12],
                Inputs = new IntPtr[8],
                Outputs = new IntPtr[8],
            };
        }

        // Whether raytracing runs here: 1 ready, 0 no device yet, -1 not possible; the reason in buffer.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int KspRtAvailable(StringBuilder buffer, int size);

        // The proxy's sizes of MeshDesc, MaterialDesc and Instance, and FramePacket's as the result (Ready).
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint KspRtStructSizes(out uint mesh, out uint material, out uint instance);

        // A mesh under its id, copied before the call returns: 1 taken, 0 rejected (KspRtStatus says why).
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int KspRtSetMesh(ref MeshDesc desc);

        // Frees the mesh of that id.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void KspRtReleaseMesh(int mesh);

        // A material under its id: 1 taken, 0 rejected.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int KspRtSetMaterial(ref MaterialDesc desc);

        // Frees the material of that id.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void KspRtReleaseMaterial(int material);

        // A layer of the material textures: size by size RGBA8 pixels, sRGB, rows from the top; 1 taken.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int KspRtSetMaterialTexture(int layer, byte[] pixels, int size);

        // The instances of the frame frameIndex (AnyFrame: of every frame without its own), copied: 1 taken.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int KspRtSetInstances(Instance[] instances, int count, uint frameIndex);

        // The groups of the GPU's instances for the frame frameIndex, as KspRtSetInstances takes its instances.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int KspRtSetGpuGroups(GpuGroup[] groups, int count, uint frameIndex);

        // The service's status line into buffer; returns its generation, which changes with the device, when the
        // meshes are to be handed over again.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int KspRtStatus(StringBuilder buffer, int size);

        // The service's generation, and the one this side's ids are of (Raytracing.h,
        // HandOverFor): what is handed over for an old one is dropped.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint KspRtGeneration();

        // Tells the service which generation the ids handed over from now on are of.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void KspRtHandOverFor(uint generation);

        // Each pass's GPU time in milliseconds by signal bit, the radiance cache's in 5 and the denoiser's in 6,
        // up to count of them.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void KspRtPassTimings([Out] float[] timings, int count);

        // Whether DLSS Ray Reconstruction runs: 1 yes, 0 no, with the reason in buffer.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int KspRtReconstructionAvailable(StringBuilder buffer, int size);

        // How many frames Ray Reconstruction has reconstructed since the proxy started.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong KspRtReconstructedFrames();

        // The proxy's size of ReconstructPacket, checked against this side's before the first.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint KspRtReconstructPacketSize();

        // The proxy's render event callback, which every event id here goes through (shared with frame generation).
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr KspFgGetRenderEventFunc();

        // Whether Ready probed the proxy yet, why raytracing cannot run if the probe found it so (null if not), and
        // the render event callback it found.
        private static bool probed;
        private static string problem;
        private static IntPtr renderEvent;

        // Whether the proxy offers raytracing here, and why not. Probed once for the
        // layout; the service's own readiness is asked each time, as the device comes
        // later than the process.
        internal static bool Ready(out string why)
        {
            if (!probed)
            {
                probed = true;
                try
                {
                    uint mesh, material, instance;
                    uint frame = KspRtStructSizes(out mesh, out material, out instance);
                    if (frame != Marshal.SizeOf(typeof(FramePacket)) || mesh != Marshal.SizeOf(typeof(MeshDesc))
                        || material != Marshal.SizeOf(typeof(MaterialDesc))
                        || instance != Marshal.SizeOf(typeof(Instance)))
                        problem = "the proxy's raytracing structures differ from ReDefinition's: dxgi.dll and "
                                  + "ReDefinition.dll are from different versions";
                    else
                        renderEvent = KspFgGetRenderEventFunc();
                }
                catch (Exception e)
                {
                    problem = e is DllNotFoundException || e is EntryPointNotFoundException
                        ? "the proxy (dxgi.dll next to the game) is not there or has no raytracing"
                        : e.GetType().Name + ": " + e.Message;
                }
            }
            if (problem != null)
            {
                why = problem;
                return false;
            }
            var text = new StringBuilder(256);
            int state = KspRtAvailable(text, text.Capacity);
            why = text.ToString();
            return state == 1;
        }

        // The proxy's render event callback for IssuePluginEventAndData, zero until Ready found matching layouts.
        internal static IntPtr RenderEvent
        {
            get { return renderEvent; }
        }

    }
}
