using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The scene's environment light under the own clouds (master plan 46.1 part 3, PRUEFPROTOKOLL 293, 294). Measured: the land's and the vessel's non-sun
    // light comes from KSP's one flight reflection probe (FlightReflectionProbe: realtime, rendered by script, layers Water, Local Scenery and Scaled Scenery,
    // cleared to a solid sky colour); RenderSettings' flat ambient and the SH ambient probe give nothing. The probe never sees the own clouds or their shadow:
    // under a closed deck it keeps lighting the scene with a clear, sunlit sky and land. Below the clouds the sky and the land the probe holds are lit by the
    // share of the flux the clouds pass on (AerialPerspectivePlan.LitShare, the same share the air under them gets), so the probe's intensity is scaled by it.
    // A value someone else writes is the new baseline; everything is put back when the own clouds are not drawn.
    internal static class OwnCloudProbeLight
    {
        private const float RescanSeconds = 10f;

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
