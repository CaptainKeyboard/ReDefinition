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

    // The probe of package D37 inside the integrated series K-W3b (start "lights"): the camera frost against the running game. It
    // measures, as rows D37.*:
    //   - the shader and the atlas (loaded, built in how many ms);
    //   - the order of the cameras in the flight view and in the IVA, against RECON.md (Camera 00, then the presenter at depth 0.1, then
    //     the marker and UI cameras), and where the internal camera stands (the IVA order is not in RECON.md: it is recorded here);
    //   - the state machine live, with the build and clear times shortened through the tuning override: exposure builds in a forced
    //     snowfall at 245 K and clears when it stops;
    //   - the zero-cost rule: frames drawn while the glass is clear;
    //   - the IVA: warm cabin keeps the window clear, a hard cold frosts it, the draw mode (host or quad) and whether the interior mask
    //     is valid;
    //   - the map view: nothing is drawn.
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

        // RECON.md's flight chain: the scaled-space camera before Camera 00, Camera 00 before the presenter (which exists while the rig
        // runs), the presenter before the marker and UI cameras.
        internal static bool ChainMatchesRecon(List<KeyValuePair<float, string>> chain)
        {
            float scaled = DepthOf(chain, "Camera ScaledSpace"), main = DepthOf(chain, "Camera 00"), presenter = DepthOf(chain, "ReDefinitionPresenter");
            float marker = DepthOf(chain, "markerCam"), ui = DepthOf(chain, "UIMainCamera");
            if (float.IsNaN(main)) return false;
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
            Emit("D37.camera_chain_order_matches", ChainMatchesRecon(flightChain));

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
            try
            {
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
                    EmitText("D37.iva_draw_mode", CameraFrostRenderer.ActiveMode);
                    Emit("D37.iva_mask_valid", CameraFrostRenderer.IvaMaskValid);
                    Emit("D37.iva_mask_coverage", module.ReadInteriorCoverage());
                    EmitText("D37.host_camera_iva", CameraFrostRenderer.LastHostCamera ?? "none");

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
                ClearForced();
                module.ResetState();
                LogLine("D37: the probe ran " + (Time.frameCount - originalFrame) + " frames");
            }
        }
    }
}
