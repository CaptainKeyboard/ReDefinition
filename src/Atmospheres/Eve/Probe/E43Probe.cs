using System;
using System.Collections.Generic;
using System.Diagnostics;
using AtmoSim.Core;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The probe of package E43 inside the integrated series K-W4a (one start, run1): the wind of the layers against the running game.
    // Variants (one start): steer-main (layerWindSteering MainPeriod), steer-maps (MapsOnly), steer-off (Off), noshear (windShear off, MainPeriod).
    // Per variant, after 40 s of steering, it reports for the two lowest and the two highest bound layers: the layer height, the field wind, the motion of
    // the pattern plus EVE's drift, the angle and speed error between them (rows E43.layer.*), the largest change of the turning rate per second, the applied
    // upwardsCloudSpeed against the updraft. Once: the cost of a refill of the 3D window and its rows against the core. Not measurable from inside the game
    // and left to the screenshots of the series: the optical-flow arrows of the rendered layers, the trail and duplicate rows of the upscalers and frame
    // generation, the sense of EVE's rotation (a marker test: Tuning e43.rotationSign) [VERIFY: not measured yet].
    [AtmospheresModule(984)]
    internal sealed class E43Probe : SeriesProbe
    {
        internal const string Series = "K-W4a";

        private LayerSteeringMode savedMode;
        private bool savedShear;

        protected override string SeriesName
        {
            get { return Series; }
        }

        protected override double MaxSeconds
        {
            get { return 12 * 60; }
        }

        protected override IList<string> DefaultVariants
        {
            get { return new[] { "steer-main", "steer-maps", "steer-off", "noshear" }; }
        }

        protected override void ApplyVariant(string name)
        {
            savedMode = LayerWindOptions.Steering;
            savedShear = LayerWindOptions.WindShear;
            if (name == "steer-main") LayerWindOptions.Steering = LayerSteeringMode.MainPeriod;
            else if (name == "steer-maps") LayerWindOptions.Steering = LayerSteeringMode.MapsOnly;
            else if (name == "steer-off") LayerWindOptions.Steering = LayerSteeringMode.Off;
            else if (name == "noshear") { LayerWindOptions.Steering = LayerSteeringMode.MainPeriod; LayerWindOptions.WindShear = false; }
        }

        protected override void RevertVariant(string name)
        {
            LayerWindOptions.Steering = savedMode;
            LayerWindOptions.WindShear = savedShear;
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            yield return ProbeWait.Until(() => HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready && FlightGlobals.ActiveVessel != null,
                330, "the flight");
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                Fail("E43: no flight to measure in");
                yield break;
            }
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);
            Emit("E43.eve_available", EveLayerAccess.Available);
            Emit("E43.upwards_available", EveLayerAccess.UpwardsAvailable);
            if (!EveLayerAccess.Available) yield break;
            yield return ProbeWait.Until(() => EveCloudModule.Current != null && EveCloudModule.Current.FirstBoundLayer() != null, 120, "a bound layer");
            EveCloudModule module = EveCloudModule.Current;
            if (module == null || module.FirstBoundLayer() == null) yield break;

            // the window refill: cost and agreement with the core, once
            CelestialBody body = FlightGlobals.currentMainBody;
            GeneratorField gf = GeneratorModule.Current;
            GeneratorState state = gf != null && body != null ? gf.StateOf(body.flightGlobalsIndex) : null;
            if (state != null && !state.Broken)
            {
                var window = new WeatherWindow();
                Vessel v = FlightGlobals.ActiveVessel;
                bool filled = gf.FillWindow(body.flightGlobalsIndex, v.latitude, v.longitude, 40.0, Planetarium.GetUniversalTime(), window);
                Emit("E43.window_filled", filled);
                if (filled)
                {
                    LayerOptions o = LayerWindOptions.Layers();
                    var sw = Stopwatch.StartNew();
                    WindowAdvection.Refill(window, 0, window.Ny, state.Generator, o);
                    sw.Stop();
                    Emit("E43.window_refill_ms", sw.Elapsed.TotalMilliseconds);
                    float max = 0f;
                    for (int i = 0; i < window.Count; i++) if (window.CloudWater[i] > max) max = window.CloudWater[i];
                    Emit("E43.window_refill_cloud_max", max);
                }
            }

            foreach (string variant in new[] { "steer-main", "steer-maps", "steer-off", "noshear" })
            {
                BeginVariant(variant);
                double worstAccel = 0.0;
                var lastRate = new Dictionary<EveLayer, double>();
                var lastTime = new Dictionary<EveLayer, double>();
                // the steering needs minutes to turn a layer to its target at the rate limit: the first variant waits until every steered layer has converged
                double start = Time.realtimeSinceStartup;
                double limit = variant == "steer-main" ? 330.0 : 15.0;
                while (Time.realtimeSinceStartup - start < limit)
                {
                    int started = 0, converged = 0;
                    foreach (EveLayer h in module.CurrentLayers)
                    {
                        LayerSteerState st = module.SteeringOf(h);
                        if (st == null || !st.Started) continue;
                        started++;
                        if (st.Converged) converged++;
                        double prevRate, prevTime;
                        if (lastRate.TryGetValue(h, out prevRate) && lastTime.TryGetValue(h, out prevTime) && st.LastRealTime > prevTime)
                            worstAccel = Math.Max(worstAccel, Math.Abs(st.RateRadS - prevRate) / (st.LastRealTime - prevTime));
                        lastRate[h] = st.RateRadS;
                        lastTime[h] = st.LastRealTime;
                    }
                    if (variant == "steer-main" && started >= 2 && converged == started && Time.realtimeSinceStartup - start > 5.0) break;
                    yield return ProbeWait.Frames(1);
                }
                // the drift is read back from EVE's own member (CloudsPQS.mainPeriod), not from the steering's state: the pattern's speed plus that drift is the layer's motion
                var steered = new List<EveLayer>();
                foreach (EveLayer h in module.CurrentLayers)
                {
                    LayerSteerState st = module.SteeringOf(h);
                    if (st != null && st.Started) steered.Add(h);
                }
                steered.Sort((a, b) => module.SteeringOf(a).HeightM.CompareTo(module.SteeringOf(b).HeightM));
                Emit("E43.layers_steered." + variant, steered.Count);
                Emit("E43.rate_change_max_rad_s2." + variant, worstAccel);
                if (steered.Count >= 2)
                {
                    double radius = body != null ? body.Radius : 600000.0;
                    for (int k = 0; k < 2; k++)
                    {
                        EveLayer h = k == 0 ? steered[0] : steered[steered.Count - 1];
                        LayerSteerState st = module.SteeringOf(h);
                        string tag = variant + "." + (k == 0 ? "low" : "high");
                        double rev;
                        bool read = EveLayerAccess.TryGetDriftRev(h, out rev);
                        double rate = read ? rev * 2.0 * Math.PI / Tuning.Get("e43.rotationSign", 1f) : st.RateRadS;
                        double total = st.PatternMs + rate * radius * Math.Max(0.2, Math.Cos(st.LatDeg * Math.PI / 180.0));
                        Emit("E43.layer.drift_read_from_eve." + tag, read);
                        Emit("E43.layer.height_m." + tag, st.HeightM);
                        Emit("E43.layer.field_wind_east_ms." + tag, st.WindEastMs);
                        Emit("E43.layer.total_drift_ms." + tag, total);
                        Emit("E43.layer.angle_error_deg." + tag, LayerWindSteering.AngleErrorDeg(total, st.WindEastMs, st.WindNorthMs));
                        Emit("E43.layer.speed_error_pct." + tag, 100.0 * Math.Abs(total - st.WindEastMs) / Math.Max(1.0, Math.Abs(st.WindEastMs)));
                        Emit("E43.layer.upward_applied." + tag, st.UpApplied);
                        Emit("E43.layer.updraft_ms." + tag, st.UpdraftMs);
                    }
                }
                EndVariant();
            }
        }
    }
}
