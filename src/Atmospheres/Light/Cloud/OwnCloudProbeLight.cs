using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The scene's environment light under the own clouds (master plan 46.1 part 3, PRUEFPROTOKOLL 293, 294). Measured: the land's and the vessel's non-sun
    // light comes from KSP's one flight reflection probe (FlightReflectionProbe: realtime, rendered by script, layers Water, Local Scenery and Scaled Scenery,
    // cleared to a solid sky colour); RenderSettings' flat ambient and the SH ambient probe give nothing. The probe never sees the own clouds or their shadow:
    // under a closed deck it keeps lighting the scene with a clear, sunlit sky and land. Below the clouds the sky and the land the probe holds are lit by the
    // diffuse share the clouds overhead pass on, so the probe's intensity is scaled by it: the diffuse share of the vertical optical depth over the camera and a
    // ring round it (the idea check 295: the sky light follows the depth overhead; the sun path's share fails at a low sun and at a cloud's edge). Known limit:
    // the probe's colour stays the clear sky's; the cause fix, the own clouds in the probe, goes with the band on the sea (master plan 46.1).
    // A value someone else writes is the new baseline; everything is put back when the own clouds are not drawn.
    internal static class OwnCloudProbeLight
    {
        private const float RescanSeconds = 1f;
        // The air round the camera whose sky is meant: the camera and RingPoints points RingM away, averaged.
        internal const double RingM = 3000.0;
        internal const int RingPoints = 8;

        // The share of the diffuse flux a non-absorbing layer of vertical optical depth tau passes on (two-stream, AerialPerspectivePlan.TwoStreamDecay).
        internal static double DiffuseShare(float tau)
        {
            return CloudLightMath.OtherLayersTransmission(Mathf.Max(0f, tau), AerialPerspectivePlan.TwoStreamDecay);
        }

        private static ReflectionProbe probe;
        private static float nextScan = -1f;
        private static float baseline = 1f, written = float.NaN;

        // Scales the flight probe's intensity by the lit share at the camera (1: as KSP gives it).
        internal static void Apply(float litShare)
        {
            if (probe == null && Time.realtimeSinceStartup >= nextScan)
            {
                nextScan = Time.realtimeSinceStartup + RescanSeconds;
                FlightReflectionProbe flight = Object.FindObjectOfType<FlightReflectionProbe>();
                probe = flight != null ? flight.probeComponent : null;
                written = float.NaN;
            }
            if (probe == null) return;
            // Someone else's value (or the first frame) becomes the baseline.
            if (float.IsNaN(written) || Mathf.Abs(probe.intensity - written) > 1e-5f) baseline = probe.intensity;
            written = baseline * Mathf.Clamp01(litShare);
            probe.intensity = written;
        }

        // Puts the probe's intensity back (the own clouds not drawn, the scene left).
        internal static void Release()
        {
            if (probe != null && !float.IsNaN(written) && Mathf.Abs(probe.intensity - written) <= 1e-5f) probe.intensity = baseline;
            written = float.NaN;
        }
    }
}
