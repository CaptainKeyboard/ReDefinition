using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using ReDefinition.Settings;
using UnityEngine;

namespace ReDefinition.Flight
{
    // The planets' orbit lines at their opacity at once as the map opens. KSP
    // fades a body's or another vessel's orbit line in and out by how far the
    // camera is from it (OrbitRendererBase.SplineOpacityUpdate, 0.25 a second),
    // and does so only while it draws the map (DrawOrbit; decompiled). A line
    // starts at full opacity (Start), so on the first opening of the map, and on
    // each opening after the camera moved, the planets' orbits show across a close
    // view and take four seconds to fade out. For a second from the first update
    // after the map opens, and after the tracking station loads, a planet's line
    // (a body's about the sun) is taken to the end of its fade at once; moons' and
    // vessels' lines, and every line afterwards, fade as in KSP. The second starts
    // at the first update, not at the event, so that frames between the two do
    // not use it up.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class OrbitLineFade : MonoBehaviour
    {
        private const string HarmonyId = "ReDefinition.OrbitLineFade";
        private const float Seconds = 1f;
        // An opening not yet drawn; one in its second, and when that ends; the
        // updates in it and the fades snapped.
        private static bool pending;
        private static bool open;
        private static float until;
        private static int updates;
        private static int snapped;

        internal static MethodBase Target()
        {
            return typeof(OrbitRendererBase).GetMethod("SplineOpacityUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            HarmonyHooks.Safely("orbit-line-fade", "The orbit lines fade in and out as in KSP when the map opens", () =>
            {
                HarmonyHooks.Install(HarmonyId, typeof(OrbitLineFade),
                    new List<HarmonyHook> { new HarmonyHook(Target(), nameof(OpacityPrefix), nameof(OpacityPostfix), null) });
                // Instance methods: KSP's events take the handler's Target's type
                // (EvtDelegate; decompiled), so a static one throws.
                GameEvents.OnMapEntered.Add(Snap);
                GameEvents.onLevelWasLoadedGUIReady.Add(Loaded);
            });
        }

        private void Snap()
        {
            pending = true;
        }

        private void Loaded(GameScenes scene)
        {
            if (scene == GameScenes.TRACKSTATION) Snap();
        }

        private static void OpacityPrefix(float ___lineOpacity, out float __state)
        {
            __state = ___lineOpacity;
        }

        private static void Report()
        {
            open = false;
            Debug.Log(Log.Tag + " Orbit lines: " + snapped + " of " + updates
                      + " planets' opacity updates in the map's first second taken to their end.");
        }

        // A fade this frame taken to its end: out to 0, in to 1. Moons' and
        // vessels' lines fade as in KSP.
        private static void OpacityPostfix(OrbitRendererBase __instance, ref float ___lineOpacity, float __state)
        {
            float now = Time.unscaledTime;
            if (pending)
            {
                if (open) Report();
                pending = false;
                open = true;
                until = now + Seconds;
                updates = 0;
                snapped = 0;
            }
            if (!open) return;
            if (now > until)
            {
                Report();
                return;
            }
            CelestialBody body = __instance.celestialBody;
            if (body == null || Planetarium.fetch == null || body.referenceBody != Planetarium.fetch.Sun || body == Planetarium.fetch.Sun)
                return;
            updates++;
            if (___lineOpacity == __state) return;
            ___lineOpacity = ___lineOpacity < __state ? 0f : 1f;
            snapped++;
        }
    }
}
