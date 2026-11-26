using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // Measurement (master plan 46.1 part 4 stage B, PRUEFPROTOKOLL 314): whether an own reflection probe in KSP's flight probe's box, with a higher importance,
    // REPLACES KSP's probe for the land (the deferred reflections) and the sea (the ocean's unity_SpecCube0) or ADDS to it. light.testOwnProbe 1 puts a probe
    // of mode Custom with a pure red cube on KSP's probe's place and box (importance one above); 0 (the default): none.
    internal static class OwnProbeTest
    {
        internal const string TuneOn = "light.testOwnProbe";
        private static GameObject holder;
        private static ReflectionProbe own, ksp;
        private static Cubemap red;

        // From the own clouds' frame.
        internal static void Sync()
        {
            bool want = Tuning.Get(TuneOn, 0f) > 0.5f;
            if (!want)
            {
                if (holder != null) Object.Destroy(holder);
                holder = null;
                own = null;
                return;
            }
            if (ksp == null)
            {
                FlightReflectionProbe flight = Object.FindObjectOfType<FlightReflectionProbe>();
                ksp = flight != null ? flight.probeComponent : null;
                if (ksp == null) return;
            }
            if (own == null)
            {
                if (red == null)
                {
                    red = new Cubemap(16, TextureFormat.RGBA32, false);
                    var px = new Color[16 * 16];
                    for (int i = 0; i < px.Length; i++) px[i] = new Color(1f, 0f, 0f, 1f);
                    for (int f = 0; f < 6; f++) red.SetPixels(px, (CubemapFace)f);
                    red.Apply();
                }
                holder = new GameObject("ReDefinition own probe test");
                own = holder.AddComponent<ReflectionProbe>();
                own.mode = ReflectionProbeMode.Custom;
                own.customBakedTexture = red;
                own.importance = ksp.importance + 1;
                own.size = ksp.size;
                own.center = ksp.center;
                own.boxProjection = ksp.boxProjection;
                own.blendDistance = ksp.blendDistance;
                own.intensity = 1f;
                AtmospheresLog.Say("light measurement: an own red probe at KSP's flight probe, importance " + own.importance + " (KSP's " + ksp.importance + "), size "
                                   + ksp.size.ToString("F0") + ", blend " + ksp.blendDistance.ToString("0.0") + ", box projection " + ksp.boxProjection);
            }
            holder.transform.position = ksp.transform.position;
        }
    }
}
