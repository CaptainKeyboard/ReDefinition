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
        // the few per cent water reflects looking down still show its disc; the screen
        // clips the disc in the sky to white, the water's reflection takes its light as it
        // is, as the sun's. The real moon (about 2500 cd/m^2) over a moonlit sea (about
        // 0.005 cd/m^2): a glint from steeply above, 2.6 % of the moon spread by the waves to
        // a fifteenth, still some hundred times brighter than the sea (measured at sea,
        // 2026-10-04: at 300 times the Mun high gave glints of 0.05, unseen; at 3000 the
        // glints of slopes near three times the usual were as bright as the path's middle,
        // and the path too wide, the player found).
        internal const float MoonBoost = 1000f;

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
