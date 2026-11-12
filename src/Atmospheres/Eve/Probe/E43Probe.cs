using System;
using System.Collections.Generic;
using System.Diagnostics;
using AtmoSim.Core;
using ReDefinition.Atmospheres.Forecast;
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
            get { return new[] { "steer-main", "steer-maps", "steer-off", "noshear", "marker" }; }
        }

        protected override void ApplyVariant(string name)
        {
            if (name == "steer-main") LayerWindOptions.SteeringOverride = LayerSteeringMode.MainPeriod;
            else if (name == "steer-maps") LayerWindOptions.SteeringOverride = LayerSteeringMode.MapsOnly;
            else if (name == "steer-off") LayerWindOptions.SteeringOverride = LayerSteeringMode.Off;
            else if (name == "noshear") { LayerWindOptions.SteeringOverride = LayerSteeringMode.MainPeriod; LayerWindOptions.WindShearOverride = false; }
            else if (name == "marker") LayerWindOptions.SteeringOverride = LayerSteeringMode.MainPeriod;
        }

        protected override void RevertVariant(string name)
        {
            LayerWindOptions.SteeringOverride = null;
            LayerWindOptions.WindShearOverride = null;
            LayerWindOptions.MarkerRateRadS = null;
        }

        // The marker test (review R1): the probe may not read back the rate it set, so it pins every steered layer to a known rate of one sign and then the other, takes two
        // screens of the sky for each and measures how far the picture of the lowest layer moved (LayerMotionMeasure). The difference of the two measured shifts is twice the
        // shift of the known rate and the layer's own pattern motion cancels. Compared with the east direction on the screen (the layer's point under the view and the body's
        // surface velocity there projected through the camera) it gives the sense and the axis of EVE's rotation and its size, from rendered pixels only. Pictures: the first
        // screen of the positive rate (natural) and the same screen with the measured motion (red) and the east direction (green) drawn in (aid). [VERIFY] in the game: the
        // flight camera is pitched up to the sky; the measurement fails visibly (no layer in view) where the camera does not look at the layer.
        private IEnumerable<ProbeWait> MarkerScenario(EveCloudModule module, CelestialBody body)
        {
            const double Rate = 4.0e-5, IntervalS = 3.0;
            const int OutW = 192, MaxShift = 14;
            EveLayer layer = null;
            yield return ProbeWait.Until(() => { layer = LowestSteered(module); return layer != null; }, 40, "a steered layer for the marker test");
            Emit("E43.marker.layer_found", layer != null);
            if (layer == null || body == null) yield break;
            double height = LayerWindSteering.LayerMidHeightM(layer.InnerRadiusM, layer.OuterRadiusM, body.Radius);
            ProbeShots shots = ProbeShots.Ensure();
            try { GameEvents.onHideUI.Fire(); } catch (Exception) { }
            if (FlightCamera.fetch != null) FlightCamera.fetch.camPitch = -1.0f;
            yield return ProbeWait.Seconds(3.0);
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : Camera.main;
            Vector3 centre = body.position;
            Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.8f, 0f));
            double layerRadius = body.Radius + height;
            // the ray against the sphere of the layer (the far side of the near hit is the layer seen from below)
            Vector3 oc = ray.origin - centre;
            double bq = Vector3.Dot(oc, ray.direction), cq = Vector3.Dot(oc, oc) - layerRadius * layerRadius, disc = bq * bq - cq;
            bool hit = disc > 0.0 && (-bq + Math.Sqrt(disc)) > 0.0;
            Emit("E43.marker.camera_hits_layer", hit);
            if (!hit) { try { GameEvents.onShowUI.Fire(); } catch (Exception) { } yield break; }
            Vector3 pc = ray.origin + ray.direction * (float)(-bq + Math.Sqrt(disc));
            Vector3 east = ((Vector3)body.getRFrmVel(pc)).normalized;
            Vector3 s0 = cam.WorldToScreenPoint(pc), s1 = cam.WorldToScreenPoint(pc + east * 100f);
            double latPc = body.GetLatitude(pc) * Math.PI / 180.0;
            double expectedMetres = Rate * layerRadius * Math.Cos(latPc) * IntervalS;
            var shifts = new double[2][];
            byte[] firstPlus = null; int sw = 0, sh = 0;
            for (int s = 0; s < 2; s++)
            {
                LayerWindOptions.MarkerRateRadS = s == 0 ? Rate : -Rate;
                yield return ProbeWait.Seconds(3.0);
                byte[] a = null, b = null; int w = 0, h = 0;
                shots.Capture((rgb, cw, ch) => { a = rgb; w = cw; h = ch; });
                yield return ProbeWait.Until(() => shots.Idle, 10, "the first screen");
                yield return ProbeWait.Seconds(IntervalS);
                shots.Capture((rgb, cw, ch) => { b = rgb; });
                yield return ProbeWait.Until(() => shots.Idle, 10, "the second screen");
                if (a == null || b == null || w < 64) { Emit("E43.marker.screens_taken", false); yield break; }
                if (s == 0) { firstPlus = a; sw = w; sh = h; }
                int ow = OutW, oh = Math.Max(24, OutW * h / w);
                float[] la = LayerMotionMeasure.Luma(a, w, h, ow, oh), lb = LayerMotionMeasure.Luma(b, w, h, ow, oh);
                // the sky part of the screen only (the upper 40 %: the vessel and the ground are static and would pull the match to zero)
                int top = (int)(0.6 * oh), ch2 = oh - top;
                var ca = new float[ow * ch2]; var cb = new float[ow * ch2];
                Array.Copy(la, top * ow, ca, 0, ca.Length); Array.Copy(lb, top * ow, cb, 0, cb.Length);
                LayerMotionMeasure.Normalise(ca); LayerMotionMeasure.Normalise(cb);
                double dx, dy, conf;
                if (!LayerMotionMeasure.Estimate(ca, cb, ow, ch2, MaxShift, out dx, out dy, out conf)) { Emit("E43.marker.screens_taken", false); yield break; }
                shifts[s] = new[] { dx, dy, conf };
            }
            Emit("E43.marker.screens_taken", true);
            double scale = OutW / (double)sw;
            double cosine, metres;
            LayerMotionMeasure.Verdict(shifts[0][0], shifts[0][1], shifts[1][0], shifts[1][1], (s1.x - s0.x) / 100.0 * scale, (s1.y - s0.y) / 100.0 * scale, out cosine, out metres);
            Emit("E43.marker.match_confidence_min", Math.Min(shifts[0][2], shifts[1][2]));
            Emit("E43.marker.rotation_cosine_east", cosine);
            Emit("E43.rotation_sense_east", cosine > 0.9 && metres > 0.0);
            Emit("E43.marker.eastward_speed_ratio", expectedMetres > 0.0 ? metres / expectedMetres : 0.0);
            // the pictures: natural = the first screen, aid = the same with the measured motion (red) and the east direction (green) drawn from the middle of the sky part
            if (firstPlus != null)
            {
                shots.WriteScreen("E43-marker-natural.png", firstPlus, sw, sh);
                byte[] aid = (byte[])firstPlus.Clone();
                int mx = sw / 2, my = (int)(0.8 * sh);
                double inv = sw / (double)OutW;
                DrawLine(aid, sw, sh, mx, my, mx + (int)(8.0 * 0.5 * (shifts[0][0] - shifts[1][0]) * inv), my + (int)(8.0 * 0.5 * (shifts[0][1] - shifts[1][1]) * inv), 255, 40, 40);
                double el = Math.Sqrt((s1.x - s0.x) * (s1.x - s0.x) + (s1.y - s0.y) * (s1.y - s0.y));
                if (el > 1e-9) DrawLine(aid, sw, sh, mx, my, mx + (int)(120.0 * (s1.x - s0.x) / el), my + (int)(120.0 * (s1.y - s0.y) / el), 40, 220, 80);
                shots.WriteScreen("E43-marker-aid.png", aid, sw, sh);
            }
            try { GameEvents.onShowUI.Fire(); } catch (Exception) { }
        }

        private static EveLayer LowestSteered(EveCloudModule module)
        {
            EveLayer best = null; double bestHeight = double.MaxValue;
            foreach (EveLayer h in module.CurrentLayers)
            {
                LayerSteerState st = module.SteeringOf(h);
                if (st != null && st.Started && st.HeightM < bestHeight) { best = h; bestHeight = st.HeightM; }
            }
            return best;
        }

        // A line of three-byte pixels (row 0 at the bottom), a few pixels thick.
        private static void DrawLine(byte[] rgb, int w, int h, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
        {
            int n = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)), steps = Math.Max(1, n);
            for (int i = 0; i <= steps; i++)
            {
                int x = x0 + (x1 - x0) * i / steps, y = y0 + (y1 - y0) * i / steps;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        int p = 3 * (yy * w + xx);
                        rgb[p] = r; rgb[p + 1] = g; rgb[p + 2] = b;
                    }
            }
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
                var sw = Stopwatch.StartNew();
                bool filled = gf.FillWindow(body.flightGlobalsIndex, v.latitude, v.longitude, 40.0, Planetarium.GetUniversalTime(), window);
                sw.Stop();
                Emit("E43.window_filled", filled);
                if (filled)
                {
                    // FillWindow is the layered window where a switch is on; its time is the cost of the refill
                    Emit("E43.window_refill_ms", sw.Elapsed.TotalMilliseconds);
                    float max = 0f;
                    for (int i = 0; i < window.Count; i++) if (window.CloudWater[i] > max) max = window.CloudWater[i];
                    Emit("E43.window_refill_cloud_max", max);
                }
            }

            foreach (string variant in new[] { "steer-main", "steer-maps", "steer-off", "noshear", "marker" })
            {
                BeginVariant(variant);
                if (variant == "marker")
                {
                    foreach (ProbeWait wait in MarkerScenario(module, body)) yield return wait;
                    LayerWindOptions.MarkerRateRadS = null;
                    EndVariant();
                    continue;
                }
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
                // option B: the layers are painted from the layer maps of their heights while EVE's own drift stays stopped (no layer drifts by itself)
                int drifting = 0, bound = 0;
                foreach (EveLayer h in module.CurrentLayers)
                {
                    if (h.Tracker.State != BindState.Bound) continue;
                    bound++;
                    double rev0;
                    if (EveLayerAccess.TryGetDriftRev(h, out rev0) && Math.Abs(rev0) > 1.0e-12) drifting++;
                }
                Emit("E43.layer_sets_in_use." + variant, module.LayerSetsInUse);
                Emit("E43.layers_drifting_by_eve." + variant, drifting);
                Emit("E43.layers_bound." + variant, bound);
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
                        double latRad = st.LatDeg * Math.PI / 180.0;
                        double total = LayerWindSteering.LayerSpeedMs(st.PatternMs, rate, latRad, radius);
                        Emit("E43.layer.drift_read_from_eve." + tag, read);
                        Emit("E43.layer.height_m." + tag, st.HeightM);
                        Emit("E43.layer.field_wind_east_ms." + tag, st.WindEastMs);
                        Emit("E43.layer.total_drift_ms." + tag, total);
                        if (state != null && variant == "steer-main")
                        {
                            // one definition for the window and for EVE: the rate EVE holds (read back from its member) is the core's rate for this height, and the speed it gives
                            // at the viewer is the model's; the comparison with the field's wind at the viewer is reported, and judged only where the rigid rate is meant to hold
                            double shared = LayerWindSteering.SharedRate(state.Generator.D, state.Parameters, st.HeightM, LayerWindOptions.WindShear);
                            Emit("E43.layer.rate_error_vs_shared_rad_s." + tag, Math.Abs(rate - shared));
                            Emit("E43.layer.speed_error_vs_model_ms." + tag, Math.Abs(total - LayerWindSteering.LayerSpeedMs(st.PatternMs, shared, latRad, radius)));
                            double errPct = 100.0 * Math.Abs(total - st.WindEastMs) / Math.Max(1.0, Math.Abs(st.WindEastMs));
                            Emit("E43.layer.speed_error_vs_field_pct." + tag, errPct);
                            double fromJet = Math.Abs(Math.Abs(latRad) - state.Generator.D.JetLat0) * 180.0 / Math.PI;
                            bool inBand = fromJet <= 10.0 && HeightWind.LayerDecoupleWeight(state.Parameters, st.HeightM) >= 1.0;
                            Emit("E43.layer.in_reference_band." + tag, inBand);
                            if (inBand) Emit("E43.layer.speed_error_in_band_pct." + tag, errPct);
                            float offsetDeg;
                            if (EveLayerAccess.TryGetOffsetDeg(h, out offsetDeg)) Emit("E43.layer.offset_deg." + tag, offsetDeg);
                        }
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
