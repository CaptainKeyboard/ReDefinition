using System;
using System.Runtime.InteropServices;
using System.Text;
using FidelityFX.FSR3;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Bridges
{
    // NGX's render preset hints as the proxy passes them on (Dlss.h): Default lets
    // the DLSS library choose for each mode. The numbers are NGX's; NGX's own log
    // shows K = 11 ("Using App hint Preset K").
    internal enum DlssPreset : uint
    {
        Default = 0,
        J = 10,
        K = 11,
        L = 12,
        M = 13,
    }

    // The DLSS model the player chooses, in every mode, or DLSS's own choice for each mode.
    // NVIDIA's DLSS Programming Guide (31 March 2026, 3.12): Preset K is the transformer
    // model before L and M; Preset L "Delivers a sharper, more stable image with less
    // ghosting than Preset J, K but are more expensive performance wise"; Preset M
    // "Delivers similar image quality improvements as Preset L but closer in speed to
    // Presets J, K". With Default, nvngx_dlss.dll 310.9.1 takes K for DLAA, quality and
    // balanced, M for performance and L for ultra performance (NGX's log).
    internal enum DlssModel
    {
        Default,
        K,
        M,
        L,
    }

    internal static class DlssModels
    {
        internal static DlssPreset Preset(DlssModel model)
        {
            if (probePreset.HasValue) return probePreset.Value;
            switch (model)
            {
                case DlssModel.K: return DlssPreset.K;
                case DlssModel.M: return DlssPreset.M;
                case DlssModel.L: return DlssPreset.L;
                default: return DlssPreset.Default;
            }
        }

        // -redefinitionProbeDlssPreset J|K|L|M: that preset in every mode, for a probe's run.
        private static readonly DlssPreset? probePreset = ProbePreset();

        private static DlssPreset? ProbePreset()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            int at = System.Array.IndexOf(args, "-redefinitionProbeDlssPreset");
            DlssPreset preset;
            return at >= 0 && at + 1 < args.Length && System.Enum.TryParse(args[at + 1], out preset) ? preset : (DlssPreset?)null;
        }

        internal static string Name(DlssModel model)
        {
            return model == DlssModel.Default ? "Default" : "Preset " + model;
        }
    }

    // The managed half of DLSS; the native half is Dlss.h in the dxgi.dll proxy, which
    // evaluates DLSS on Unity's own D3D11 device (NativeUpscalerLink).
    //
    // nvngx_dlss.dll comes from NVIDIA's release (NvidiaDownloader).
    internal static class DlssBridge
    {
        private const string Proxy = "dxgi.dll";

        // Field order and types as DlssPacket in Dlss.h; size and magic are checked
        // on both sides.
        [StructLayout(LayoutKind.Sequential)]
        internal struct DlssPacket : IRingPacket<DlssPacket>
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
            public int Quality;
            public uint Preset;
            public uint Flags;
            public float JitterX;
            public float JitterY;
            public float MotionVectorScaleX;
            public float MotionVectorScaleY;

            void IRingPacket<DlssPacket>.CopyFrom(ref DlssPacket packet)
            {
                this = packet;
            }
        }

        private const uint PacketMagic = 0x4B535044;   // 'KSPD'

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint KspDlssPacketSize();

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspDlssStatus(StringBuilder buffer, int size);

        [DllImport(Proxy, EntryPoint = "KspDlssRenderSizes2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspDlssRenderSizes(uint outputWidth, uint outputHeight, int quality,
                                                     [Out] uint[] optimal, [Out] uint[] minimum, [Out] uint[] maximum);

        private static readonly NativeUpscalerLink link = new NativeUpscalerLink("DLSS", typeof(DlssPacket),
            () => KspDlssPacketSize(), (buffer, size) => KspDlssStatus(buffer, size), 2, 3,
            () => KspDlssRenderSizes(0, 0, 0, null, null, null));

        internal static NativeUpscalerLink Link
        {
            get { return link; }
        }

        // Field order and types as DlssSizeQuery in Dlss.h.
        [StructLayout(LayoutKind.Sequential)]
        private struct SizeQuery : IRingPacket<SizeQuery>
        {
            public uint Size;
            public uint Magic;
            public IntPtr Texture;
            public uint OutputWidth;
            public uint OutputHeight;

            void IRingPacket<SizeQuery>.CopyFrom(ref SizeQuery packet)
            {
                this = packet;
            }
        }

        private const uint SizeQueryMagic = 0x4B535051;   // 'KSPQ'
        private const int SizeQueryEvent = 6;

        // Whether to offer DLSS: the proxy has it, and KSP renders on an NVIDIA GPU.
        // Whether that GPU is an RTX one, and whether a player's nvngx_dlss.dll is
        // there, only NGX can say (State).
        internal static bool Offered
        {
            get { return WhyNotOffered() == null; }
        }

        internal static string WhyNotOffered()
        {
            if (!link.Ready) return link.Problem;
            if (SystemInfo.graphicsDeviceVendorID != 0x10DE)
                return "DLSS needs an NVIDIA RTX GPU; KSP renders on " + SystemInfo.graphicsDeviceName + ".";
            return null;
        }

        internal static bool Submit(CommandBuffer buffer, ref DlssPacket packet)
        {
            if (!link.Ready) return false;
            packet.Size = (uint)link.PacketSize;
            packet.Magic = PacketMagic;
            return link.Submit(buffer, ref packet);
        }

        // Asks the proxy for every mode's render size at this output size, before a
        // rig is built at one of them: answered on the render thread and readable
        // through RenderSize a frame or two later. The texture is any of Unity's,
        // for its device.
        internal static bool RequestRenderSizes(IntPtr texture, Vector2Int output)
        {
            if (!link.Ready || texture == IntPtr.Zero) return false;
            SizeQuery query = new SizeQuery
            {
                Size = (uint)Marshal.SizeOf(typeof(SizeQuery)),
                Magic = SizeQueryMagic,
                Texture = texture,
                OutputWidth = (uint)output.x,
                OutputHeight = (uint)output.y,
            };
            return link.Execute(SizeQueryEvent, ref query);
        }

        // The render size DLSS asked for at this output size and quality value
        // (guide 5.2.8), once the proxy has made a feature or answered a query for
        // them; false before.
        internal static bool RenderSize(Vector2Int output, int quality, out Vector2Int optimal)
        {
            optimal = Vector2Int.zero;
            if (!link.Ready) return false;
            uint[] size = new uint[2];
            try
            {
                if (KspDlssRenderSizes((uint)output.x, (uint)output.y, quality, size, null, null) == 0) return false;
            }
            catch (Exception)
            {
                return false;
            }
            optimal = new Vector2Int((int)size[0], (int)size[1]);
            return optimal.x > 0 && optimal.y > 0;
        }

        // ReDefinition's modes as NGX's quality values: performance 0, balanced 1,
        // quality 2, ultra performance 3, DLAA 5. The guide's modes (3.2.1) have no
        // ultra quality; that mode runs as quality.
        internal static int Quality(Fsr3Upscaler.QualityMode mode)
        {
            switch (mode)
            {
                case Fsr3Upscaler.QualityMode.NativeAA: return 5;
                case Fsr3Upscaler.QualityMode.UltraQuality: return 2;
                case Fsr3Upscaler.QualityMode.Quality: return 2;
                case Fsr3Upscaler.QualityMode.Balanced: return 1;
                case Fsr3Upscaler.QualityMode.Performance: return 0;
                default: return 3;
            }
        }
    }
}
