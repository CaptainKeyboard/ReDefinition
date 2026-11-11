using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The probe of package D38 inside the integrated series K-W3b.
    //
    // Start "lights" (shared with the other probes of the series): the state of the aurora shell in the running game. It writes what the
    // game can say about itself: that the shader is in the bundle and the renderer started, that the Auto setting draws where no foreign
    // aurora layer exists, what the model says at the current time, whether the body transforms' up axis is the rotation axis the shell
    // takes as the pole, and whether the log stays free of TUFX lines (the shell never touches TUFX).
    //
    // Start "aurora" (only this probe runs, -atmoProbe D38Probe): the shell in the real sky with the full mod set, the rows of
    // D38Probe.Sky.cs. It decides, by measurement in the frame, (1) the camera event and the way the shell finds the sky (draw order
    // against Scatterer's sky, D38-01), (2) that terrain and other opaque things hide the shell, and (3) the three artefact metrics
    // (lid, drip, band) at a night pose per latitude (D38-02), each against a shell-off frame of the same pose.
    [AtmospheresModule(986)]
    internal sealed partial class D38Probe : SeriesProbe
    {
        internal const string Series = "K-W3b";
        internal const string SkyStart = "aurora";

        protected override string SeriesName
        {
            get { return Series; }
        }

        protected override double MaxSeconds
        {
            get { return StartId == SkyStart ? 18 * 60 : 6 * 60; }
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

            // The shell takes body.transform.up as the pole (AuroraRenderer.Compose): the largest angle to the rotation axis over the
            // bodies with an atmosphere (a tilted body, D38-07).
            double worst = 0.0;
            foreach (CelestialBody b in FlightGlobals.Bodies)
            {
                if (b == null || !b.atmosphere || b.transform == null) continue;
                double dot = Vector3d.Dot((Vector3d)b.transform.up, b.RotationAxis.normalized);
                worst = Math.Max(worst, Math.Acos(Math.Max(-1.0, Math.Min(1.0, dot))) * 180.0 / Math.PI);
            }
            Emit("D38.pole_axis_max_error_deg", worst);

            if (StartId == SkyStart)
            {
                foreach (ProbeWait w in SkyScenario()) yield return w;
            }
        }
    }
}
