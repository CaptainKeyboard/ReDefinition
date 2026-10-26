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
    // NativeUpscalerLink names first: the test player loads the proxy by its own path.
    internal static class ProxyLoad
    {
        // No refusal to name: the test player loads the proxy itself.
        internal static string Refused
        {
            get { return null; }
        }
    }
}


namespace ReDefinition.Scenery
{
    // The space centre's lawn material, which RtMaterialIntent and RtStockTerrain ask whether a material is
    // (src/Scenery/KscGround.cs): the test player has no space centre, so no material is.
    internal static class KscGround
    {
        // The lawn's colour as the shader draws it; unused where no material is the lawn's.
        internal static readonly UnityEngine.Color LawnColour = new UnityEngine.Color(0.2f, 0.3f, 0.1f);

        // Never: the test player's materials are not the space centre's lawn.
        internal static bool Draws(UnityEngine.Material material)
        {
            return false;
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
