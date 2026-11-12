using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // What the own cloud pass takes from outside the Atmospheres (package T09): the light of the moons and planets, which the main mod's
    // raytracing pack computes for the ground (RtReflectors.CloudLight) and the Atmospheres cannot reference, and the requests to drop the
    // temporal history. The main mod assigns Moon once at start-up (an integration request of T09, nothing assigns it yet); with Moon null
    // the clouds have the sun and the scene's ambient light only.
    internal static class CloudBridgeLights
    {
        // The light the moons and planets give the clouds of `main` seen from `eye` (world space): the direction towards it, weighted by the
        // brightness of each body, and the colour in units of the sunlight, linear. False where none lights the clouds.
        internal delegate bool MoonLight(CelestialBody main, Vector3d eye, out Vector3 towards, out Color colour);

        internal static MoonLight Moon;

        // Puts a GPU profiler marker with that label into a command buffer (the main mod's GpuProfiler.Mark); the main mod assigns it. Null: no markers,
        // and the cost of the cloud pass is not separable in the profiler's report (tools/weather_metrics/kt2.py then reports no value).
        internal static System.Action<UnityEngine.Rendering.CommandBuffer, string> Mark;
        internal const string MarkBegin = "ReDefinition clouds: begin", MarkEnd = "ReDefinition clouds: end";

        private static int resets;
        private static string lastReason = "";

        // Counts the requests to drop the history of the temporal reconstruction (a mod's cut, a teleport it knows of); the module compares the
        // count with the one it saw (LayerReconstructDriver.Record).
        internal static int ResetRequests { get { return resets; } }
        internal static string LastResetReason { get { return lastReason; } }

        internal static void RequestHistoryReset(string reason)
        {
            resets++;
            lastReason = reason ?? "";
        }
    }
}
