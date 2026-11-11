using System;

namespace ReDefinition.Atmospheres
{
    // Which of the two ways of composing the veil and the flakes draws now, and how a change between them is blended. Pure: the
    // renderer gives it the setting and what the host answers each frame.
    //
    // Host draws after the upscaler through the main mod's hook, with the render-size depth; Scene draws in the scene before the
    // upscaler. Auto takes Host only for a vendor/upscaler pair whose player metrics are recorded (PrecipTuning key
    // precip.hostProven.<vendor>.<upscaler>) and where the hook answers; every other pair, and every game without the main mod, gets Scene.
    // When the host hook disappears (the main mod is gone, or the hook threw and was dropped) Scene takes over within FadeSeconds.
    // A change of mode, by the setting or by a lost hook, cross-fades: the two modes' veils are composed so that their combined
    // coverage is the one value the view asks for at every moment (no double veil, no dip).
    internal sealed class PrecipCompose
    {
        // The time a change of mode takes, in seconds.
        internal const float FadeSeconds = 0.25f;

        private float hostWeight;
        private float sceneWeight;
        private bool started;
        private bool lostLogged;

        // The share of the picture the host composite draws now, 0 to 1.
        internal float HostWeight
        {
            get { return hostWeight; }
        }

        // The share the scene composite draws now.
        internal float SceneWeight
        {
            get { return sceneWeight; }
        }

        // True once after the host hook was lost while it was drawing (the renderer logs it).
        internal bool TakeLostNotice()
        {
            bool notice = lostLogged;
            lostLogged = false;
            return notice;
        }

        // The mode the setting and the host's answer ask for. Host asked for but not usable falls back to Scene.
        internal static WeatherCompose Resolve(WeatherCompose setting, bool hostUsable, bool pairProven)
        {
            switch (setting)
            {
                case WeatherCompose.Scene:
                    return WeatherCompose.Scene;
                case WeatherCompose.Host:
                    return hostUsable ? WeatherCompose.Host : WeatherCompose.Scene;
                default:
                    return hostUsable && pairProven ? WeatherCompose.Host : WeatherCompose.Scene;
            }
        }

        // One frame on. hostUsable is whether the hook is registered and answering now. Returns the mode wanted.
        internal WeatherCompose Step(WeatherCompose setting, bool hostUsable, bool pairProven, float dt)
        {
            WeatherCompose wanted = Resolve(setting, hostUsable, pairProven);
            float hostTarget = wanted == WeatherCompose.Host ? 1f : 0f;
            float sceneTarget = 1f - hostTarget;
            if (!started)
            {
                started = true;
                hostWeight = hostTarget;
                sceneWeight = sceneTarget;
                return wanted;
            }
            float step = FadeSeconds > 0f ? Math.Max(dt, 0f) / FadeSeconds : 1f;
            if (!hostUsable && hostWeight > 0f)
            {
                // The hook cannot draw any more: the host's share is gone at once and the scene's share runs up from where it was.
                lostLogged = lostLogged || wanted == WeatherCompose.Scene;
                hostWeight = 0f;
                sceneWeight = Math.Min(1f, sceneWeight + step);
                return wanted;
            }
            hostWeight = Toward(hostWeight, hostTarget, step);
            sceneWeight = Toward(sceneWeight, sceneTarget, step);
            return wanted;
        }

        // The alpha one of two cross-fading layers draws with so that the two together cover `alpha`: 1 - (1 - alpha) ^ weight.
        internal static float VeilShare(float alpha, float weight)
        {
            return PrecipMath.VeilShare(alpha, weight);
        }

        private static float Toward(float v, float target, float step)
        {
            if (v < target) return Math.Min(v + step, target);
            return Math.Max(v - step, target);
        }
    }
}
