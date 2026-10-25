using UnityEngine;

namespace ReDefinition.Raytracing
{
    // How dark the sky is where the camera is, 0 by day to 1 once the sun is some degrees
    // under the horizon: the eye's adaptation to the night, in which a moon stands out
    // far brighter than its light on the ground. The moons' discs in the sky (MoonGlow)
    // and mirrored by water (RtReflectors, the surfaces the reflections see) are made
    // brighter by it; the light they cast is not.
    internal static class NightAdaptation
    {
        // At full night, a moon's disc in the water as many times as bright as its albedo
        // makes it. The real moon outshines the night sea by orders of magnitude, so that
        // the few per cent water reflects still show its path; the screen clips the disc in
        // the sky to white, the water's reflection takes its light as it is, as the sun's.
        // So much that the path's middle comes to about white at night: brighter, the
        // screen clips it to a white band wider than the path the waves give (Beckmann's,
        // as tools/sea_reflection_check.py measures it). Measured at sea, 2026-10-04, at
        // 1000: the path's middle 3 to 17 times white (7 on average), its half width on the
        // screen 1.6 to 2 times the waves' (5.0 for 2.5 degrees from 10 m, 6.5 for 4.0
        // from 1 km), the player's "far too wide".
        internal const float MoonBoost = 150f;

        // 0 with the sun about 7 degrees or more over the eye's horizon, 1 with it as far under it, linear in the sine
        // of its height between; 0 without a body or star.
        internal static float Night(Vector3d eye, CelestialBody main, CelestialBody star)
        {
            if (main == null || star == null) return 0f;
            Vector3d up = (eye - main.position).normalized;
            double sunHeight = Vector3d.Dot(up, (star.position - eye).normalized);
            return Mathf.Clamp01(0.5f - (float)sunHeight * 4f);
        }

        // The factor on a moon's disc: 1 by day, MoonBoost at full night.
        internal static float DiscFactor(Vector3d eye, CelestialBody main, CelestialBody star)
        {
            return Mathf.Lerp(1f, MoonBoost, Night(eye, main, star));
        }
    }
}
