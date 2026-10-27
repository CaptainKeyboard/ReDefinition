using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine.Rendering;

namespace ReDefinition.Proxy
{
    // The managed half of AMD's upscaler; the native half is AmdUpscaler.h in the
    // dxgi.dll proxy, which runs AMD's DLL on its D3D12 device and copies the result
    // back into Unity's texture (NativeUpscalerLink).
    //
    // The DLL chooses the upscaler for the GPU: FSR 4 where the DLL
    // and the GPU have it, otherwise the FSR 3.1 the DLL carries. The proxy's status
    // names it. amd_fidelityfx_upscaler_dx12.dll is the player's.
    internal static class AmdUpscalerBridge
    {
        // The proxy's file name, as the DllImports below resolve it in this process.
        private const string Proxy = ProxyDll.Name;

        // Field order and types as AmdUpscalerPacket in AmdUpscaler.h.
        [StructLayout(LayoutKind.Sequential)]
        internal struct AmdPacket : IRingPacket<AmdPacket>
        {
            // The packet's size and PacketMagic, set by Submit.
            public uint Size;
            public uint Magic;
            // Unity's native textures: the frame at render size, what the upscaler writes at output size, depth
            // and motion vectors.
            public IntPtr Colour;
            public IntPtr Output;
            public IntPtr Depth;
            public IntPtr MotionVectors;
            // The render and the output size in pixels.
            public uint RenderWidth;
            public uint RenderHeight;
            public uint OutputWidth;
            public uint OutputHeight;
            // NativeUpscalerLink's flags: HDR, inverted depth, auto exposure, reset.
            public uint Flags;
            // The frame's subpixel jitter, and what turns the motion vectors into pixels.
            public float JitterX;
            public float JitterY;
            public float MotionVectorScaleX;
            public float MotionVectorScaleY;
            // RCAS after the upscaler, 0 to 1; 0 switches it off.
            public float Sharpness;
            // The time since the last frame in milliseconds, the camera's clip planes and vertical field of view.
            public float FrameTimeDeltaMs;
            public float CameraNear;
            public float CameraFar;
            public float VerticalFovRadians;
            // The optional reactive and transparency-and-composition masks at render size; zero for none.
            public IntPtr Reactive;
            public IntPtr TransparencyAndComposition;

            // Into the ring's boxed copy, so a frame's packet is marshalled without a new box.
            void IRingPacket<AmdPacket>.CopyFrom(ref AmdPacket packet)
            {
                this = packet;
            }
        }

        // The proxy's magic for this packet, checked on every one.
        private const uint PacketMagic = 0x4B535041;   // 'KSPA'

        // The native packet's size in bytes, checked against this one's.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint KspAmdUpscalerPacketSize();

        // What the native side sees, as text: 1 running, -1 stopped, -2 stopped for a reason that stands, 0 not
        // yet.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspAmdUpscalerStatus(StringBuilder buffer, int size);

        // 1 while the proxy presents through Direct3D 12 and AMD's upscaler has its devices.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspAmdUpscalerReady();

        // Which FSR runs, as text: 1 FSR 4, 2 the DLL's FSR 3 on a GPU it offers no FSR 4, 3 an earlier DLL's FSR,
        // 0 before a context.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspAmdUpscalerProvider(StringBuilder buffer, int size);

        // The text KspAmdUpscalerProvider answers into, reused.
        private static readonly StringBuilder providerBuffer = new StringBuilder(64);
        // The name as last made, made anew only when the proxy's answer changes: the
        // row asks every frame.
        private static int shownKind;
        private static string shownSource;
        private static string shownName;
        // A proxy older than the export says nothing, once found out.
        private static bool providerMissing;

        // Which FSR the DLL runs, for the settings row: "FSR 4.1.1", or "FSR 3.1.5
        // fallback" where the DLL is FSR 4's but offers this GPU only the FSR 3 it
        // carries (AmdUpscaler::Provider). Null before it made a context, or without
        // the proxy's export.
        internal static string RunningName()
        {
            if (!link.Ready || providerMissing) return null;
            int kind;
            try
            {
                providerBuffer.Length = 0;
                kind = KspAmdUpscalerProvider(providerBuffer, providerBuffer.Capacity);
            }
            catch (Exception)
            {
                providerMissing = true;
                return null;
            }
            if (kind == 0 || providerBuffer.Length == 0) return null;
            if (NativeText.Refresh(providerBuffer, ref shownSource) || kind != shownKind)
            {
                shownKind = kind;
                shownName = kind == 2 ? shownSource + " fallback" : shownSource;
            }
            return shownName;
        }

        // The link to the proxy's AMD upscaler: render events 4 (a frame) and 5 (the rig gone), probed through
        // KspAmdUpscalerReady.
        private static readonly NativeUpscalerLink link = new NativeUpscalerLink("AMD's upscaler", typeof(AmdPacket),
            () => KspAmdUpscalerPacketSize(), (buffer, size) => KspAmdUpscalerStatus(buffer, size), ProxyEvents.AmdFrame, ProxyEvents.AmdRelease,
            () => KspAmdUpscalerReady());

        // The link, for the rig's state and teardown.
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

        // Why AMD's upscaler is not offered, for the settings row's tooltip; null where it is.
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

        // A frame for AMD's upscaler, recorded into the upscaler's buffer with its size and magic set; false where
        // the link is not ready or the ring refused it.
        internal static bool Submit(CommandBuffer buffer, ref AmdPacket packet)
        {
            if (!link.Ready) return false;
            packet.Size = (uint)link.PacketSize;
            packet.Magic = PacketMagic;
            return link.Submit(buffer, ref packet);
        }
    }
}
