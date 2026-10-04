using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ReDefinition.Raytracing
{
    // The proxy's raytracing service (src/DxgiProxy/Raytracing.h), as the managed
    // side reaches it: exports called from the main thread, and packets for the
    // render events 30 (a frame) and 31 (DLSS Ray Reconstruction); event 32 frees
    // Ray Reconstruction's resources once it is no longer used.
    //
    // Free of KSP's types, like everything in this folder but the sources that
    // collect KSP's scene: the raytracing test player compiles it on its own.
    internal static class RtNative
    {
        private const string Proxy = "dxgi.dll";

        internal const int FrameEvent = 30;
        internal const int ReconstructEvent = 31;
        internal const int ReconstructReleaseEvent = 32;

        internal const uint MeshMagic = 0x4B53524Du;        // 'KSRM'
        internal const uint MaterialMagic = 0x4B535241u;    // 'KSRA'
        internal const uint FrameMagic = 0x4B535246u;       // 'KSRF'
        internal const uint ReconstructMagic = 0x4B535252u; // 'KSRR'

        internal const uint FlagSameFrame = 1u << 0;
        internal const uint FlagReset = 1u << 1;
        internal const uint FlagDenoise = 1u << 2;
        internal const uint FlagMotionVectors = 1u << 3;
        internal const uint FlagGuides = 1u << 4;
        internal const uint FlagExtra = 1u << 5;
        internal const uint FlagWater = 1u << 6;
        internal const uint FlagWaterNormals = 1u << 7;
        internal const uint FlagLegacySmoothness = 1u << 8;

        internal const uint SignalSun = 1u << 0;
        internal const uint SignalOcclusion = 1u << 1;
        internal const uint SignalDiffuse = 1u << 2;
        internal const uint SignalSpecular = 1u << 3;
        internal const uint SignalForwardLight = 1u << 4;

        internal const int InputDepth = 0;
        internal const int InputGBuffer0 = 1;
        internal const int InputGBuffer1 = 2;
        internal const int InputGBuffer2 = 3;
        internal const int InputMotion = 4;
        internal const int InputEnvironment = 5;
        // Instances the GPU made, three texels each, InstancesPerRow to a row (Raytracing.h).
        internal const int InputInstances = 6;
        // Instance flags (Raytracing.h, kRtInstance*).
        internal const uint InstanceTerrain = 1u << 0;
        internal const int InstancesPerRow = 256;
        // Instances and groups for every frame without its own.
        internal const uint AnyFrame = 0xFFFFFFFFu;

        internal const int OutputSun = 0;
        internal const int OutputDiffuse = 1;
        internal const int OutputSpecular = 2;
        internal const int OutputOcclusion = 3;
        internal const int OutputForwardLight = 4;

        internal const int MaterialTextureSize = 64;
        // As Raytracing.h: a Direct3D 12 texture array's most layers.
        internal const int MaxMaterialTextures = 2048;

        [StructLayout(LayoutKind.Sequential)]
        internal struct MeshDesc
        {
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

        [StructLayout(LayoutKind.Sequential)]
        internal struct MaterialDesc
        {
            public uint Size;
            public uint Magic;
            public int Material;
            public uint Flags;
            public float AlbedoR, AlbedoG, AlbedoB, AlbedoA;
            public float SpecularR, SpecularG, SpecularB;
            public float Smoothness;
            public float EmissionR, EmissionG, EmissionB;
            public int Texture;
            // Above 0: the albedo is in Unity's colour space and the vertex colour times
            // this is added to it (RtTerrainColour); the texture is not used.
            public float VertexColour;
            public float Reserved0, Reserved1, Reserved2;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Instance
        {
            public float M00, M01, M02, M03;
            public float M10, M11, M12, M13;
            public float M20, M21, M22, M23;
            public int Mesh;
            public int Material;
            public uint Mask;
            public uint Flags;
        }

        // A run of the GPU's instances, as RtGpuInstanceGroup in Raytracing.h.
        [StructLayout(LayoutKind.Sequential)]
        internal struct GpuGroup
        {
            public int Mesh;
            public int Material;
            public uint First;
            public uint Count;
            public uint Mask;
            public uint Flags;
            public uint Reserved0;
            public uint Reserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FramePacket : Bridges.IRingPacket<FramePacket>
        {
            public uint Size;
            public uint Magic;
            public uint FrameIndex;
            public uint Flags;
            public uint Width;
            public uint Height;
            public uint Signals;
            public uint Reserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] ClipToWorld;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] WorldToClip;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] PrevWorldToClip;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] SunDirection;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] SunColor;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] SkyColor;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public float[] Params;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] BodySphere;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)] public float[] Ambient;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] WorldToView;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] ViewToClip;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] CameraDelta;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public float[] Jitter;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public float[] DepthRange;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] Lights;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] LightParams;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] NightLight;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] Planet;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public float[] LightSurface;
            // The sea's frame, fixed to the main body (RtCamera.Sea): a point on the sea
            // relative to the camera, its east and north, each with a w; all 0 for none.
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)] public float[] Sea;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public IntPtr[] Inputs;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public IntPtr[] Outputs;

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
                Array.Copy(Inputs, target.Inputs, Inputs.Length);
                Array.Copy(Outputs, target.Outputs, Outputs.Length);
            }
        }

        // Event 31's packet, as RtReconstructPacket in Raytracing.h: DLSS Ray
        // Reconstruction for the frame whose raytracing made its guides.
        [StructLayout(LayoutKind.Sequential)]
        internal struct ReconstructPacket : Bridges.IRingPacket<ReconstructPacket>
        {
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
            public IntPtr Colour;
            public IntPtr Depth;
            public IntPtr Motion;
            public IntPtr GBuffer0;
            public IntPtr GBuffer1;
            public IntPtr GBuffer2;
            public IntPtr Specular;
            public IntPtr Output;

            void Bridges.IRingPacket<ReconstructPacket>.CopyFrom(ref ReconstructPacket packet)
            {
                this = packet;
            }
        }

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
                Inputs = new IntPtr[8],
                Outputs = new IntPtr[8],
            };
        }

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int KspRtAvailable(StringBuilder buffer, int size);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint KspRtStructSizes(out uint mesh, out uint material, out uint instance);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int KspRtSetMesh(ref MeshDesc desc);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void KspRtReleaseMesh(int mesh);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int KspRtSetMaterial(ref MaterialDesc desc);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void KspRtReleaseMaterial(int material);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int KspRtSetMaterialTexture(int layer, byte[] pixels, int size);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int KspRtSetInstances(Instance[] instances, int count, uint frameIndex);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int KspRtSetGpuGroups(GpuGroup[] groups, int count, uint frameIndex);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int KspRtStatus(StringBuilder buffer, int size);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void KspRtPassTimings([Out] float[] timings, int count);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int KspRtReconstructionAvailable(StringBuilder buffer, int size);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong KspRtReconstructedFrames();

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint KspRtReconstructPacketSize();

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr KspFgGetRenderEventFunc();

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

        internal static IntPtr RenderEvent
        {
            get { return renderEvent; }
        }

    }
}
