using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // A weather field of constant weather for the probe: the same sample everywhere and at every time, with the temperature and the
    // wind it is told. The probe installs it as WeatherHub.Field for the steps that need weather (ForcedField forces the level and the
    // type but takes the temperature from the field behind it, and the simulation that would give one is not there yet).
    internal sealed class ConstantWeatherField : IWeatherField
    {
        internal float Level01;
        internal PrecipType Type = PrecipType.None;
        internal float TemperatureK = 280f;
        internal Vector3 WindWorld = Vector3.zero;

        public bool Sample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample)
        {
            sample = WeatherSample.Clear(bodyIndex);
            sample.Level01 = Level01;
            sample.Type = Level01 > 0f ? Type : PrecipType.None;
            sample.SnowFraction = sample.Type == PrecipType.Snow ? 1f : 0f;
            sample.SleetFraction = sample.Type == PrecipType.Sleet ? 1f : 0f;
            sample.RainFraction = sample.Type == PrecipType.Rain ? 1f : 0f;
            sample.VisibilityM = Level01 > 0f ? Mathf.Lerp(8000f, 80f, Level01) : WeatherSample.ClearVisibilityM;
            sample.WindWorld = WindWorld;
            sample.GustFactor = Level01;
            sample.TemperatureK = TemperatureK;
            return true;
        }

        public bool GetMap(int bodyIndex, WeatherMapKind kind, double ut, WeatherMap target)
        {
            return false;
        }

        public bool FillWindow(int bodyIndex, double centreLatDeg, double centreLonDeg, double radiusKm, double ut, WeatherWindow target)
        {
            return false;
        }

        public void Advance(double budgetMs)
        {
        }

        public long Version { get { return 1; } }
        public long DefinitionId { get { return 37; } }
        public bool ThreadSafe { get { return true; } }
        public double StepSeconds { get { return 1.0; } }
        public FieldReadiness Readiness { get { return FieldReadiness.Ready; } }
    }

    // The probe of package D37 inside the integrated series K-W3b (start "frost"): the camera frost against the running game. It
    // measures, as rows D37.*:
    //   - the shader and the atlas (loaded, built in how many ms);
    //   - the order of the cameras in the flight view and in the IVA, against RECON.md (Camera 00, then the presenter at depth 0.1, then
    //     the marker and UI cameras), and where the internal camera stands (the IVA order is not in RECON.md: it is recorded here);
    //   - the state machine live, with the build and clear times shortened through the tuning override: exposure builds in a forced
    //     snowfall at 245 K and clears when it stops;
    //   - the zero-cost rule: frames drawn while the glass is clear;
    //   - the IVA: warm cabin keeps the window clear, a hard cold frosts it, the draw mode (host or quad) and whether the interior mask
    //     is valid;
    //   - the premises of the IVA path (review R1): whether the internal camera draws into the picture the upscaler gets, which camera
    //     the host hook is called for and how often, that the hook's image has the camera's aspect, which way of taking the internal
    //     camera's depth gave a plausible mask (and the coverage of every way, pinned one after the other);
    //   - the A/B of the final upscaled image (a tap behind the frost's own handler) with the frost drawn and not drawn, with a control
    //     pair, for the host composite and the forced quad: visibility, props clear inside the mask, orientation of the frost (R12);
    //   - the fallback policy live: with the host composite forced out, no frost where an upscaler runs, the quad where none does (R3);
    //   - the ceilings of the exposure with and without wind (R7);
    //   - the map view: nothing is drawn.
    // The series must run this start with the Medium profile, a vessel with crew on the ground, the main mod present, and read the rows
    // D37.* of tests/series/rows/D37.json (required rows decide); the three upscalers and frame generation are the player's rows.
    // It needs a flight on the body, started by the series' other probe or by hand (-atmoKeep); it waits for it. The EVA view needs a
    // kerbal outside and is not driven here: the rows say so [VERIFY: EVA first person, ThroughTheEyes and FreeIva are for the owner's
    // run].
    [AtmospheresModule(986)]
    internal sealed class CameraFrostProbe : SeriesProbe
    {
        internal const string Series = "K-W3b";

        protected override string SeriesName
        {
            get { return Series; }
        }

        protected override double MaxSeconds
        {
            get { return 12 * 60; }
        }

        // Describes the enabled cameras by depth order: "name(depth)" joined by " < ".
        private static string Chain(out List<KeyValuePair<float, string>> ordered)
        {
            ordered = new List<KeyValuePair<float, string>>();
            foreach (Camera camera in Camera.allCameras)
                if (camera != null && camera.enabled) ordered.Add(new KeyValuePair<float, string>(camera.depth, camera.name));
            ordered.Sort((a, b) => a.Key.CompareTo(b.Key));
            var sb = new StringBuilder();
            foreach (KeyValuePair<float, string> entry in ordered)
            {
                if (sb.Length > 0) sb.Append(" < ");
                sb.Append(entry.Value).Append('(').Append(entry.Key.ToString("0.##", CultureInfo.InvariantCulture)).Append(')');
            }
            return sb.ToString();
        }

        private static float DepthOf(List<KeyValuePair<float, string>> chain, string name)
        {
            foreach (KeyValuePair<float, string> entry in chain)
                if (entry.Value == name) return entry.Key;
            return float.NaN;
        }

        // Whether the host image has the aspect of the flight camera's pixels (the composite is drawn at the image's own aspect).
        private static bool HostAspectMatches()
        {
            if (CameraFrostRenderer.HostImageH <= 0 || CameraFrostRenderer.HostCameraPixelH <= 0) return false;
            float image = CameraFrostRenderer.HostImageW / (float)CameraFrostRenderer.HostImageH;
            float camera = CameraFrostRenderer.HostCameraPixelW / (float)CameraFrostRenderer.HostCameraPixelH;
            return Math.Abs(image - camera) < 0.02f;
        }

        // Every step-th pixel of a FrostProbeTap luminance image, row 0 at the bottom.
        private static float[] Downsample(float[] image, int step)
        {
            int w = FrostProbeTap.Width / step, h = FrostProbeTap.Height / step;
            var small = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    small[y * w + x] = image[y * step * FrostProbeTap.Width + x * step];
            return small;
        }

        // The flight chain as the repository measured it (tests/data/recon/dumps/run2-cameras-flight.json, the game run of the recon
        // series; RECON.md P6 names the depth of Camera 00 only): the scaled-space camera before Camera 00, Camera 00 before the presenter,
        // the presenter before the marker and UI cameras. With the rig running (rigRuns) the presenter must be there: without it the
        // check would pass on a chain of Camera 00 alone.
        internal static bool ChainMatchesRecon(List<KeyValuePair<float, string>> chain, bool rigRuns)
        {
            float scaled = DepthOf(chain, "Camera ScaledSpace"), main = DepthOf(chain, "Camera 00"), presenter = DepthOf(chain, "ReDefinitionPresenter");
            float marker = DepthOf(chain, "markerCam"), ui = DepthOf(chain, "UIMainCamera");
            if (float.IsNaN(main)) return false;
            if (rigRuns && float.IsNaN(presenter)) return false;
            if (!float.IsNaN(scaled) && !(scaled < main)) return false;
            if (!float.IsNaN(presenter) && !(main < presenter)) return false;
            if (!float.IsNaN(presenter) && !float.IsNaN(marker) && !(presenter < marker)) return false;
            if (!float.IsNaN(ui) && !(main < ui)) return false;
            return true;
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            yield return ProbeWait.Until(() => HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready && FlightGlobals.ActiveVessel != null,
                330, "the flight");
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                Fail("D37: no flight to measure in");
                yield break;
            }
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            SetSetting(AtmospheresSettings.Keys.CameraFrost, 1.0);
            yield return ProbeWait.Until(() => CameraFrostRenderer.Current != null && CameraFrostRenderer.Current.PassReady, 60, "the frost pass");
            CameraFrostRenderer module = CameraFrostRenderer.Current;
            Emit("D37.shader_loaded", module != null && module.PassReady);
            if (module == null || !module.PassReady) yield break;
            Emit("D37.atlas_build_ms", CameraFrostRenderer.AtlasBuildMs);

            // The camera chain in the flight view.
            List<KeyValuePair<float, string>> flightChain;
            EmitText("D37.camera_chain_flight", Chain(out flightChain));
            Emit("D37.camera_chain_order_matches", ChainMatchesRecon(flightChain, Services.PostUpscale.HostAvailable));
            Emit("D37.rig_runs", Services.PostUpscale.HostAvailable);
            Emit("D37.upscaler_runs", Services.PostUpscale.HostAvailable && WeatherHooks.Caps.Upscaler != UpscalerKind.None);

            // A field of constant weather, the pane shortened to seconds.
            IWeatherField previous = WeatherHub.Field;
            var field = new ConstantWeatherField { Level01 = 0f, Type = PrecipType.Snow, TemperatureK = 245f, WindWorld = new Vector3(8f, 0f, 0f) };
            WeatherHub.Field = field;
            ForceTuning("frost.buildS", 4f);
            ForceTuning("frost.clearS", 4f);
            ForceTuning("frost.ivaLagS", 2f);
            ForceTuning("frost.evaLagS", 1f);
            ForceTuning("frost.windRefMs", 10f);
            int originalFrame = Time.frameCount;
            var tap = new FrostProbeTap();
            try
            {
                module.ResetState();

                // The ceiling of the exposure (review R7) with the shipped wind reference and cold: a level-1 snowfall at -18 C settles at
                // 0.6875 without wind and reaches 1 from about 11 m/s of airflow on. The build time is shortened as above.
                ForceTuning("frost.windRefMs", 20f);
                field.Level01 = 1f;
                field.TemperatureK = 255f;
                field.WindWorld = Vector3.zero;
                yield return ProbeWait.Seconds(14);
                Emit("D37.exposure_ceiling_calm", CameraFrostRenderer.LastExposure);
                module.ResetState();
                field.WindWorld = new Vector3(20f, 0f, 0f);
                yield return ProbeWait.Seconds(14);
                Emit("D37.exposure_ceiling_20ms", CameraFrostRenderer.LastExposure);
                field.Level01 = 0f;
                field.TemperatureK = 245f;
                field.WindWorld = new Vector3(8f, 0f, 0f);
                ForceTuning("frost.windRefMs", 10f);
                module.ResetState();

                // Snow at 245 K builds the exposure; no weather clears it.
                field.Level01 = 0.9f;
                yield return ProbeWait.Until(() => CameraFrostRenderer.LastExposure > 0.5f, 40, "the exposure to build");
                Emit("D37.exposure_builds", CameraFrostRenderer.LastExposure > 0.5f);
                Emit("D37.exposure_peak", CameraFrostRenderer.LastExposure);
                Emit("D37.growth_at_peak", CameraFrostRenderer.LastGrowth);
                field.Level01 = 0f;
                yield return ProbeWait.Until(() => CameraFrostRenderer.LastExposure < 0.02f, 40, "the exposure to clear");
                Emit("D37.exposure_clears", CameraFrostRenderer.LastExposure < 0.02f);

                // The IVA: the camera chain there, the warm cabin, the hard cold, the draw mode and the interior mask.
                bool iva = false;
                try
                {
                    iva = CameraManager.Instance != null && CameraManager.Instance.SetCameraIVA();
                }
                catch (Exception e)
                {
                    LogLine("D37: the IVA could not be entered (" + e.GetType().Name + " " + e.Message + ")");
                }
                Emit("D37.iva_entered", iva);
                if (iva)
                {
                    yield return ProbeWait.Frames(60);
                    List<KeyValuePair<float, string>> ivaChain;
                    EmitText("D37.camera_chain_iva", Chain(out ivaChain));
                    float internalDepth = DepthOf(ivaChain, "InternalCamera");
                    Emit("D37.iva_internal_camera_found", !float.IsNaN(internalDepth));
                    Emit("D37.iva_internal_depth", float.IsNaN(internalDepth) ? -1000.0 : internalDepth);
                    Emit("D37.iva_view_is_iva", CameraFrostRenderer.ActiveView == FrostView.Iva);

                    // A clear pane in a view that can draw records nothing: frames drawn while no weather and a clear glass.
                    module.ResetState();
                    field.Level01 = 0f;
                    yield return ProbeWait.Seconds(2);
                    int drawnBefore = CameraFrostRenderer.DrawnFrames;
                    yield return ProbeWait.Frames(90);
                    Emit("D37.inactive_drawn_frames", CameraFrostRenderer.DrawnFrames - drawnBefore);

                    module.ResetState();
                    field.Level01 = 0.9f;
                    yield return ProbeWait.Seconds(12);
                    Emit("D37.iva_warm_cabin_clear", CameraFrostRenderer.LastExposure < 0.02f);
                    Emit("D37.iva_warm_cabin_exposure", CameraFrostRenderer.LastExposure);

                    field.TemperatureK = 150f;
                    yield return ProbeWait.Until(() => CameraFrostRenderer.LastExposure > 0.3f, 40, "the pane to freeze in a hard cold");
                    Emit("D37.iva_hard_cold_frosts", CameraFrostRenderer.LastExposure > 0.3f);
                    yield return ProbeWait.Seconds(3);

                    // The premises of the IVA path (review R1): which camera the rig upscales and the hook is called for, whether the
                    // internal camera draws into the picture the upscaler gets, and which way of taking its depth gives a mask.
                    // The mask is given up to 25 s to settle (the judge reads it back about once a second and may try the next way).
                    for (int k = 0; k < 250 && CameraFrostRenderer.IvaMaskNeeded && !CameraFrostRenderer.IvaMaskValid && !CameraFrostRenderer.IvaMaskExhausted; k++)
                        yield return ProbeWait.Frames(6);
                    EmitText("D37.iva_draw_mode", CameraFrostRenderer.ActiveMode);
                    EmitText("D37.iva_mode_reason", CameraFrostRenderer.ModeReason ?? "none");
                    EmitText("D37.host_camera_iva", CameraFrostRenderer.LastHostCamera ?? "none");
                    Emit("D37.iva_internal_in_rig_image", CameraFrostRenderer.InternalInRigImage);
                    Emit("D37.iva_mask_needed", CameraFrostRenderer.IvaMaskNeeded);
                    Emit("D37.iva_hook_calls", CameraFrostRenderer.HostCallsIva);
                    Emit("D37.iva_hook_calls_flight_camera", CameraFrostRenderer.HostCallsIvaFlightCamera);
                    Emit("D37.iva_hook_calls_internal_camera", CameraFrostRenderer.HostCallsIvaInternalCamera);
                    Emit("D37.iva_hook_camera_known", CameraFrostRenderer.HostCallsIva > 0
                        && CameraFrostRenderer.HostCallsIvaFlightCamera + CameraFrostRenderer.HostCallsIvaInternalCamera == CameraFrostRenderer.HostCallsIva);
                    Emit("D37.iva_hook_calls_drawn", CameraFrostRenderer.HostCallsIvaDrawn);
                    Emit("D37.iva_host_image_w", CameraFrostRenderer.HostImageW);
                    Emit("D37.iva_host_image_h", CameraFrostRenderer.HostImageH);
                    Emit("D37.iva_camera_pixel_w", CameraFrostRenderer.HostCameraPixelW);
                    Emit("D37.iva_camera_pixel_h", CameraFrostRenderer.HostCameraPixelH);
                    Emit("D37.iva_host_image_aspect_ok", HostAspectMatches());
                    Emit("D37.iva_mask_valid", CameraFrostRenderer.IvaMaskValid);
                    Emit("D37.iva_mask_way", CameraFrostRenderer.IvaMaskWay);
                    float coverage = CameraFrostRenderer.IvaMaskCoverage;
                    Emit("D37.iva_mask_coverage", coverage);
                    // Required: the IVA has a usable mask, or does not need one (the internal camera draws past the upscaler).
                    Emit("D37.iva_mask_ok", !CameraFrostRenderer.IvaMaskNeeded
                        || (CameraFrostRenderer.IvaMaskValid && FrostMaskJudge.Plausible(coverage)));

                    // The A/B pictures of the final upscaled image with the frost drawn and not drawn at the same state: twice without as
                    // the control (what moves by itself), once with. Host composite first, then the quad forced.
                    byte[] atlasBytes = FrostAtlas.Build();
                    FrostTuning shape = FrostTuning.Defaults();
                    float exposureNow = CameraFrostRenderer.LastExposure;
                    float growthNow = module.CurrentGrowth;
                    float[] maskPixels = null;
                    int maskW = 0, maskH = 0;
                    if (CameraFrostRenderer.IvaMaskNeeded) maskPixels = module.ReadInteriorMask(out maskW, out maskH);
                    foreach (string phase in new[] { "host", "quad" })
                    {
                        ForceTuning("frost.forceQuad", phase == "quad" ? 1f : 0f);
                        ForceTuning("frost.quadWithUpscaler", phase == "quad" ? 1f : 0f);
                        yield return ProbeWait.Frames(20);
                        EmitText("D37.iva_ab_" + phase + "_mode", CameraFrostRenderer.ActiveMode);
                        var shots = new float[3][];
                        var shotPixels = new Color[3][];
                        float[] abOff = { 1f, 0f, 1f };
                        for (int s = 0; s < 3; s++)
                        {
                            ForceTuning("frost.abOff", abOff[s]);
                            yield return ProbeWait.Frames(8);
                            tap.Arm();
                            for (int k = 0; k < 90 && !tap.Captured; k++) yield return ProbeWait.Frames(1);
                            shots[s] = tap.ReadLuma();
                            shotPixels[s] = tap.LastPixels;
                        }
                        ForceTuning("frost.abOff", 0f);
                        if (shots[0] != null && shots[1] != null && shots[2] != null && shotPixels[1] != null && shots[0].Length == shots[2].Length)
                        {
                            // T4: the natural view with the frost and the heat map against the envelope of the two frost-off frames.
                            var envelope = new float[shots[0].Length];
                            for (int i = 0; i < envelope.Length; i++) envelope[i] = Math.Max(shots[0][i], shots[2][i]);
                            SeriesPictures.SaveSheet("D37-iva-" + phase, shotPixels[1], envelope, FrostProbeTap.Width, FrostProbeTap.Height);
                        }
                        if (shots[0] == null || shots[1] == null || shots[2] == null)
                        {
                            Fail("D37: the probe tap captured no picture in the " + phase + " A/B (the hook did not answer for the flight or internal camera)");
                            continue;
                        }
                        EmitText("D37.iva_ab_" + phase + "_camera", tap.CapturedCamera ?? "none");
                        Emit("D37.iva_ab_" + phase + "_control_changed", FrostAbMetrics.ChangedShare(shots[0], shots[2]));
                        Emit("D37.iva_ab_" + phase + "_changed", FrostAbMetrics.ChangedShare(shots[1], shots[0]));
                        if (phase == "host")
                        {
                            if (maskPixels != null)
                            {
                                float inside = FrostAbMetrics.ChangedShareIn(shots[1], shots[0], FrostProbeTap.Width, FrostProbeTap.Height, maskPixels, maskW, maskH, true);
                                Emit("D37.iva_ab_changed_inside_mask", inside);
                                Emit("D37.iva_ab_changed_outside_mask",
                                    FrostAbMetrics.ChangedShareIn(shots[1], shots[0], FrostProbeTap.Width, FrostProbeTap.Height, maskPixels, maskW, maskH, false));
                                Emit("D37.iva_ab_props_clear", inside <= 0.02f);
                            }
                            else
                            {
                                // The internal camera draws past the upscaler and covers the composite by itself: nothing to hold back.
                                Emit("D37.iva_ab_props_clear", true);
                            }

                            // The orientation of the frost (review R12): the change against the predicted opacity map, turned and not.
                            float aspect = CameraFrostRenderer.HostImageH > 0 ? CameraFrostRenderer.HostImageW / (float)CameraFrostRenderer.HostImageH : 16f / 9f;
                            const int gw = FrostProbeTap.Width / 4, gh = FrostProbeTap.Height / 4;
                            float[] predicted = FrostShape.AlphaMap(atlasBytes, growthNow, exposureNow, aspect, ref shape, gw, gh);
                            float normal, flipped;
                            FrostAbMetrics.Orientation(Downsample(shots[1], 4), Downsample(shots[0], 4), predicted, gw, gh, out normal, out flipped);
                            Emit("D37.iva_ab_orientation_normal", normal);
                            Emit("D37.iva_ab_orientation_flipped", flipped);
                            Emit("D37.iva_ab_orientation_ok", normal > flipped + 0.02f);
                        }
                    }
                    ForceTuning("frost.forceQuad", 0f);
                    ForceTuning("frost.quadWithUpscaler", 0f);

                    // The fallback policy live: a view the host composite cannot serve (forced) is drawn by the quad only where no upscaler
                    // runs on the picture, and is left without frost where one does.
                    ForceTuning("frost.forceQuad", 1f);
                    yield return ProbeWait.Frames(20);
                    bool upscalerRuns = Services.PostUpscale.HostAvailable && WeatherHooks.Caps.Upscaler != UpscalerKind.None;
                    int suppressedBefore = CameraFrostRenderer.SuppressedFrames, quadBefore = CameraFrostRenderer.QuadFrames;
                    yield return ProbeWait.Frames(30);
                    EmitText("D37.iva_policy_mode", CameraFrostRenderer.ActiveMode);
                    Emit("D37.iva_policy_upscaler_runs", upscalerRuns);
                    Emit("D37.iva_policy_ok", upscalerRuns
                        ? CameraFrostRenderer.ActiveMode == FrostDrawPolicy.Suppressed && CameraFrostRenderer.SuppressedFrames - suppressedBefore >= 20 && CameraFrostRenderer.QuadFrames == quadBefore
                        : CameraFrostRenderer.ActiveMode == FrostDrawPolicy.Quad && CameraFrostRenderer.QuadFrames - quadBefore >= 20);
                    ForceTuning("frost.forceQuad", 0f);

                    // Every way of taking the mask, one after the other, the coverage each gives: the record of which event of the internal
                    // camera holds a valid depth (review R1). Information; the required rows are iva_mask_ok and the A/B above.
                    if (CameraFrostRenderer.IvaMaskNeeded)
                    {
                        for (int way = 0; way < FrostMaskJudge.Ways.Length; way++)
                        {
                            ForceTuning("frost.ivaMaskWay", way);
                            yield return ProbeWait.Seconds(5);
                            Emit("D37.iva_mask_cov_way" + way, CameraFrostRenderer.IvaMaskCoverage);
                            EmitText("D37.iva_mask_event_way" + way, FrostMaskJudge.Ways[way].Event + (FrostMaskJudge.Ways[way].ForceDepth ? "+depth" : ""));
                        }
                        ForceTuning("frost.ivaMaskWay", -1f);
                        yield return ProbeWait.Seconds(1);
                    }

                    // Map view: nothing is drawn while it is on.
                    bool map = false;
                    try
                    {
                        MapView.EnterMapView();
                        map = true;
                    }
                    catch (Exception e)
                    {
                        LogLine("D37: the map view could not be entered (" + e.GetType().Name + " " + e.Message + ")");
                    }
                    Emit("D37.map_entered", map);
                    if (map)
                    {
                        yield return ProbeWait.Seconds(2);
                        int drawn = CameraFrostRenderer.DrawnFrames;
                        yield return ProbeWait.Frames(60);
                        Emit("D37.map_view_drawn_frames", CameraFrostRenderer.DrawnFrames - drawn);
                        Emit("D37.map_view_is_off", CameraFrostRenderer.ActiveView == FrostView.Off);
                        try
                        {
                            MapView.ExitMapView();
                        }
                        catch (Exception e)
                        {
                            LogLine("D37: the map view could not be left (" + e.GetType().Name + " " + e.Message + ")");
                        }
                        yield return ProbeWait.Seconds(1);
                    }
                    CameraManager.Instance.SetCameraFlight();
                    yield return ProbeWait.Seconds(1);
                }
            }
            finally
            {
                WeatherHub.Field = previous;
                tap.Dispose();
                ClearForced();
                module.ResetState();
                LogLine("D37: the probe ran " + (Time.frameCount - originalFrame) + " frames");
            }
        }
    }
}
