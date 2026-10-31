using UnityEngine.Rendering;

namespace ReDefinition.Probe
{
    // The GPU profiler's markers that RtCamera puts in its command buffers, as
    // src/Probe/GpuProfiler.cs has them: the test player has no profiler, so none are
    // put in.
    internal static class GpuProfiler
    {
        // Never on, so RtCamera adds no markers.
        internal static bool On
        {
            get { return false; }
        }

        // Puts no marker in: there is no profiler to read it.
        internal static void Mark(CommandBuffer buffer, string label)
        {
        }
    }
}

namespace ReDefinition.Proxy
{
    // Whether Windows refused the game's dxgi.dll proxy (src/Proxy/ProxyLoad.cs), which
    // NativeUpscalerLink names first: the test player has the proxy beside its exe and checks nothing.
    internal static class ProxyLoad
    {
        // No refusal to name: the test player does not ask Windows.
        internal static string Refused
        {
            get { return null; }
        }
    }
}

namespace ReDefinition.Raytracing
{
    // Parallax's grass ranges, which RtCamera reads for the meadow's shade (src/Raytracing/Ksp/RtKspMods.cs): the
    // test player has no Parallax, so there is no grass.
    internal static class RtKspMods
    {
        // No grass range about the camera or the vessel.
        internal static float ParallaxGrassRange(out float vesselRange)
        {
            vesselRange = 0f;
            return 0f;
        }

        // No vessel grass: the camera's own position.
        internal static UnityEngine.Vector3 ParallaxVessel(UnityEngine.Vector3 camera)
        {
            return camera;
        }
    }
}
