using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Writes the light of the weather: the ambient light scaled by WeatherView.AmbientFactor and the sun's Light (intensity by the
    // sun-dimming curve, shadowStrength by the level), and the exposure compensation of TUFX where it is present. The values are written
    // in Camera.onPreCull, once per frame, after every Update and LateUpdate, and the hook is added only when there is something to write
    // so that it comes after the hooks of Scatterer, Deferred and Firefly, whose Light and ambient writes it must follow. A value that
    // someone else wrote is the new baseline (AmbientTracker, the same pattern as StormAmbient); everything is put back when the weather
    // ends and when the scene is left.
    //
    // EVE draws a screen-space cloud shadow of its own, which darkens the ground under cloud already: where an EVE layer carries the
    // weather (WeatherView.CloudLayerName is set) our sun dimming counts with weight SunDimWeightEve (0.5) until E40 has measured EVE's
    // darkening [UNKNOWN]. The weights are Tuning keys "light.sunDimWeight" and "light.sunDimWeightEve".
    internal sealed class LightWriter
    {
        // The level below which nothing is written: the weather scale starts at 0.04.
        internal const float OffBelow = PrecipScale.FlurriesFrom;

        private readonly StormAmbient ambient = new StormAmbient();
        private readonly AmbientTracker sunIntensity = new AmbientTracker();
        private readonly AmbientTracker sunShadow = new AmbientTracker();
        private readonly TufxHook tufx = new TufxHook();
        private Light sun;
        private bool hooked;
        private bool sunScaling;
        private float ambientFactor = 1f;
        private float sunFactor = 1f;
        private float shadowFactor = 1f;
        private float level;
        private int sunFrame = -1;

        // The ambient scaling, for the log.
        internal StormAmbient Ambient
        {
            get { return ambient; }
        }

        // How often someone else changed the ambient light, the sun's intensity or its shadow strength while we scaled it.
        internal int ExternalWrites
        {
            get { return ambient.ExternalWrites + sunIntensity.ExternalWrites + sunShadow.ExternalWrites; }
        }

        // Whether anything is scaled now.
        internal bool Writing
        {
            get { return ambient.Scaling || sunScaling; }
        }

        // The factors the last Update asked for.
        internal float AmbientFactor { get { return ambientFactor; } }
        internal float SunFactor { get { return sunFactor; } }
        internal float ShadowFactor { get { return shadowFactor; } }

        // The sun factor for a look: the curve's direct dimming, counted with the EVE weight where an EVE layer carries the weather.
        internal static float SunFactorOf(float curveSunDirect, bool eveLayerPresent)
        {
            float w = eveLayerPresent ? Tuning.Get("light.sunDimWeightEve", 0.5f) : Tuning.Get("light.sunDimWeight", 1f);
            w = w < 0f ? 0f : w > 1f ? 1f : w;
            return 1f - w * (1f - curveSunDirect);
        }

        // Takes the view of the frame (call it in LateUpdate, after the composer): decides the factors and hooks or unhooks onPreCull.
        internal void Update(WeatherView view, Light sunLight)
        {
            sun = sunLight;
            level = view.Active ? view.Level : 0f;
            if (level < OffBelow)
            {
                ambientFactor = 1f;
                sunFactor = 1f;
                shadowFactor = 1f;
                SyncHook(false);
                Restore();
                return;
            }
            LookValues look = PrecipitationLook.Evaluate(level);
            ambientFactor = view.AmbientFactor;
            sunFactor = SunFactorOf(look.SunDirect, !string.IsNullOrEmpty(view.CloudLayerName));
            shadowFactor = look.ShadowStrength;
            SyncHook(true);
        }

        // Writes the values; once per frame, from Camera.onPreCull.
        private void OnPreCull(Camera camera)
        {
            try
            {
                if (level < OffBelow) return;
                ambient.Apply(ambientFactor);
                ApplySun();
                tufx.Apply(level);
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("atmospheres-light", "the light writer failed and puts the light back", e);
                SyncHook(false);
                Restore();
            }
        }

        private void ApplySun()
        {
            if (sun == null || sunFrame == Time.frameCount) return;
            sunFrame = Time.frameCount;
            sunScaling = true;
            sun.intensity = sunIntensity.Scale(new Color(sun.intensity, 0f, 0f, 1f), sunFactor).r;
            sun.shadowStrength = sunShadow.Scale(new Color(sun.shadowStrength, 0f, 0f, 1f), shadowFactor).r;
        }

        // Puts everything back (the weather ended or the scene is left).
        internal void Restore()
        {
            // The calls that reach the engine are guarded and kept in their own methods, so that an idle writer runs outside the game.
            if (ambient.Scaling) ambient.Restore();
            if (sunScaling) RestoreSun();
            sunFrame = -1;
            tufx.Restore();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void RestoreSun()
        {
            sunScaling = false;
            Color restore;
            if (sun != null)
            {
                if (sunIntensity.Restore(new Color(sun.intensity, 0f, 0f, 1f), out restore)) sun.intensity = restore.r;
                if (sunShadow.Restore(new Color(sun.shadowStrength, 0f, 0f, 1f), out restore)) sun.shadowStrength = restore.r;
            }
            else
            {
                sunIntensity.Restore(Color.black, out restore);
                sunShadow.Restore(Color.black, out restore);
            }
        }

        // Ends the writer: unhooks and restores.
        internal void Shutdown()
        {
            SyncHook(false);
            Restore();
        }

        private void SyncHook(bool need)
        {
            if (need == hooked) return;
            hooked = need;
            if (need) Camera.onPreCull += OnPreCull;
            else Camera.onPreCull -= OnPreCull;
        }
    }
}
