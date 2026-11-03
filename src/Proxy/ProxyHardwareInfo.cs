using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ReDefinition.Proxy
{
    // What the proxy knows about the machine rather than about frame generation: the NVIDIA GPU
    // of the adapter KSP renders on and where the ini says NVIDIA's DLLs lie.
    //
    // Asked whatever the proxy's ini says about frame generation, and quietly: without the
    // proxy, or with one older than the mod, the answers are "none known" and "unset".
    internal static class ProxyHardwareInfo
    {
        // The proxy's file name, as the DllImports below resolve it in this process.
        private const string Proxy = ProxyDll.Name;

        // NVAPI's architecture and implementation of the adapter's NVIDIA GPU; 0 where there is none known.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspNvidiaGpu(out uint architecture, out uint implementation);

        // The ini's dlssDirectory and streamlineDirectory, UTF-16, cut to size characters.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern void KspNvidiaDirectories(StringBuilder dlss, StringBuilder streamline, int size);

        // The NVIDIA GPU of the adapter KSP renders on (KspNvidiaGpu): NVAPI's
        // architecture and implementation, what NVIDIA's DLLs are offered for
        // download by (NvidiaFiles). False where it is not NVIDIA's, where NVAPI does
        // not answer, or without the proxy.
        public static bool NvidiaGpu(out uint architecture, out uint implementation)
        {
            architecture = 0;
            implementation = 0;
            int known;
            if (!ProxyCall.TryNative(NvidiaGpuCall, out known) || known == 0) return false;
            architecture = gpuArchitecture;
            implementation = gpuImplementation;
            return true;
        }

        // NvidiaGpu's answer, filled by the call below it.
        private static uint gpuArchitecture;
        private static uint gpuImplementation;
        private static readonly Func<int> NvidiaGpuCall = () => KspNvidiaGpu(out gpuArchitecture, out gpuImplementation);

        // Where the proxy's ini says NVIDIA's DLLs lie, dlssDirectory and
        // streamlineDirectory; empty where unset or without the proxy.
        public static void NvidiaDirectories(out string dlss, out string streamline)
        {
            StringBuilder dlssBuffer = new StringBuilder(260);
            StringBuilder streamlineBuffer = new StringBuilder(260);
            bool read = ProxyCall.TryNative(() => KspNvidiaDirectories(dlssBuffer, streamlineBuffer, 260));
            dlss = read ? dlssBuffer.ToString() : "";
            streamline = read ? streamlineBuffer.ToString() : "";
        }
    }
}
