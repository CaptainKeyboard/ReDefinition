using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Writes the weather view's visibility extinction (the loss of visibility in rain, snow, dust and fog, meteorological optical range) into
    // Unity's global fog, late in Camera.onPreCull of the flight camera (after the frame driver wrote the view in LateUpdate, and after Scatterer
    // and Deferred had their turn in Update). The fog stays the scene's own, so it is depth-correct, part of the colour the upscaler sees, and
    // consistent with the geometry's motion.
    //
    // The values follow the view through VisibilityExtinctionRamp (nothing moves faster than a 12 % swing at 3 Hz), the player's visibilityExtinctionBrightness scales the
    // colour, and FogController keeps the bookkeeping: the fog as it was, the fog written last, and the way back, which happens only
    // while the fog is still the one this writer wrote. Another writer's change is counted (ExternalWrites) and logged once.
    //
    // The assumption that TUFX's post-processing leaves RenderSettings.fog visibly applied is not verified [VERIFY via RECON P7]: where
    // the fog does not show, the precipitation extinction carries the look of the weather.
    //
    // Motion vectors, depth, masks: none (a global state). Frame generation: part of the colour, interpolated. Raytracing: the
    // extinction density is also read from the view by the raytracing side. Exposure: scene units.
    [AtmospheresModule(61)]
    internal sealed class VisibilityExtinctionWriter : IAtmospheresModule
    {
        private readonly FogController tracker = new FogController();
        private readonly VisibilityExtinctionRamp ramp = new VisibilityExtinctionRamp();
        private bool initialised;
        private bool writing;
        private bool wroteOnce;
        private int lastFrame = -1;
        private bool loggedStart;
        private bool loggedExternal;
        private float lastExternalDensity = -1f;
        private float lastLoggedDensity = -1f;
        private float nextLogAt;

        // The start parameter -atmoNoVisibilityExtinction: this run writes no visibility extinction (owner 2026-11-23: the cloud tests need a free view, "das müsste eher
        // ein Startparameter sein"; it stays on in every run without it). Read once: the command line does not change while the game runs.
        internal const string NoVisibilityExtinctionArgument = "-atmoNoVisibilityExtinction";
        internal static readonly bool NoVisibilityExtinctionByArgument = CommandLine.Has(NoVisibilityExtinctionArgument, true);
        private bool loggedNoVisibilityExtinction;

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
                AtmospheresLog.Failed("visibility-extinction-writer", "writing the fog failed; it is put back and the writer is off", e);
                Release("failure");
                initialised = false;
                Camera.onPreCull -= OnPreCull;
            }
        }

        private void Write()
        {
            if (NoVisibilityExtinctionByArgument)
            {
                if (!loggedNoVisibilityExtinction)
                {
                    loggedNoVisibilityExtinction = true;
                    AtmospheresLog.Say("visibility extinction: off for this run (start parameter " + NoVisibilityExtinctionArgument + ")");
                }
                if (writing) Release("start parameter " + NoVisibilityExtinctionArgument);
                return;
            }
            WeatherView view = WeatherHooks.View;
            bool on = AtmospheresSettings.Enabled && AtmospheresSettings.WeatherEffects && !view.IsStale(Time.frameCount) && view.Active
                      && view.VisibilityExtinctionDensity > 0f;
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
                    AtmospheresLog.WarnOnce("visibility extinction: another writer changed the fog density (found " + found.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)
                                            + ", written " + tracker.WrittenDensity.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture) + "); the writer keeps writing and counts it");
                }
            }

            Color c = view.VisibilityExtinctionColour;
            ramp.Step(view.VisibilityExtinctionDensity, c.r, c.g, c.b, view.Level, Tuning.Get("visibility.gustAmp", 0.1f), Tuning.Get("visibility.gustPeriodS", 6f),
                Tuning.Get("visibility.hertz", 3f), AtmospheresSettings.VisibilityExtinctionBrightness, dt);
            Apply();
            if (!loggedStart)
            {
                loggedStart = true;
                AtmospheresLog.Say("visibility extinction: the weather view's visibility extinction is written into the scene fog (exponential squared, late in onPreCull)");
            }
            LogState(view);
        }

        // The fog the scene holds while this writer owns it, logged when the density moved by a tenth or after ten seconds: the share of a far object's own colour that
        // the exponential squared fog leaves is exp(-(distance x density)^2), so a density of 1e-4 leaves 5 % of a terrain at 17 km (a far mountain drawn in the fog
        // colour, flat, is the look of a held fog, not of the air).
        private void LogState(WeatherView view)
        {
            float now = Time.realtimeSinceStartup;
            float density = ramp.Density;
            bool moved = lastLoggedDensity < 0f || Mathf.Abs(density - lastLoggedDensity) > 0.1f * Mathf.Max(density, lastLoggedDensity);
            if (!moved && now < nextLogAt) return;
            lastLoggedDensity = density;
            nextLogAt = now + 10f;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            AtmospheresLog.Say("visibility extinction: scene fog held " + density.ToString("0.000000", inv) + " (the view asks " + view.VisibilityExtinctionDensity.ToString("0.000000", inv) + "), colour "
                               + ramp.R.ToString("0.00", inv) + " " + ramp.G.ToString("0.00", inv) + " " + ramp.B.ToString("0.00", inv) + ", level "
                               + view.Level.ToString("0.000", inv) + ", visibility " + view.VisibilityM.ToString("0", inv) + " m, "
                               + (density > 1e-9f ? "5 % of the terrain's own colour is left at " + (1.7320508f / density).ToString("0", inv) + " m" : "no fog"));
        }

        // The view has no visibility extinction: the written fog runs down to nothing and the saved fog goes back once it is zero.
        private void Fade(float dt)
        {
            ramp.Step(0f, ramp.R, ramp.G, ramp.B, 0f, 0f, 6f, Tuning.Get("visibility.hertz", 3f), 1f, dt);
            if (ramp.Density > 1e-6f)
            {
                Apply();
                LogState(WeatherHooks.View);
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
            AtmospheresLog.Say("visibility extinction: released (" + reason + ")");
        }
    }
}
