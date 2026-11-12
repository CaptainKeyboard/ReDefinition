using System;

namespace ReDefinition.Atmospheres
{
    // The decisions of the module that drives the own cloud pass in the game (package T09), as functions of plain numbers so that the tests
    // can run them: whether the pass draws, when the maps and the height maps are fetched, what counts as a cut in the weather, which
    // camera event the pass sits at, and the flash gain of the lightning.
    internal static class OwnCloudPlan
    {
        // The seconds between two fetches of the layers' maps, and between two builds of the height maps (a slow rhythm: the offsets move over
        // hours of game time; a build costs milliseconds).
        internal const float MapIntervalS = 1f, HeightIntervalS = 20f, HeightIntervalWarpS = 5f;
        // The flat extra a cut of the weather needs beyond the time that the frames and the warp account for, in s of game time.
        internal const double CutSlackS = 10.0;
        // The Kelvin band around freezing inside which the group choice keeps its last value.
        internal const double FreezingBandK = 1.0;

        // The camera events the pass can sit at (the setting is the tuning key cloud.cameraEvent): 0 BeforeForwardAlpha (after the lit opaque
        // scene, before the transparents), 1 AfterForwardAlpha, 2 BeforeImageEffects. [VERIFY] in the game: where the depth texture is readable
        // and where Scatterer's sky has been drawn (the first integrated series logs the sampled depth).
        internal static int CameraEventIndex(float tuned)
        {
            return Math.Max(0, Math.Min(2, (int)Math.Round(tuned)));
        }

        // Whether the pass draws for a body and a scene: the mod and the own clouds are on, the source in force is Ours, the scene is a flight
        // scene on a body with an air and a set of layers, and the map view is not showing (the shell is the map view's picture).
        internal static bool Wanted(bool enabled, bool flightScene, bool atmosphere, bool weatherRuns, bool ownActive, int layerSpecs, bool mapView)
        {
            return enabled && flightScene && atmosphere && weatherRuns && ownActive && layerSpecs > 0 && !mapView;
        }

        // Whether the maps are due: the interval has passed, or the stack was just built.
        internal static bool Due(float now, float last, float interval, bool force)
        {
            return force || now - last >= interval;
        }

        // The interval of the height maps by the time warp: faster under warp, when the offsets move faster.
        internal static float HeightInterval(float warpRate)
        {
            return warpRate > 4f ? HeightIntervalWarpS : HeightIntervalS;
        }

        // Whether the universal time jumped beyond what the frames and the warp account for (a revert, a quickload, a day skipped): the history
        // of the temporal reconstruction is not the weather of now. dt is the real frame time in s.
        internal static bool WeatherCut(double ut, double lastUt, double dt, double warpRate)
        {
            double allowed = CutSlackS + 2.0 * dt * Math.Max(1.0, warpRate);
            return Math.Abs(ut - lastUt) > allowed;
        }

        // The brightness of the flash as the layer light takes it: the sun's brightness in the frame's units over the harness's sun (3.0), the
        // unit the lightning peak is given in (LightningModule.FillBolts peakScale).
        internal static float FlashPeakScale(float sunLuminance)
        {
            return Math.Max(0.1f, sunLuminance / HarnessSunLuminance);
        }

        // The luminance of the sun colour of the parity harness (3.0, 2.9, 2.7).
        internal const float HarnessSunLuminance = 2.9f;
    }
}
