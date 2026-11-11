namespace ReDefinition.Upscaler
{
    // The order the rays try EVE's cloud objects of a body in, and when the packed cloud map is made again. Free of Unity and KSP,
    // for the tests.
    internal static class CloudLayerPreference
    {
        // The shortest time between two repacks caused by the weather repainting its maps (16 MB RG16, a blit of well under 0.1 ms
        // [VERIFY] with the GPU profiler marker).
        internal const float RepaintRepackSeconds = 0.5f;

        // The index of the object tried at `step` (0 to count - 1) when `preferred` (or -1) is tried first and the others follow in their
        // own order: each index comes exactly once.
        internal static int IndexAt(int step, int preferred)
        {
            if (preferred < 0) return step;
            if (step == 0) return preferred;
            return step - 1 < preferred ? step - 1 : step;
        }

        // Whether the weather's repaint of the maps (CloudMapsVersion differs from the packed one) calls for a repack: at most once
        // per RepaintRepackSeconds since the last pack.
        internal static bool RepaintDue(long mapsVersion, long packedVersion, float secondsSincePack)
        {
            return mapsVersion != packedVersion && secondsSincePack >= RepaintRepackSeconds;
        }
    }
}
