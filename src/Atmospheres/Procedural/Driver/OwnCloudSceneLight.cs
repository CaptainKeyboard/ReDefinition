using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Whether the own clouds' shadow carries the scene's sun this frame (master plan 46.1, PRUEFPROTOKOLL 288-290). Measured: the land takes 73 % of its light
    // from the scene's sun (287), and the own cloud shadow's mask reaches land, ground and vessel alike (289, a forced factor 0 darkens each as the sun off
    // does); it dims the sun by the direct beam plus the light the clouds scatter down. The weather's global sun dimming (LightWriter, the precipitation
    // look) on top would count the clouds twice: while the shadow is drawn the writer leaves the sun alone. Without the shadow (switched off, not drawn,
    // no own clouds) the writer dims as before.
    internal static class OwnCloudSceneLight
    {
        private static int frame = -10;
        private static bool published;

        // Published by the own clouds' frame (OwnCloudModule) while their shadow is drawn, before the cameras cull.
        internal static void Publish()
        {
            frame = Time.frameCount;
            published = true;
        }

        // Whether the shadow carried the sun this or the last frame (the writer's LateUpdate may run before the module's frame in the same frame).
        // Nothing published yet: false without asking the engine (the unit tests run the writer outside it).
        internal static bool ShadowCarriesSun { get { return published && FrameNow() - frame <= 1; } }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int FrameNow()
        {
            return Time.frameCount;
        }
    }
}
