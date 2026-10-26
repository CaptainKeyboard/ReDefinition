using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine.Rendering;

namespace ReDefinition.Proxy
{
    // The managed half of XeSS; the native half is XessUpscaler.h in the dxgi.dll
    // proxy, which runs Intel's libxess.dll on its D3D12 device and copies the result
    // back into Unity's texture (NativeUpscalerLink).
    //
    // XeSS on Direct3D 12 runs on "Other vendor GPU supporting SM 6.4 and with hardware
    // acceleration for DP4a", XMX on Intel Arc (xess_sr_developer_guide_english.md,
    // Requirements). libxess.dll is the player's. The packet is AMD's
    // (AmdUpscalerBridge.AmdPacket) with a magic of its own.
    internal static class XessUpscalerBridge
    {
        // The proxy's file name, as the DllImports below resolve it in this process.
        private const string Proxy = ProxyDll.Name;

        // The proxy's magic for a XeSS packet, which tells it from one for AMD's upscaler.
        private const uint PacketMagic = 0x4B535058;   // 'KSPX'

        // The native packet's size in bytes, checked against AmdPacket's.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint KspXessUpscalerPacketSize();

        // What the native side sees, as text, with the state AMD's upscaler reports it in.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspXessUpscalerStatus(StringBuilder buffer, int size);

        // 1 while the proxy presents through Direct3D 12 and XeSS has its devices.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspXessUpscalerReady();

        // The version libxess.dll reports, as text; 0 before the DLL is loaded.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspXessUpscalerVersion(StringBuilder buffer, int size);

        // The text KspXessUpscalerVersion answers into, reused.
        private static readonly StringBuilder versionBuffer = new StringBuilder(64);
        // The name as last made, made anew only when the proxy's answer changes: the
        // row asks every frame.
        private static string shownName;

        // Which XeSS libxess.dll reports, for the settings row: "XeSS 2.0.2"; null
        // before the DLL is loaded.
        internal static string RunningName()
        {
            if (!link.Ready) return null;
            int known;
            try
            {
                versionBuffer.Length = 0;
                known = KspXessUpscalerVersion(versionBuffer, versionBuffer.Capacity);
            }
            catch (Exception)
            {
                return null;
            }
            if (known == 0 || versionBuffer.Length == 0) return null;
            if (shownName == null || !Same(versionBuffer, shownName)) shownName = versionBuffer.ToString();
            return shownName;
        }

        // Whether the builder holds the same text, compared without making a string of it.
        private static bool Same(StringBuilder text, string other)
        {
            if (text.Length != other.Length) return false;
            for (int i = 0; i < other.Length; i++)
            {
                if (text[i] != other[i]) return false;
            }
            return true;
        }

        // The link to the proxy's XeSS: render events 7 (a frame) and 8 (the rig gone), probed through
        // KspXessUpscalerReady and KspXessUpscalerVersion.
        private static readonly NativeUpscalerLink link = new NativeUpscalerLink("XeSS",
            typeof(AmdUpscalerBridge.AmdPacket), () => KspXessUpscalerPacketSize(),
            (buffer, size) => KspXessUpscalerStatus(buffer, size), 7, 8,
            () =>
            {
                KspXessUpscalerReady();
                KspXessUpscalerVersion(null, 0);
            });

        // The link, for the rig's state and teardown.
        internal static NativeUpscalerLink Link
        {
            get { return link; }
        }

        // Offered where the proxy has it and presents through Direct3D 12, which
        // XeSS needs on a GPU that is not Intel's. Whether the DLL is there and the
        // GPU runs it, the proxy says once frames arrive (State).
        internal static bool Offered
        {
            get { return WhyNotOffered() == null; }
        }

        // Why XeSS is not offered, for the settings row's tooltip; null where it is.
        internal static string WhyNotOffered()
        {
            if (!link.Ready) return link.Problem;
            int ready;
            try
            {
                ready = KspXessUpscalerReady();
            }
            catch (Exception)
            {
                return "The dxgi.dll next to KSP_x64.exe has no XeSS -- it is older than the mod.";
            }
            return ready != 0
                ? null
                : "XeSS needs ReDefinition's proxy to present through Direct3D 12, and it does not"
                  + " (enabled=0 or measureOnly=1 in ReDefinitionProxy.ini, or its setup failed: ReDefinitionProxy.log says which).";
        }

        // A frame for XeSS, recorded into the upscaler's buffer with its size and magic set; false where the link
        // is not ready or the ring refused it.
        internal static bool Submit(CommandBuffer buffer, ref AmdUpscalerBridge.AmdPacket packet)
        {
            if (!link.Ready) return false;
            packet.Size = (uint)link.PacketSize;
            packet.Magic = PacketMagic;
            return link.Submit(buffer, ref packet);
        }
    }
}
