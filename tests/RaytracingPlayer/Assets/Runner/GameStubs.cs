using UnityEngine.Rendering;

namespace ReDefinition.Probe
{
    // The GPU profiler's markers that RtCamera puts in its command buffers, as
    // src/Probe/GpuProfiler.cs has them: the test player has no profiler, so none are
    // put in.
    internal static class GpuProfiler
    {
        internal static bool On
        {
            get { return false; }
        }

        internal static void Mark(CommandBuffer buffer, string label)
        {
        }
    }
}

namespace ReDefinition.Bridges
{
    // Whether Windows refused the game's dxgi.dll proxy (src/Bridges/ProxyLoad.cs), which
    // NativeUpscalerLink names first: the test player loads the proxy by its own path.
    internal static class ProxyLoad
    {
        internal static string Refused
        {
            get { return null; }
        }
    }
}
