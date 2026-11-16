namespace ReDefinition.Atmospheres.Viewer
{
    // When the viewer may move the vessel. A vessel that is still loading or packed (a pad vessel is, for about 6 s after the flight scene is ready: the game logs 'landed - waiting for
    // ground contact to resume physics'; the logs show it packed for 6 to 15 s) is snapped to the ground when it unpacks, whatever was done to its position meanwhile: a vessel the viewer had put 300 m up was moved 308 m
    // down at its unpacking and shed about 25 debris vessels (K-V1, K-S1 and K-S2 logs of 2026-11-15/16). So the viewer places nothing until the vessel has been loaded and
    // unpacked for UnpackedSettleS seconds, and says so in its window. Pure: the clock and the state are given.
    internal sealed class PlacementGuard
    {
        internal const double UnpackedSettleS = 3.0;
        internal const string LoadingMessage = "Vessel is still loading, try again";
        internal const string NoVesselMessage = "No active vessel";

        // The time since which the vessel has been loaded and unpacked without a break; NaN while it is not.
        private double since = double.NaN;

        // Notes the vessel's state at this time (real seconds); call it every frame and before each placement.
        internal void Observe(double now, bool loadedAndUnpacked)
        {
            if (!loadedAndUnpacked) since = double.NaN;
            else if (double.IsNaN(since)) since = now;
        }

        // Why the vessel may not be placed now, or null where it may.
        internal string Refusal(double now, bool hasVessel)
        {
            if (!hasVessel) return NoVesselMessage;
            if (double.IsNaN(since) || now - since < UnpackedSettleS) return LoadingMessage;
            return null;
        }

        internal const double MaxSpeedMs = 50.0;
        internal const string WarpMessage = "Time warp is on, set it to 1x";
        internal const string EvaMessage = "A kerbal on EVA is not placed";

        // Why a loaded vessel is not teleported whatever its age: time warp (a held vessel warped would be packed as landed and snapped to the ground), a kerbal on EVA, a speed over
        // MaxSpeedMs relative to the surface (the placement stops it dead). Null where none of them.
        internal static string StateRefusal(bool timeWarp, bool eva, double surfaceSpeedMs)
        {
            if (timeWarp) return WarpMessage;
            if (eva) return EvaMessage;
            if (surfaceSpeedMs > MaxSpeedMs) return "Vessel is moving at " + surfaceSpeedMs.ToString("0") + " m/s, slow down first";
            return null;
        }

        // The vessel is forgotten (a new scene, a new vessel): it has to settle again.
        internal void Reset()
        {
            since = double.NaN;
        }
    }
}
