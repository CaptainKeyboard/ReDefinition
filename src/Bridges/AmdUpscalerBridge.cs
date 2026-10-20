using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine.Rendering;

namespace ReDefinition.Bridges
{
    // The managed half of AMD's upscaler; the native half is AmdUpscaler.h in the
    // dxgi.dll proxy, which runs AMD's DLL on its D3D12 device and copies the result
    // back into Unity's texture (NativeUpscalerLink).
    //
    // Which upscaler that is, is the DLL's choice for the GPU: FSR 4 where the DLL
    // and the GPU have it, otherwise the FSR 3.1 the DLL carries; the proxy's status
    // names it. amd_fidelityfx_upscaler_dx12.dll is the player's.
    internal static class AmdUpscalerBridge
    {
        private const string Proxy = "dxgi.dll";

        // Field order and types as AmdUpscalerPacket in AmdUpscaler.h.
        [StructLayout(LayoutKind.Sequential)]
        internal struct AmdPacket : IRingPacket<AmdPacket>
        {
            public uint Size;
            public uint Magic;
            public IntPtr Colour;
            public IntPtr Output;
            public IntPtr Depth;
            public IntPtr MotionVectors;
            public uint RenderWidth;
            public uint RenderHeight;
            public uint OutputWidth;
            public uint OutputHeight;
            public uint Flags;
            public float JitterX;
            public float JitterY;
            public float MotionVectorScaleX;
            public float MotionVectorScaleY;
            public float Sharpness;
            public float FrameTimeDeltaMs;
            public float CameraNear;
            public float CameraFar;
            public float VerticalFovRadians;
            public IntPtr Reactive;
            public IntPtr TransparencyAndComposition;

            void IRingPacket<AmdPacket>.CopyFrom(ref AmdPacket packet)
            {
                this = packet;
            }
        }

        private const uint PacketMagic = 0x4B535041;   // 'KSPA'

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint KspAmdUpscalerPacketSize();

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspAmdUpscalerStatus(StringBuilder buffer, int size);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspAmdUpscalerReady();

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspAmdUpscalerProvider(StringBuilder buffer, int size);

        private static readonly StringBuilder providerBuffer = new StringBuilder(64);
        // The name as last made, made anew only when the proxy's answer changes: the
        // row asks every frame.
        private static int shownKind;
        private static string shownSource;
        private static string shownName;

        // Which FSR the DLL runs, for the settings row: "FSR 4.1.1", or "FSR 3.1.5
        // fallback" where the DLL is FSR 4's but offers this GPU only the FSR 3 it
        // carries (AmdUpscaler::Provider); null before it made a context, or without
        // the proxy's export.
        internal static string RunningName()
        {
            if (!link.Ready) return null;
            int kind;
            try
            {
                providerBuffer.Length = 0;
                kind = KspAmdUpscalerProvider(providerBuffer, providerBuffer.Capacity);
            }
            catch (Exception)
            {
                return null;
            }
            if (kind == 0 || providerBuffer.Length == 0) return null;
            if (kind != shownKind || shownSource == null || !Same(providerBuffer, shownSource))
            {
                shownKind = kind;
                shownSource = providerBuffer.ToString();
                shownName = kind == 2 ? shownSource + " fallback" : shownSource;
            }
            return shownName;
        }

        private static bool Same(StringBuilder text, string other)
        {
            if (text.Length != other.Length) return false;
            for (int i = 0; i < other.Length; i++)
            {
                if (text[i] != other[i]) return false;
            }
            return true;
        }

        private static readonly NativeUpscalerLink link = new NativeUpscalerLink("AMD's upscaler", typeof(AmdPacket),
            () => KspAmdUpscalerPacketSize(), (buffer, size) => KspAmdUpscalerStatus(buffer, size), 4, 5,
            () => KspAmdUpscalerReady());

        internal static NativeUpscalerLink Link
        {
            get { return link; }
        }

        // Offered where the proxy has it and presents through Direct3D 12, which
        // AMD's API needs. Whether the DLL is there, and which version it runs, the
        // proxy says once frames arrive (State).
        internal static bool Offered
        {
            get { return WhyNotOffered() == null; }
        }

        internal static string WhyNotOffered()
        {
            if (!link.Ready) return link.Problem;
            int ready;
            try
            {
                ready = KspAmdUpscalerReady();
            }
            catch (Exception)
            {
                return "The dxgi.dll next to KSP_x64.exe has no AMD's upscaler -- it is older than the mod.";
            }
            return ready != 0
                ? null
                : "AMD's upscaler needs ReDefinition's proxy to present through Direct3D 12, and it does not"
                  + " (enabled=0 or measureOnly=1 in ReDefinitionProxy.ini, or its setup failed: ReDefinitionProxy.log says which).";
        }

        internal static bool Submit(CommandBuffer buffer, ref AmdPacket packet)
        {
            if (!link.Ready) return false;
            packet.Size = (uint)link.PacketSize;
            packet.Magic = PacketMagic;
            return link.Submit(buffer, ref packet);
        }
    }
}
