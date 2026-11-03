using System.Runtime.InteropServices;

namespace ReDefinition.Proxy
{
    // The proxy measures where a frame's time goes (LoadMonitor.h): the main and render
    // threads' CPU time and the game's adapter's GPU load. This is the managed half that
    // registers the main thread and reads the last second's figures.
    //
    // Without a proxy, or with one older than the mod, the load simply stays unknown.
    internal static class ProxyLoadMonitor
    {
        // The proxy's file name, as the DllImports below resolve it in this process.
        private const string Proxy = ProxyDll.Name;

        // Tells the proxy's load monitor which thread is the main thread; called from it.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern void KspPerfRegisterMainThread();

        // The last second's load in percent: 1 with figures, 0 before one, -1 switched off in the ini.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspPerfLoad(out float mainThread, out float renderThread, out float gpu,
                                              out float gpuThisProcess);

        // Set when the proxy lacks the load exports: without them the load stays unknown.
        private static bool perfMissing;

        // Tells the proxy which thread is the game's main thread. Called once, from it.
        public static void RegisterMainThread()
        {
            if (!ProxyCall.TryNative(KspPerfRegisterMainThread)) perfMissing = true;
        }

        // The last completed second of load, each in percent, -1 where unknown.
        // These are the main thread's and the render thread's CPU time as a share of one
        // core, the busiest 3D engine of the game's adapter, and the game's
        // share of it.
        public static FrameGenerationBridge.LoadState Read(out float mainThread, out float renderThread,
                                                           out float gpu, out float gpuThisProcess)
        {
            mainThread = renderThread = gpu = gpuThisProcess = -1f;
            if (perfMissing) return FrameGenerationBridge.LoadState.Missing;

            float m = -1f;
            float r = -1f;
            float g = -1f;
            float o = -1f;
            int state = 0;
            if (!ProxyCall.TryNative(() => state = KspPerfLoad(out m, out r, out g, out o)))
            {
                perfMissing = true;
                return FrameGenerationBridge.LoadState.Missing;
            }

            mainThread = m;
            renderThread = r;
            gpu = g;
            gpuThisProcess = o;
            return StateOf(state);
        }

        // KspPerfLoad's answer as a state: above 0 figures, 0 none yet, below 0 switched off.
        internal static FrameGenerationBridge.LoadState StateOf(int answer)
        {
            return answer > 0 ? FrameGenerationBridge.LoadState.Measured
                 : answer < 0 ? FrameGenerationBridge.LoadState.SwitchedOff
                 : FrameGenerationBridge.LoadState.NotYet;
        }
    }
}
