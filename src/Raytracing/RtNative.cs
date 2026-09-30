using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ReDefinition.Raytracing
{
    // The proxy's raytracing service (src/DxgiProxy/Raytracing.h), as the managed
    // side reaches it: exports called from the main thread, and packets for the
    // render events 30 (a frame) and 31 (DLSS Ray Reconstruction).
    //
    // Free of KSP's types, like everything in this folder but the sources that
    // collect KSP's scene: the raytracing test player compiles it on its own.
    internal static class RtNative
    {
        private const string Proxy = "dxgi.dll";

        internal const int FrameEvent = 30;
        internal const int ReconstructEvent = 31;

        internal const uint MeshMagic = 0x4B53524Du;        // 'KSRM'
        internal const uint MaterialMagic = 0x4B535241u;    // 'KSRA'
        internal const uint FrameMagic = 0x4B535246u;       // 'KSRF'
        internal const uint ReconstructMagic = 0x4B535252u; // 'KSRR'

        internal const uint FlagSameFrame = 1u << 0;
        internal const uint FlagReset = 1u << 1;
        internal const uint FlagDenoise = 1u << 2;
        internal const uint FlagMotionVectors = 1u << 3;

        internal const uint SignalSun = 1u << 0;
        internal const uint SignalOcclusion = 1u << 1;
        internal const uint SignalDiffuse = 1u << 2;
        internal const uint SignalSpecular = 1u << 3;

        internal const int InputDepth = 0;
        internal const int InputGBuffer0 = 1;
        internal const int InputGBuffer1 = 2;
        internal const int InputGBuffer2 = 3;
        internal const int InputMotion = 4;
        internal const int InputEnvironment = 5;

        internal const int OutputSun = 0;
        internal const int OutputDiffuse = 1;
        internal const int OutputSpecular = 2;
        internal const int OutputOcclusion = 3;

        internal const int MaterialTextureSize = 64;
        internal const int MaxMaterialTextures = 4096;

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

        [StructLayout(LayoutKind.Sequential)]
        internal struct FramePacket
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
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public IntPtr[] Inputs;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public IntPtr[] Outputs;
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
        internal static extern int KspRtSetInstances(Instance[] instances, int count);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int KspRtStatus(StringBuilder buffer, int size);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void KspRtPassTimings([Out] float[] timings, int count);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int KspRtReconstructionAvailable(StringBuilder buffer, int size);

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

        internal static string Status(out int generation)
        {
            var text = new StringBuilder(512);
            generation = KspRtStatus(text, text.Capacity);
            return text.ToString();
        }
    }
}
