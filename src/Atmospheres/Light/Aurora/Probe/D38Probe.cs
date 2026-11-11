using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The probe of package D38 inside the integrated series K-W3b (start lights): the state of the aurora shell in the running game. It
    // writes what the game can say about itself: that the shader is in the bundle and the renderer started, that the Auto setting
    // draws where no foreign aurora layer exists, what the model says at the current time, and whether the log stays free of TUFX
    // lines (the shell never touches TUFX). The pixel metrics (lid, drip, band) need the screenshots of a night pose per latitude and
    // are rows that are not required until the series' screenshot step reads them with tools/weather_metrics/aurora.py [VERIFY: not
    // measured yet].
    [AtmospheresModule(986)]
    internal sealed class D38Probe : SeriesProbe
    {
        internal const string Series = "K-W3b";

        protected override string SeriesName
        {
            get { return Series; }
        }

        protected override double MaxSeconds
        {
            get { return 6 * 60; }
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            yield return ProbeWait.Until(() => HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready && FlightGlobals.ActiveVessel != null,
                330, "the flight");
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                Fail("D38: no flight to measure in");
                yield break;
            }
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            yield return ProbeWait.Until(() => AuroraRenderer.Ready, 60, "the aurora renderer");
            Emit("D38.shader_loaded", AuroraRenderer.Ready);

            bool volumetric;
            Emit("D38.auto_draws_without_foreign", AuroraPolicy.ShellWanted(AuroraMode.Auto, false, out volumetric) && !volumetric);
            Emit("D38.auto_leaves_foreign", !AuroraPolicy.ShellWanted(AuroraMode.Auto, true, out volumetric));

            // The model at the current time, and the renderer's own state after some frames.
            yield return ProbeWait.Frames(120);
            double ut = Planetarium.GetUniversalTime();
            Emit("D38.model_activity", AuroraModel.Activity(ut));
            Emit("D38.model_event", AuroraModel.StrongEvent(ut));
            CelestialBody body = FlightGlobals.currentMainBody;
            BodyClimate climate = body != null ? Bodies.Get(body) : null;
            Emit("D38.body_rule_enabled", climate != null && climate.Aurora.Enabled);
            EmitText("D38.state", AuroraRenderer.Describe());
            Emit("D38.foreign_detected", AuroraRenderer.ForeignDetected);
        }
    }
}
