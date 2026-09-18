using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Bridges
{
    internal enum UpscalerBackend
    {
        Fsr3,
        Dlss,
        Amd,
    }

    internal static class UpscalerBackends
    {
        internal static string Name(UpscalerBackend backend)
        {
            switch (backend)
            {
                case UpscalerBackend.Dlss: return "DLSS";
                case UpscalerBackend.Amd: return "AMD FSR (DLL)";
                default: return "FSR 3";
            }
        }

        // Why a technique cannot run in this installation, or null. FSR 3 runs
        // wherever the mod does.
        internal static string WhyNotOffered(UpscalerBackend backend)
        {
            switch (backend)
            {
                case UpscalerBackend.Dlss: return DlssBridge.WhyNotOffered();
                case UpscalerBackend.Amd: return AmdUpscalerBridge.WhyNotOffered();
                default: return null;
            }
        }

        // The next technique in order that can run here.
        internal static UpscalerBackend Next(UpscalerBackend backend)
        {
            int count = Enum.GetValues(typeof(UpscalerBackend)).Length;
            UpscalerBackend next = backend;
            do next = (UpscalerBackend)(((int)next + 1) % count);
            while (next != UpscalerBackend.Fsr3 && WhyNotOffered(next) != null);
            return next;
        }

        // The proxy's side of a technique that runs there; null for FSR 3.
        internal static NativeUpscalerLink Link(UpscalerBackend backend)
        {
            switch (backend)
            {
                case UpscalerBackend.Dlss: return DlssBridge.Link;
                case UpscalerBackend.Amd: return AmdUpscalerBridge.Link;
                default: return null;
            }
        }
    }

    // What DLSS (DlssBridge) and AMD's upscaler (AmdUpscalerBridge) share in reaching
    // the dxgi.dll proxy: whether it has their exports in this build's form, a ring of
    // packet slots the render thread reads, the render events that carry a frame and
    // the release, and the status.
    //
    // The packets travel through the rig's dispatch buffer, executed from
    // OnRenderImage -- where a render event sees the frame's drawing (measured in a
    // Unity 2019.4 player with KSP's native graphics jobs).
    internal sealed class NativeUpscalerLink
    {
        // The flags both packets carry, as Dlss.h and AmdUpscaler.h read them.
        internal const uint FlagHdr = 1u << 0;
        internal const uint FlagDepthInverted = 1u << 1;
        internal const uint FlagAutoExposure = 1u << 2;
        internal const uint FlagReset = 1u << 3;

        private const string Proxy = "dxgi.dll";

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr KspFgGetRenderEventFunc();

        private readonly string name;
        private readonly Type packetType;
        private readonly Func<uint> nativePacketSize;
        private readonly Func<StringBuilder, int, int> nativeStatus;
        private readonly int packetEvent;
        private readonly int releaseEvent;
        private readonly Action probe;
        private readonly StringBuilder statusBuffer = new StringBuilder(512);

        private bool probed;
        private string problem;
        private IntPtr renderEvent = IntPtr.Zero;
        private PacketRing ring;

        // The packet's size, once Ready.
        internal int PacketSize
        {
            get { return ring != null ? ring.PacketSize : 0; }
        }

        // probe: calls every further export the technique needs, so a proxy
        // without one is found here, with the reason, rather than on first use.
        internal NativeUpscalerLink(string name, Type packetType, Func<uint> nativePacketSize,
                                    Func<StringBuilder, int, int> nativeStatus, int packetEvent, int releaseEvent,
                                    Action probe)
        {
            this.name = name;
            this.packetType = packetType;
            this.nativePacketSize = nativePacketSize;
            this.nativeStatus = nativeStatus;
            this.packetEvent = packetEvent;
            this.releaseEvent = releaseEvent;
            this.probe = probe;
        }


        // Asked once. Until the proxy has answered in this build's form nothing is
        // sent, whatever went wrong on the way.
        internal bool Ready
        {
            get
            {
                if (probed) return problem == null;
                probed = true;
                problem = name + ": the dxgi.dll proxy was not asked";

                uint nativeSize;
                try
                {
                    nativeSize = nativePacketSize();
                    renderEvent = KspFgGetRenderEventFunc();
                    if (probe != null) probe();
                    nativeStatus(null, 0);
                }
                // Without the proxy the name resolves to the system's dxgi.dll,
                // which the game has loaded: its exports are missing, not the file.
                catch (DllNotFoundException)
                {
                    problem = NotInstalled();
                    return false;
                }
                catch (EntryPointNotFoundException)
                {
                    problem = NotInstalled();
                    return false;
                }
                catch (Exception e)
                {
                    problem = name + ": the dxgi.dll proxy could not be asked (" + e.GetType().Name + ").";
                    return false;
                }

                int packetSize = Marshal.SizeOf(packetType);
                if (nativeSize != (uint)packetSize || renderEvent == IntPtr.Zero)
                {
                    problem = "The dxgi.dll proxy and the mod disagree on the " + name + " packet (" + nativeSize
                              + " against " + packetSize + " bytes) -- install both from the same release.";
                    return false;
                }

                ring = new PacketRing(packetSize);
                problem = null;
                return true;
            }
        }

        private string NotInstalled()
        {
            return name + " needs ReDefinition's dxgi.dll proxy next to KSP_x64.exe. The dxgi.dll the game loaded has no "
                   + name + ": the proxy is not installed, or it is older than the mod.";
        }

        internal string Problem
        {
            get { return Ready ? null : problem; }
        }

        // This frame's packet, its size and magic already set, into the buffer.
        internal bool Submit<T>(CommandBuffer buffer, ref T packet) where T : struct
        {
            return Issue(buffer, packetEvent, ref packet);
        }

        // A packet into a slot of the ring and its event into the buffer.
        internal bool Issue<T>(CommandBuffer buffer, int eventId, ref T packet) where T : struct
        {
            return buffer != null && Ready && ring.Issue(buffer, renderEvent, eventId, ref packet);
        }

        // Outside a frame's buffer, in the order of the frame: a query before a rig.
        internal bool Execute<T>(int eventId, ref T packet) where T : struct
        {
            if (!Ready) return false;
            CommandBuffer buffer = new CommandBuffer { name = "ReDefinition." + name + " event " + eventId };
            bool issued = Issue(buffer, eventId, ref packet);
            if (issued) Graphics.ExecuteCommandBuffer(buffer);
            buffer.Release();
            return issued;
        }

        // The rig is gone: the proxy lets go, in the order of the frame.
        internal void Release()
        {
            if (!Ready) return;
            CommandBuffer buffer = new CommandBuffer { name = "ReDefinition." + name + " release" };
            buffer.IssuePluginEventAndData(renderEvent, releaseEvent, IntPtr.Zero);
            Graphics.ExecuteCommandBuffer(buffer);
            buffer.Release();
        }

        // 1 while the proxy upscales frames, -1 once something stopped it, -2 when
        // that stands until the GPU, driver or DLL changes, 0 before its first
        // frame. A frame or two behind with multithreaded rendering.
        internal int State()
        {
            if (!Ready) return -1;
            try
            {
                return nativeStatus(null, 0);
            }
            catch (Exception)
            {
                return -1;
            }
        }

        internal string Describe()
        {
            if (!Ready) return problem;
            statusBuffer.Length = 0;
            try
            {
                nativeStatus(statusBuffer, statusBuffer.Capacity);
            }
            catch (Exception e)
            {
                return name + ": the proxy's status could not be read (" + e.GetType().Name + ")";
            }
            return statusBuffer.ToString();
        }
    }
}
