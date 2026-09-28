using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using ReDefinition.Settings;
using UnityEngine;

namespace ReDefinition.Flight
{
    // The planets' orbit lines shown or hidden at once as the camera asks. KSP
    // fades a body's or another vessel's orbit line in and out by how far the
    // camera is from it (OrbitRendererBase.SplineOpacityUpdate, 0.25 a second),
    // and does so only while it draws the map (DrawOrbit; decompiled). A line
    // starts at full opacity (Start) and keeps the opacity it had when the map
    // closed, so on opening the map close to a planet the other planets' orbits
    // showed across the view for four seconds, and zoomed out they were missing
    // for as long. A planet's line (a body's about the sun) is taken to the end of
    // each fade at once, at every opening and while zooming; the moons' and
    // vessels' lines fade as in KSP.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class OrbitLineFade : MonoBehaviour
    {
        private const string HarmonyId = "ReDefinition.OrbitLineFade";
        private static bool noted;

        internal static MethodBase Target()
        {
            return typeof(OrbitRendererBase).GetMethod("SplineOpacityUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            HarmonyHooks.Safely("orbit-line-fade", "The planets' orbit lines fade in and out as in KSP", () =>
                HarmonyHooks.Install(HarmonyId, typeof(OrbitLineFade),
                    new List<HarmonyHook> { new HarmonyHook(Target(), nameof(OpacityPrefix), nameof(OpacityPostfix), null) }));
        }

        private static void OpacityPrefix(float ___lineOpacity, out float __state)
        {
            __state = ___lineOpacity;
        }

        // A planet's fade this frame taken to its end: out to 0, in to 1.
        private static void OpacityPostfix(OrbitRendererBase __instance, ref float ___lineOpacity, float __state)
        {
            if (___lineOpacity == __state) return;
            CelestialBody body = __instance.celestialBody;
            Planetarium planetarium = Planetarium.fetch;
            if (body == null || planetarium == null || body == planetarium.Sun || body.referenceBody != planetarium.Sun) return;
            ___lineOpacity = ___lineOpacity < __state ? 0f : 1f;
            if (noted) return;
            noted = true;
            Debug.Log(Log.Tag + " Orbit lines: the planets' shown and hidden at once, first " + body.bodyName + "'s.");
        }
    }
}
