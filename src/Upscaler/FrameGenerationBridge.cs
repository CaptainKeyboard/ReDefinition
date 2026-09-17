using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition
{
    // The managed half of frame generation.
    //
    // The native side lives in the dxgi.dll proxy next to KSP_x64.exe, which is
    // already loaded in this process, so it is reachable through a plain
    // DllImport. Where the proxy is not installed the entry points do not exist,
    // the first call throws, and everything here goes quiet for the rest of the
    // session; the upscaler works without the proxy.
    //
    // Frame generation gets the engine's own per-pixel motion vectors and depth.
    internal static class FrameGenerationBridge
    {
        private const string Proxy = "dxgi.dll";

        // Everything about one frame besides the textures. Field order and
        // types match FramePacket in FrameGeneration.h; size and magic are
        // checked on both sides so a mismatch is a log line, not a silent
        // misread.
        //
        // It is sent through the render thread (IssuePluginEventAndData) and
        // not by a call from the main thread: with multithreaded rendering the
        // main thread runs ahead, and a value set here and read at Present time
        // could belong to the frame after the one being presented.
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct FramePacket
        {
            public uint Size;
            public uint Magic;
            public uint FrameIndex;
            public uint RenderWidth;
            public uint RenderHeight;
            public uint Reset;
            public float JitterX;
            public float JitterY;
            public float MotionVectorScaleX;
            public float MotionVectorScaleY;
            public float NearPlane;
            public float FarPlane;
            public float VerticalFovRadians;
            public float FrameTimeDeltaMs;
            public float PositionX, PositionY, PositionZ;
            public float UpX, UpY, UpZ;
            public float RightX, RightY, RightZ;
            public float ForwardX, ForwardY, ForwardZ;

            // For DLSS frame generation (StreamlineCamera): row-major, without
            // jitter, view space down +z, Direct3D clip space.
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] ViewToClip;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] ClipToView;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] ClipToPrevClip;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] PrevClipToClip;
        }

        private const uint PacketMagic = 0x4B535046;   // 'KSPF'
        private const int PacketEvent = 1;
        private static PacketRing packetRing;

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern void KspFgRegisterInputs3(IntPtr depth, IntPtr motionVectors, IntPtr hudLess);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern void KspFgSetEnabled(int enabled);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint KspFgPacketSize();

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr KspFgGetRenderEventFunc();

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspFgStatus(StringBuilder buffer, int size);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspFgConfigured();

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspFgCanGenerate();

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspFgContextFailing();

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern void KspFgRetryContext();

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern void KspFgRequestCheck();

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspFgCounters(out uint rendered, out uint presented);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspFgTechnique(out int multiplier);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspFgDlssVsync();

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspNvidiaGpu(out uint architecture, out uint implementation);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern void KspNvidiaDirectories(StringBuilder dlss, StringBuilder streamline, int size);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern void KspPerfRegisterMainThread();

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspPerfLoad(out float mainThread, out float renderThread, out float gpu,
                                              out float gpuThisProcess);

        // Set once, on the first failure of any kind, or when the proxy says
        // frame generation is switched off in its ini, so a missing native DLL does
        // not throw every frame.
        private static bool unavailable;
        private static string unavailableReason = "";
        private static IntPtr renderEvent = IntPtr.Zero;
        private static bool renderEventResolved;

        // Asked once, on first use, rather than learnt from the first call that
        // fails: a settings dialog has to know whether to offer the switch
        // before anything has been called. And the proxy is asked whether frame
        // generation is on in its ini -- with frameGeneration=0 it exports
        // everything and generates nothing, and a switch offered then would do
        // nothing.
        private static bool probed;

        public static bool Available
        {
            get
            {
                if (!probed)
                {
                    probed = true;
                    int configured = 0;
                    if (TryCall(() => configured = KspFgConfigured()) && configured == 0)
                    {
                        unavailable = true;
                        unavailableReason = "proxy present, but frameGeneration=0 in ReDefinitionProxy.ini";
                        Debug.Log(UpscalerProbe.Tag + " Frame generation is switched off in the proxy's ini"
                                  + " (frameGeneration=0); upscaling continues unaffected.");
                    }
                }
                return !unavailable;
            }
        }

        // Whether a swapchain proxy presents through a swapchain that generates,
        // Streamline's or FidelityFX's. The ini allowing it (Available) is not
        // enough: without NVIDIA's or AMD's runtime, with measureOnly=1, or after a
        // failed setup nothing generates. A proxy without the export is one older than the
        // mod, and frame generation goes off with that reason.
        public static bool CanGenerate
        {
            get
            {
                int present;
                return Available && TryExport(CanGenerateCall, "KspFgCanGenerate", out present) && present != 0;
            }
        }

        // Which frame generation the proxy's swapchain presents with, for the
        // settings row: DLSS where NVIDIA's Streamline runs, with the frames shown
        // per rendered frame while it generates, FSR 3 otherwise; null while none
        // does. A proxy without the export is older than the mod.
        public static string TechniqueName()
        {
            int technique;
            if (!Available || !TryExport(TechniqueCall, "KspFgTechnique", out technique)) return null;
            if (technique == 1) return "FSR 3";
            if (technique != 2) return null;
            return techniqueMultiplier >= 2 && techniqueMultiplier < DlssNames.Length
                ? DlssNames[techniqueMultiplier]
                : DlssNames[0];
        }

        // The NVIDIA GPU of the adapter KSP renders on (KspNvidiaGpu): NVAPI's
        // architecture and implementation, what NVIDIA's DLLs are offered for
        // download by (NvidiaFiles). False where it is not NVIDIA's, where NVAPI does
        // not answer, or without the proxy -- quietly, whatever the proxy's ini says
        // about frame generation.
        public static bool NvidiaGpu(out uint architecture, out uint implementation)
        {
            architecture = 0;
            implementation = 0;
            int known;
            if (!TryNative(NvidiaGpuCall, out known) || known == 0) return false;
            architecture = gpuArchitecture;
            implementation = gpuImplementation;
            return true;
        }

        private static uint gpuArchitecture;
        private static uint gpuImplementation;
        private static readonly Func<int> NvidiaGpuCall = () => KspNvidiaGpu(out gpuArchitecture, out gpuImplementation);

        // Where the proxy's ini says NVIDIA's DLLs lie, dlssDirectory and
        // streamlineDirectory; empty where unset or without the proxy.
        public static void NvidiaDirectories(out string dlss, out string streamline)
        {
            StringBuilder dlssBuffer = new StringBuilder(260);
            StringBuilder streamlineBuffer = new StringBuilder(260);
            bool read = TryNative(() => KspNvidiaDirectories(dlssBuffer, streamlineBuffer, 260));
            dlss = read ? dlssBuffer.ToString() : "";
            streamline = read ? streamlineBuffer.ToString() : "";
        }

        // Whether the proxy's swapchain presents with DLSS frame generation
        // (KspFgTechnique), for what only it needs.
        public static bool DlssFrameGenerationRuns
        {
            get
            {
                int technique;
                return Available && TryNative(TechniqueCall, out technique) && technique == 2;
            }
        }

        // Whether DLSS frame generation presents with V-Sync where KSP asks for it
        // (KspFgDlssVsync): false only where its build said it does not support it.
        // True without the proxy, or with one older than the mod, which cannot say.
        public static bool DlssFrameGenerationVsync
        {
            get
            {
                int supported;
                return !TryNative(DlssVsyncCall, out supported) || supported != 0;
            }
        }

        private static readonly Func<int> DlssVsyncCall = KspFgDlssVsync;

        private static int techniqueMultiplier;
        private static readonly Func<int> TechniqueCall = () => KspFgTechnique(out techniqueMultiplier);
        private static readonly string[] DlssNames = { "DLSS", "DLSS", "DLSS 2x", "DLSS 3x", "DLSS 4x", "DLSS 5x", "DLSS 6x" };

        private static readonly Func<int> CanGenerateCall = KspFgCanGenerate;
        private static readonly Func<int> ContextFailingCall = KspFgContextFailing;
        private static readonly Action RetryContextCall = KspFgRetryContext;
        private static readonly Action RequestCheckCall = KspFgRequestCheck;

        // An export this build of the mod needs, called. A proxy without it is
        // older than the mod, and frame generation goes off with that reason --
        // once, not with every call.
        private static bool TryExport(Func<int> call, string export, out int result)
        {
            if (TryNative(call, out result)) return true;
            ProxyOlderThanMod(export);
            return false;
        }

        private static bool TryExport(Action call, string export)
        {
            if (TryNative(call)) return true;
            ProxyOlderThanMod(export);
            return false;
        }

        private static void ProxyOlderThanMod(string export)
        {
            // Told first, while the bridge still reaches it: switched off here, the
            // proxy would otherwise go on wanting frames nobody sends.
            TryNative(() => KspFgSetEnabled(0));
            unavailable = true;
            unavailableReason = "the dxgi.dll proxy is older than the mod (it has no " + export + ")";
            Debug.Log(UpscalerProbe.Tag + " Frame generation off: " + unavailableReason + ".");
        }

        // Whether the proxy's last attempt to make frame generation's context
        // failed, and whether for a reason that stands (KspFgContextFailing, see
        // FrameGeneration::kContextStands); ReDefinitionProxy.log says why.
        public static bool ContextFailing
        {
            get { return ContextState() != ContextFine; }
        }

        public static bool ContextFailureStands
        {
            get { return ContextState() == ContextStands; }
        }

        // Whether the rig's inputs are of use to the proxy: it can generate, and
        // no failure to make the context stands. The rig and its capture follow
        // this; the switch follows CanGenerate, so a player can still switch
        // frame generation on to try again.
        public static bool InputsUsable
        {
            get { return CanGenerate && !ContextFailureStands; }
        }

        // FrameGeneration::kContextFine and kContextStands.
        private const int ContextFine = 0;
        private const int ContextStands = 2;

        // The player switched frame generation on, or a new scene began: a
        // context the proxy could not make gets another try (KspFgRetryContext).
        public static void RetryContext()
        {
            if (!Available) return;
            TryExport(RetryContextCall, "KspFgRetryContext");
        }

        // The proxy's HUD-less and motion vector check at once, rather than on its
        // own rhythm (KspFgRequestCheck). A diagnostic: a proxy without the export
        // goes quiet here rather than switching frame generation off.
        public static void RequestCheck()
        {
            if (!Available) return;
            TryNative(RequestCheckCall);
        }

        private static int ContextState()
        {
            int state;
            return Available && TryExport(ContextFailingCall, "KspFgContextFailing", out state) ? state : 0;
        }

        // Depth and motion vectors at render size, and the HUD-less texture:
        // the backbuffer as copied by the rig at the end of the last scene
        // camera. All three are copied into shared textures by the proxy at
        // Present time.
        public static void RegisterInputs(RenderTexture depth, RenderTexture motionVectors, RenderTexture hudLess)
        {
            if (!Available) return;

            IntPtr d = depth != null ? depth.GetNativeTexturePtr() : IntPtr.Zero;
            IntPtr m = motionVectors != null ? motionVectors.GetNativeTexturePtr() : IntPtr.Zero;
            IntPtr h = hudLess != null ? hudLess.GetNativeTexturePtr() : IntPtr.Zero;

            if (TryCall(() => KspFgRegisterInputs3(d, m, h)))
                Debug.Log(UpscalerProbe.Tag + " Frame generation inputs registered.");
        }

        // Recorded into the upscaler's own CommandBuffer each frame, after the
        // dispatch, so the packet reaches the native side on the render thread
        // in the order of the frame it belongs to.
        public static bool SubmitFrame(CommandBuffer buffer, ref FramePacket packet)
        {
            if (!Available || buffer == null) return false;
            if (!EnsureRenderEvent()) return false;

            packet.Size = (uint)packetRing.PacketSize;
            packet.Magic = PacketMagic;
            return packetRing.Issue(buffer, renderEvent, PacketEvent, ref packet);
        }

        private static bool EnsureRenderEvent()
        {
            if (renderEventResolved) return renderEvent != IntPtr.Zero;
            renderEventResolved = true;

            uint nativeSize = 0;
            if (!TryCall(() => { nativeSize = KspFgPacketSize(); renderEvent = KspFgGetRenderEventFunc(); }))
                return false;

            int packetSize = Marshal.SizeOf(typeof(FramePacket));
            if (nativeSize != (uint)packetSize)
            {
                Debug.LogWarning(UpscalerProbe.Tag + " Frame packet layout mismatch: managed " + packetSize
                                 + " bytes, native " + nativeSize + " -- frame generation stays off.");
                renderEvent = IntPtr.Zero;
                unavailable = true;
                unavailableReason = "frame packet layout differs between mod and proxy";
                return false;
            }

            packetRing = new PacketRing(packetSize);
            return renderEvent != IntPtr.Zero;
        }

        // Every frame from the rig: the calls made once.
        public static void SetEnabled(bool enabled)
        {
            if (!Available) return;
            TryCall(enabled ? EnableCall : DisableCall);
        }

        private static readonly Action EnableCall = () => KspFgSetEnabled(1);
        private static readonly Action DisableCall = () => KspFgSetEnabled(0);

        // Set when the proxy lacks KspFgCounters -- a dxgi.dll older than the
        // mod. Only the window's bracket goes without it; frame generation stays
        // on (unlike TryCall).
        private static bool countersMissing;

        // The same for the load exports: without them the load stays unknown.
        private static bool perfMissing;


        // The proxy's running totals: frames the game presented through it, and
        // frames the swapchain presented, generated ones included. Both wrap at
        // 2^32; the caller differences two readings. False without a proxy that
        // has counted a frame.
        public static bool TryGetCounters(out uint rendered, out uint presented)
        {
            rendered = 0;
            presented = 0;
            if (countersMissing || !Available) return false;

            uint r = 0;
            uint p = 0;
            int valid = 0;
            if (TryNative(() => valid = KspFgCounters(out r, out p)))
            {
                rendered = r;
                presented = p;
                return valid != 0;
            }

            countersMissing = true;
            Debug.Log(UpscalerProbe.Tag + " The proxy does not count frames (a dxgi.dll older than the mod);"
                      + " the window shows the rendered frame rate only.");
            return false;
        }

        // The proxy measures where a frame's time goes (LoadMonitor.h) and has
        // to be told which thread is the game's main thread. Called once, from
        // it. Without a proxy, or with one older than the mod, the load simply
        // stays unknown.
        public static void RegisterMainThread()
        {
            if (!TryNative(KspPerfRegisterMainThread)) perfMissing = true;
        }

        public enum LoadState { Missing, SwitchedOff, NotYet, Measured }

        // The last completed second of load, each in percent, -1 where unknown:
        // the main thread's and the render thread's CPU time as a share of one
        // core, the busiest 3D engine of the game's adapter, and the game's
        // share of it.
        public static LoadState ReadLoad(out float mainThread, out float renderThread, out float gpu,
                                         out float gpuThisProcess)
        {
            mainThread = renderThread = gpu = gpuThisProcess = -1f;
            if (perfMissing) return LoadState.Missing;

            float m = -1f;
            float r = -1f;
            float g = -1f;
            float o = -1f;
            int state = 0;
            if (!TryNative(() => state = KspPerfLoad(out m, out r, out g, out o)))
            {
                perfMissing = true;
                return LoadState.Missing;
            }

            mainThread = m;
            renderThread = r;
            gpu = g;
            gpuThisProcess = o;
            return state > 0 ? LoadState.Measured : state < 0 ? LoadState.SwitchedOff : LoadState.NotYet;
        }

        // What the native side reports, for the mod's own diagnostics.
        public static string Describe()
        {
            if (!Available) return unavailableReason;

            StringBuilder buffer = new StringBuilder(1024);
            if (!TryCall(() => KspFgStatus(buffer, buffer.Capacity)))
                return unavailableReason;

            return buffer.ToString();
        }

        // DllImport resolution failures surface as exceptions on first use, and
        // there is more than one shape of them: no file, no export, a 32 bit
        // library. All mean the same thing here.
        private static bool TryCall(Action action)
        {
            if (TryNative(action)) return true;

            unavailable = true;
            unavailableReason = "proxy not present (dxgi.dll without the frame generation exports)";
            Debug.Log(UpscalerProbe.Tag + " Frame generation proxy not available; upscaling continues unaffected.");
            return false;
        }

        // TryNative, for an export that answers with a number.
        private static bool TryNative(Func<int> call, out int result)
        {
            result = 0;
            try
            {
                result = call();
                return true;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (BadImageFormatException) { }
            return false;
        }

        // True when the call went through; false when the native side is not
        // there in the form this build expects. What that means is the
        // caller's business: TryCall switches frame generation off, the
        // counters and the load only go quiet.
        private static bool TryNative(Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (BadImageFormatException) { }
            return false;
        }
    }
}
