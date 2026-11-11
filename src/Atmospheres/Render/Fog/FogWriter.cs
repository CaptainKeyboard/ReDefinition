using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Writes the weather view's fog into Unity's global fog, late in Camera.onPreCull of the flight camera (after the frame driver
    // wrote the view in LateUpdate, and after Scatterer and Deferred had their turn in Update). The fog stays the scene's own, so it is
    // depth-correct, part of the colour the upscaler sees, and consistent with the geometry's motion.
    //
    // The values follow the view through FogRamp (nothing moves faster than a 12 % swing at 3 Hz), the player's fogBrightness scales the
    // colour, and FogController keeps the bookkeeping: the fog as it was, the fog written last, and the way back, which happens only
    // while the fog is still the one this writer wrote. Another writer's change is counted (ExternalWrites) and logged once.
    //
    // The assumption that TUFX's post-processing leaves RenderSettings.fog visibly applied is not verified [VERIFY via RECON P7]: where
    // the fog does not show, the veil carries the look of the weather.
    //
    // Motion vectors, depth, masks: none (a global state). Frame generation: part of the colour, interpolated. Raytracing: the fog
    // density is also read from the view by the raytracing side. Exposure: scene units.
    [AtmospheresModule(61)]
    internal sealed class FogWriter : IAtmospheresModule
    {
        private readonly FogController tracker = new FogController(false);
        private readonly FogRamp ramp = new FogRamp();
        private bool initialised;
        private bool writing;
        private bool wroteOnce;
        private int lastFrame = -1;
        private bool loggedStart;
        private bool loggedExternal;
        private float lastExternalDensity = -1f;

        // How many times the fog was found changed by another writer.
        internal static int ExternalWrites { get; private set; }
        // The density this writer wrote last.
        internal static float WrittenDensity { get; private set; }

        public void Init()
        {
            Camera.onPreCull += OnPreCull;
            initialised = true;
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
            ramp.Reset();
        }

        public void Tick(float dt)
        {
        }

        public void Shutdown()
        {
            if (initialised) Camera.onPreCull -= OnPreCull;
            initialised = false;
            Release("shutdown");
        }

        private void OnPreCull(Camera cam)
        {
            if (!initialised) return;
            Camera flight = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (flight == null || cam != flight || lastFrame == Time.frameCount) return;
            lastFrame = Time.frameCount;
            try
            {
                Write();
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("fog-writer", "writing the fog failed; it is put back and the writer is off", e);
                Release("failure");
                initialised = false;
                Camera.onPreCull -= OnPreCull;
            }
        }

        private void Write()
        {
            WeatherView view = WeatherHooks.View;
            bool on = AtmospheresSettings.Enabled && AtmospheresSettings.WeatherEffects && !view.IsStale(Time.frameCount) && view.Active
                      && view.FogDensity > 0f && !FogController.LegacyHolds;
            float dt = Time.timeScale > 0f ? Mathf.Min(Time.unscaledDeltaTime, 0.25f) : 0f;
            if (!on)
            {
                if (writing) Fade(dt);
                return;
            }

            if (!writing)
            {
                tracker.Save(RenderSettings.fog, RenderSettings.fogMode, RenderSettings.fogDensity, RenderSettings.fogColor);
                ramp.Reset();
                writing = true;
                wroteOnce = false;
                lastExternalDensity = -1f;
            }

            // Another writer changed the fog since our last write: count it once per new value and log the first.
            float found = RenderSettings.fogDensity;
            if (wroteOnce && Math.Abs(found - tracker.WrittenDensity) > FogController.Same && Math.Abs(found - lastExternalDensity) > FogController.Same)
            {
                lastExternalDensity = found;
                tracker.CountExternal();
                ExternalWrites = tracker.ExternalWrites;
                if (!loggedExternal)
                {
                    loggedExternal = true;
                    AtmospheresLog.WarnOnce("fog: another writer changed the fog density (found " + found.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)
                                            + ", written " + tracker.WrittenDensity.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture) + "); the writer keeps writing and counts it");
                }
            }

            Color c = view.FogColour;
            ramp.Step(view.FogDensity, c.r, c.g, c.b, view.Level, Tuning.Get("fog.gustAmp", 0.1f), Tuning.Get("fog.gustPeriodS", 6f),
                Tuning.Get("fog.hertz", 3f), AtmospheresSettings.FogBrightness, dt);
            Apply();
            if (!loggedStart)
            {
                loggedStart = true;
                AtmospheresLog.Say("fog: the weather view's fog is written into the scene fog (exponential squared, late in onPreCull)");
            }
        }

        // The view has no fog: the written fog runs down to nothing and the saved fog goes back once it is zero.
        private void Fade(float dt)
        {
            ramp.Step(0f, ramp.R, ramp.G, ramp.B, 0f, 0f, 6f, Tuning.Get("fog.hertz", 3f), 1f, dt);
            if (ramp.Density > 1e-6f)
            {
                Apply();
                return;
            }
            Release("weather ended");
        }

        private void Apply()
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = ramp.Density;
            var colour = new Color(ramp.R, ramp.G, ramp.B, 1f);
            RenderSettings.fogColor = colour;
            tracker.Wrote(ramp.Density, colour);
            WrittenDensity = ramp.Density;
            wroteOnce = true;
        }

        // Puts the saved fog back if the fog is still the one written; leaves it alone if another writer owns it now.
        private void Release(string reason)
        {
            if (!writing) return;
            writing = false;
            bool wasSaved = tracker.Saved;
            if (!wasSaved) return;
            FogMode mode = tracker.SavedMode;
            bool fog = tracker.SavedFog;
            float density = tracker.SavedDensity;
            Color colour = tracker.SavedColour;
            if (tracker.Restore(RenderSettings.fog, RenderSettings.fogMode, RenderSettings.fogDensity, RenderSettings.fogColor))
            {
                RenderSettings.fog = fog;
                RenderSettings.fogMode = mode;
                RenderSettings.fogDensity = density;
                RenderSettings.fogColor = colour;
            }
            WrittenDensity = 0f;
            ramp.Reset();
            AtmospheresLog.Say("fog: released (" + reason + ")");
        }
    }
}
