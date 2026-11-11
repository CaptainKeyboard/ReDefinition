using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The probe of package D35 inside the integrated series K-W3b (start "ice"): the sea ice against the running game, as the rows D35.*
    // of tests/series/rows/D35.json. The start flies a vessel that is splashed on a sea (the near-ground rows need it; without it they are
    // left out with a text row), with the main mod present; the series' Medium profile; Scatterer present for the full set. It measures:
    //   - the pieces: both shaders, the layer in the overlay host, the frame of the ground sheet, the coast map and how long it took;
    //   - the hash and the noises: the shader's values against the CPU's on 256 points (integer hashes exactly, floats within 1e-3);
    //   - the coverage: at four season phases (the field read a quarter year apart) the field's mean fraction over the sea of four
    //     windows against the share of ice in the CPU picture and in the shader's coverage pass: the rendered coverage equals the field's
    //     within 0.05;
    //   - the orbit picture: made on the worker, no ice on land, alpha under the ice, the shell built and calibrated;
    //   - the near ground (RECON P10 / P11 for the next decision): a forced sheet over the sea at camera heights 50 m, 500 m and 5 km in
    //     the aid view (flat magenta) against the view without the sheet: the share of magenta pixels in four boxes below the horizon, the
    //     control share with the sheet off, the natural view's change; and the cost of the sheet in frame time;
    //   - the switches: seaIce Off / Orbit / OrbitAndGround and ice.groundOverlay cycled with no exception from the ice code in the log.
    // NOT MEASURED here (no row is emitted, see the rows file): the ray-traced scene, the Deferred G-buffer variant, DLSS / FSR / XeSS /
    // frame-generation ghosting at the ice edge, the dither shimmer, the orbit-to-ground handover edge, Scatterer wave damping, physical ice.
    [AtmospheresModule(989)]
    internal sealed class IceProbe : SeriesProbe
    {
        internal const string Series = "K-W3b";
        internal const string Start = "ice";

        protected override string SeriesName
        {
            get { return Series; }
        }

        protected override double MaxSeconds
        {
            get { return 17 * 60; }
        }

        private readonly List<string> iceErrors = new List<string>();

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error) return;
            string where = (stackTrace ?? "") + (condition ?? "");
            if (where.Contains("ReDefinition.Atmospheres") && System.Text.RegularExpressions.Regex.IsMatch(where, @"\bIce[A-Z]"))
                iceErrors.Add((condition ?? "").Length > 120 ? condition.Substring(0, 120) : condition);
        }

        // Puts back what the scenario forced and stops listening to the log.
        private void Restore()
        {
            ClearForced();
            Application.logMessageReceived -= OnLog;
        }

        private static bool InFlight()
        {
            return HighLogic.LoadedSceneIsFlight && GameGlobals.ActiveVessel != null && GameGlobals.ActiveVessel.loaded && GameGlobals.ActiveVessel.mainBody != null;
        }

        private static string Num(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static void PlaceCamera(float distance, float pitchDegrees, float headingDegrees)
        {
            FlightCamera camera = FlightCamera.fetch;
            if (camera == null) return;
            camera.maxDistance = Mathf.Max(camera.maxDistance, distance + 10f);
            camera.minDistance = Mathf.Min(camera.minDistance, distance);
            camera.SetDistanceImmediate(distance);
            camera.camPitch = pitchDegrees * Mathf.Deg2Rad;
            camera.camHdg = headingDegrees * Mathf.Deg2Rad;
        }

        private static bool IsMagenta(Color c)
        {
            return c.r > 0.85f && c.b > 0.85f && c.g < 0.35f;
        }

        // The share of magenta pixels and the mean luma in a box given in fractions of the frame (centre x, centre y, width, height).
        private static void Box(ReconFrame frame, double cx, double cy, double w, double h, out double magenta, out double luma)
        {
            int x0 = Math.Max(0, (int)((cx - w / 2) * frame.Width)), x1 = Math.Min(frame.Width, (int)((cx + w / 2) * frame.Width));
            int y0 = Math.Max(0, (int)((cy - h / 2) * frame.Height)), y1 = Math.Min(frame.Height, (int)((cy + h / 2) * frame.Height));
            int n = 0, m = 0;
            double sum = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    Color c = frame.Pixels[y * frame.Width + x];
                    n++;
                    if (IsMagenta(c)) m++;
                    sum += ReconMath.Luma(c.r, c.g, c.b);
                }
            magenta = n > 0 ? m / (double)n : 0.0;
            luma = n > 0 ? sum / n : 0.0;
        }

        private static readonly double[][] Boxes =
        {
            new[] { 0.15, 0.25 }, new[] { 0.85, 0.25 }, new[] { 0.15, 0.45 }, new[] { 0.85, 0.45 }
        };

        // Reads the frame at the end of this frame (the screen, with the interface hidden by the caller).
        private IEnumerator<ProbeWait> Capture(ReconHost host, Action<ReconFrame> done)
        {
            host.Read();
            double end = Time.realtimeSinceStartup + 10.0;
            while (host.Reading && Time.realtimeSinceStartup < end) yield return ProbeWait.Frames(1);
            done(host.Reading ? null : host.Frame);
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            yield return ProbeWait.Until(() => InFlight(), 330, "the flight");
            if (!InFlight())
            {
                Fail("D35: no flight to measure in");
                yield break;
            }
            Application.logMessageReceived += OnLog;
            Vessel vessel = GameGlobals.ActiveVessel;
            CelestialBody body = vessel.mainBody;
            int index = body.flightGlobalsIndex;
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            SetSetting(AtmospheresSettings.Keys.OrbitOverlay, 1.0);
            SetSetting(AtmospheresSettings.Keys.SeaIce, (double)(int)SeaIceMode.OrbitAndGround);
            ForceTuning(IceHeights.BudgetKey, 12f);
            ForceTuning(IceTuning.GroundOverlayKey, 0f);
            yield return ProbeWait.Frames(3);

            // ---- the pieces ---------------------------------------------------------------------------------------------------------
            IceBodyState state = IceBodies.Of(body);
            Emit("D35.body_has_ice", state != null);
            if (state == null)
            {
                Fail("D35: the active body has no sea to freeze (no ocean or no ICE rules)");
                Restore();
                yield break;
            }
            string error;
            Shader overlayShader = AtmosphereShaders.Get(IceGroundOverlay.ShaderName, out error);
            Emit("D35.overlay_shader_loaded", overlayShader != null);
            Emit("D35.shell_shader_loaded", AtmosphereShaders.Get(IceOrbitLayer.ShaderName, out error) != null);
            bool layerRegistered = false;
            foreach (IOrbitOverlayLayer layer in OrbitOverlayLayers.All) if (layer is IceOrbitLayer) layerRegistered = true;
            Emit("D35.layer_registered", layerRegistered);
            IceModule module = IceModule.Current;
            Emit("D35.module_running", module != null);
            if (module == null || overlayShader == null)
            {
                Fail("D35: the module or the overlay shader is missing");
                Restore();
                yield break;
            }

            // The ground sheet's frame: the direction of a place from the world, turned into the sphere's frame by the body's rotation,
            // against the frame of latitude and longitude.
            {
                Vector3d centre = body.position;
                Vector3d world = body.GetWorldSurfacePosition(vessel.latitude, vessel.longitude, 0.0);
                Vector3 dirWorld = (Vector3)(world - centre).normalized;
                Quaternion toSphere = Quaternion.Inverse(body.bodyTransform != null ? body.bodyTransform.rotation : body.transform.rotation);
                Vector3 dir = toSphere * dirWorld;
                double x, y, z;
                state.Frame.Direction(vessel.latitude, vessel.longitude, out x, out y, out z);
                Emit("D35.ground_frame_dot", dir.x * x + dir.y * y + dir.z * z);
            }

            // The coast map: the heights are read within the start (a raised budget), then the worker makes the map.
            double heightsStart = Time.realtimeSinceStartup;
            IceModule.Request(body);
            yield return ProbeWait.Until(() => { IceModule.Request(body); return state.Shore != null || state.ShoreError != null; }, 600, "the coast map");
            Emit("D35.coast_map_ready", state.Shore != null);
            if (state.Shore == null)
            {
                Fail("D35: no coast map (" + (state.ShoreError ?? "heights not complete") + ")");
                Restore();
                yield break;
            }
            Emit("D35.heights_seconds", Time.realtimeSinceStartup - heightsStart);
            Emit("D35.coast_map_ms", state.ShoreMilliseconds);
            long seaTexels = 0;
            for (int i = 0; i < ShoreGrid.Width * ShoreGrid.Height; i++) if (state.Shore[i * 4 + 2] > 127) seaTexels++;
            Emit("D35.coast_sea_fraction", seaTexels / (double)(ShoreGrid.Width * ShoreGrid.Height));
            yield return ProbeWait.Until(() => { IceModule.Request(body); return state.MapsValid; }, 120, "the field's ice maps");
            Emit("D35.field_maps_ready", state.MapsValid);
            if (!state.MapsValid)
            {
                Fail("D35: the field gave no ice maps");
                Restore();
                yield break;
            }

            // ---- the shader against the CPU: the hash and the noises ------------------------------------------------------------------------
            var material = new Material(overlayShader) { name = "ReDefinition Atmospheres sea ice probe" };
            int mismatches;
            double worstWorley, worstNoise;
            bool ran = false;
            try
            {
                ran = IceGpu.CompareNoise(material, 256, 12345u, out mismatches, out worstWorley, out worstNoise);
            }
            catch (Exception e)
            {
                mismatches = -1;
                worstWorley = double.NaN;
                worstNoise = double.NaN;
                EmitText("D35.hash_error", e.GetType().Name + " " + e.Message);
            }
            Emit("D35.hash_pass_ran", ran);
            Emit("D35.hash_mismatches", mismatches);
            Emit("D35.worley_max_error", worstWorley);
            Emit("D35.noise_max_error", worstNoise);

            // ---- the coverage at four season phases ------------------------------------------------------------------------------------------
            double ut0;
            CoverClock.TryUt(out ut0);
            double year = Math.Max(1.0, state.Climate.YearSeconds);
            var iceMap = new Texture2D(256, 128, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Bilinear, wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp };
            var shoreTex = new Texture2D(ShoreGrid.Width, ShoreGrid.Height, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Bilinear, wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp };
            shoreTex.LoadRawTextureData(state.Shore);
            shoreTex.Apply(false, false);
            string[] windowNames = { "north", "south", "tropics", "all" };
            double[][] windows =
            {
                new[] { 50.0, 85.0 }, new[] { -85.0, -50.0 }, new[] { -30.0, 30.0 }, new[] { -85.0, 85.0 }
            };
            var meanNorth = new double[4];
            var meanSouth = new double[4];
            var phaseReady = new bool[4];
            double worstError = 0.0;
            for (int k = 0; k < 4; k++)
            {
                double ut = ut0 + k * year / 4.0;
                var fraction = new WeatherMap();
                var thickness = new WeatherMap();
                var snow = new WeatherMap();
                bool got = false;
                for (int tries = 0; tries < 40 && !got; tries++)
                {
                    got = WeatherHub.Field.GetMap(index, WeatherMapKind.IceFraction, ut, fraction) && Math.Abs(fraction.Ut - ut) < 3600.0
                          && WeatherHub.Field.GetMap(index, WeatherMapKind.IceThickness, ut, thickness) && Math.Abs(thickness.Ut - ut) < 3600.0;
                    if (!got) yield return ProbeWait.Seconds(1);
                }
                Emit("D35.phase." + k + ".map_ready", got);
                if (!got) continue;
                if (!WeatherHub.Field.GetMap(index, WeatherMapKind.SnowCover, ut, snow)) snow.Width = 0;
                var inputs = new IceInputs();
                state.FillInputs(inputs);
                inputs.ForcedFraction = -1f;
                inputs.Fraction.CopyFrom(fraction);
                inputs.ThicknessM.CopyFrom(thickness);
                inputs.SnowM.CopyFrom(snow.Width > 0 ? snow : null);
                IceGroundOverlay.FillIceMapTexture(iceMap, inputs.Fraction, inputs.ThicknessM, inputs.SnowM);
                ForceTuning(IceTuning.ForceFractionKey, -1f);
                IceGroundOverlay.SetCommon(material, state, iceMap, shoreTex);
                for (int w = 0; w < windows.Length; w++)
                {
                    double iceCpu, fieldMean;
                    int seaSamples;
                    IceOrbitPicture.WindowCoverage(inputs, windows[w][0], windows[w][1], -180.0, 180.0, 0.5, out iceCpu, out fieldMean, out seaSamples);
                    double iceGpu = 0.0;
                    int seaPixels = 0;
                    bool gpuOk = true;
                    try
                    {
                        IceGpu.Coverage(material, windows[w][0], windows[w][1], -180.0, 180.0, 720, 140, out iceGpu, out seaPixels);
                    }
                    catch (Exception e)
                    {
                        gpuOk = false;
                        EmitText("D35.coverage_error", e.GetType().Name + " " + e.Message);
                    }
                    string prefix = "D35.phase." + k + "." + windowNames[w] + ".";
                    Emit(prefix + "sea_samples", seaSamples);
                    Emit(prefix + "field_fraction", fieldMean);
                    Emit(prefix + "rendered_cpu", iceCpu);
                    if (seaSamples >= 400)
                    {
                        Emit(prefix + "error_cpu", Math.Abs(iceCpu - fieldMean));
                        worstError = Math.Max(worstError, Math.Abs(iceCpu - fieldMean));
                    }
                    if (gpuOk)
                    {
                        Emit(prefix + "rendered_gpu", iceGpu);
                        if (seaSamples >= 400)
                        {
                            Emit(prefix + "error_gpu", Math.Abs(iceGpu - fieldMean));
                            worstError = Math.Max(worstError, Math.Abs(iceGpu - fieldMean));
                        }
                        Emit(prefix + "gpu_cpu_difference", Math.Abs(iceGpu - iceCpu));
                    }
                    if (w == 0) meanNorth[k] = fieldMean;
                    if (w == 1) meanSouth[k] = fieldMean;
                }
                phaseReady[k] = true;
                yield return ProbeWait.Frames(1);
            }
            Emit("D35.coverage_worst_error", worstError);
            bool allPhases = phaseReady[0] && phaseReady[1] && phaseReady[2] && phaseReady[3];
            Emit("D35.seasons_differ", allPhases && Math.Abs(meanNorth[0] - meanNorth[2]) + Math.Abs(meanSouth[1] - meanSouth[3]) > 0.02);

            // ---- the orbit picture ----------------------------------------------------------------------------------------------------------
            IceOrbitLayer orbit = module.Orbit;
            double utNow;
            CoverClock.TryUt(out utNow);
            yield return ProbeWait.Until(() =>
            {
                CoverClock.TryUt(out utNow);
                IceModule.Request(body);
                OrbitOverlayHost.Draw(body, true, utNow);
                return orbit.ComposedCount(body) > 0;
            }, 90, "the first orbit picture");
            Emit("D35.orbit_picture_composed", orbit.ComposedCount(body));
            IceOrbitLayer.BodyShell shell = orbit.ShellOf(body);
            if (shell != null)
            {
                Emit("D35.orbit_shell_calibrated", shell.Calibrated);
                yield return ProbeWait.Frames(2);
                OrbitOverlayHost.Draw(body, true, utNow);
                Emit("D35.orbit_shell_visible", orbit.ShellVisible(body));
                Emit("D35.orbit_worker_ms", shell.LastWorkerMilliseconds);
                Emit("D35.orbit_sea_texels", shell.LastStats.SeaTexels);
                Emit("D35.orbit_ice_texels", shell.LastStats.IceTexels);
            }
            {
                var inputs = new IceInputs();
                state.FillInputs(inputs);
                var rgba = new byte[IceOrbitPicture.Width * IceOrbitPicture.Height * 4];
                IceOrbitPicture.Fill(inputs, rgba, IceOrbitPicture.Width, IceOrbitPicture.Height);
                long landAlpha = 0, minAlpha = 255, iceTexels = 0;
                for (int row = 0; row < IceOrbitPicture.Height; row++)
                {
                    double lat = 90.0 - (row + 0.5) / IceOrbitPicture.Height * 180.0;
                    for (int col = 0; col < IceOrbitPicture.Width; col++)
                    {
                        double lon = (col + 0.5) / IceOrbitPicture.Width * 360.0 - 180.0;
                        int a = rgba[(row * IceOrbitPicture.Width + col) * 4 + 3];
                        bool sea;
                        IceOrbitPicture.StateAt(inputs, lat, lon, out sea);
                        if (!sea && a != 0) landAlpha++;
                        if (a > 0) { iceTexels++; minAlpha = Math.Min(minAlpha, a); }
                    }
                }
                Emit("D35.orbit_land_alpha_texels", landAlpha);
                Emit("D35.orbit_min_ice_alpha", iceTexels > 0 ? minAlpha / 255.0 : 1.0);
            }

            // ---- the switches --------------------------------------------------------------------------------------------------------------
            int errorsBefore = iceErrors.Count;
            SetSetting(AtmospheresSettings.Keys.SeaIce, (double)(int)SeaIceMode.Off);
            ForceTuning(IceTuning.GroundOverlayKey, 1f);
            yield return ProbeWait.Seconds(3);
            Emit("D35.switch_off_ground_inactive", !module.Ground.Active);
            Emit("D35.switch_off_orbit_hidden", !orbit.ShellVisible(body));
            SetSetting(AtmospheresSettings.Keys.SeaIce, (double)(int)SeaIceMode.Orbit);
            yield return ProbeWait.Seconds(3);
            Emit("D35.switch_orbit_ground_inactive", !module.Ground.Active);
            SetSetting(AtmospheresSettings.Keys.SeaIce, (double)(int)SeaIceMode.OrbitAndGround);
            ForceTuning(IceTuning.GroundOverlayKey, 0f);
            yield return ProbeWait.Seconds(3);
            Emit("D35.switch_overlay_flag_off_inactive", !module.Ground.Active);
            foreach (double on in new[] { 1.0, 0.0, 1.0, 0.0 })
            {
                ForceTuning(IceTuning.GroundOverlayKey, (float)on);
                yield return ProbeWait.Seconds(2);
            }
            Emit("D35.switch_cycle_exceptions", iceErrors.Count - errorsBefore);

            // ---- the near ground: P10 / P11 for the next decision ---------------------------------------------------------------------------
            if (!vessel.Splashed)
            {
                EmitText("D35.near_ground_note", "the vessel is not splashed on a sea: the near-ground rows are left out (the start needs a vessel on the water)");
            }
            else
            {
                ReconHost host = ReconHost.Get();
                host.UseScreen = true;
                try { GameEvents.onHideUI.Fire(); } catch (Exception) { }
                SetSetting(AtmospheresSettings.Keys.SeaIce, (double)(int)SeaIceMode.OrbitAndGround);
                string[] names = { "50m", "500m", "5km" };
                float[] distances = { 100f, 1000f, 10000f };
                for (int d = 0; d < distances.Length; d++)
                {
                    string prefix = "D35.near." + names[d] + ".";
                    PlaceCamera(distances[d], 30f, 90f);
                    // The baseline: no sheet.
                    ForceTuning(IceTuning.GroundOverlayKey, 0f);
                    ForceTuning(IceTuning.AidKey, 0f);
                    ForceTuning(IceTuning.ForceFractionKey, -1f);
                    ForceTuning(IceTuning.LeadOverrideKey, -1f);
                    yield return ProbeWait.Seconds(2);
                    ReconFrame baseline = null, aid = null, natural = null, control = null;
                    IEnumerator<ProbeWait> capture = Capture(host, f => baseline = f);
                    while (capture.MoveNext()) yield return capture.Current;
                    // The aid view: flat magenta on every sea pixel of the sheet (fraction 1, no leads).
                    ForceTuning(IceTuning.GroundOverlayKey, 1f);
                    ForceTuning(IceTuning.AidKey, 1f);
                    ForceTuning(IceTuning.ForceFractionKey, 1f);
                    ForceTuning(IceTuning.LeadOverrideKey, 0f);
                    yield return ProbeWait.Until(() => module.Ground.Active, 20, "the sheet");
                    Emit(prefix + "sheet_active", module.Ground.Active);
                    Emit(prefix + "camera_height_m", module.Ground.AltitudeM);
                    yield return ProbeWait.Seconds(2);
                    capture = Capture(host, f => aid = f);
                    while (capture.MoveNext()) yield return capture.Current;
                    // The natural view, forced to full ice without leads (the A/B against the baseline).
                    ForceTuning(IceTuning.AidKey, 0f);
                    yield return ProbeWait.Seconds(2);
                    capture = Capture(host, f => natural = f);
                    while (capture.MoveNext()) yield return capture.Current;
                    // The control: the sheet off again.
                    ForceTuning(IceTuning.GroundOverlayKey, 0f);
                    yield return ProbeWait.Seconds(2);
                    capture = Capture(host, f => control = f);
                    while (capture.MoveNext()) yield return capture.Current;
                    if (baseline == null || aid == null || natural == null || control == null)
                    {
                        EmitText(prefix + "error", "a frame could not be read");
                        continue;
                    }
                    double min = 1.0, sum = 0.0, controlMax = 0.0, lumaDiff = 0.0;
                    for (int b = 0; b < Boxes.Length; b++)
                    {
                        double m, l, mb, lb, mc, lc, mn, ln;
                        Box(aid, Boxes[b][0], Boxes[b][1], 0.1, 0.1, out m, out l);
                        Box(baseline, Boxes[b][0], Boxes[b][1], 0.1, 0.1, out mb, out lb);
                        Box(control, Boxes[b][0], Boxes[b][1], 0.1, 0.1, out mc, out lc);
                        Box(natural, Boxes[b][0], Boxes[b][1], 0.1, 0.1, out mn, out ln);
                        Emit(prefix + "aid_magenta_box" + b, m);
                        min = Math.Min(min, m);
                        sum += m;
                        controlMax = Math.Max(controlMax, Math.Max(mb, mc));
                        lumaDiff += Math.Abs(ln - lb) / Math.Max(1e-4, lb);
                    }
                    Emit(prefix + "aid_magenta_min", min);
                    Emit(prefix + "aid_magenta_mean", sum / Boxes.Length);
                    Emit(prefix + "control_magenta_max", controlMax);
                    Emit(prefix + "natural_luma_change", lumaDiff / Boxes.Length);
                    Emit(prefix + "frame_changed", aid.LitFraction() > 0.0 && Math.Abs(sum / Boxes.Length - controlMax) > 0.2);
                }

                // The cost of the sheet in frame time at 500 m: on against off.
                PlaceCamera(1000f, 30f, 90f);
                ForceTuning(IceTuning.AidKey, 0f);
                ForceTuning(IceTuning.ForceFractionKey, 0.6f);
                ForceTuning(IceTuning.LeadOverrideKey, -1f);
                double onMs = 0, offMs = 0;
                for (int round = 0; round < 2; round++)
                {
                    ForceTuning(IceTuning.GroundOverlayKey, round == 0 ? 0f : 1f);
                    yield return ProbeWait.Seconds(3);
                    double sum = 0;
                    int frames = 0;
                    float until = Time.realtimeSinceStartup + 10f;
                    while (Time.realtimeSinceStartup < until)
                    {
                        sum += Time.unscaledDeltaTime;
                        frames++;
                        yield return ProbeWait.Frames(1);
                    }
                    if (round == 0) offMs = frames > 0 ? sum / frames * 1000.0 : 0;
                    else onMs = frames > 0 ? sum / frames * 1000.0 : 0;
                }
                Emit("D35.frame_ms_overlay_off", offMs);
                Emit("D35.frame_ms_overlay_on", onMs);
                Emit("D35.ground_frame_ms_delta", onMs - offMs);
                Emit("D35.overlay_triangles", module.Ground.Triangles);
                Emit("D35.overlay_build_ms", module.Ground.LastBuildMs);
                try { GameEvents.onShowUI.Fire(); } catch (Exception) { }
                ReconHost.Release();
            }

            // ---- put everything back -------------------------------------------------------------------------------------------------------------
            ClearForced();
            UnityEngine.Object.Destroy(iceMap);
            UnityEngine.Object.Destroy(shoreTex);
            UnityEngine.Object.Destroy(material);
            Emit("D35.log_exceptions_total", iceErrors.Count);
            if (iceErrors.Count > 0) EmitText("D35.log_exceptions_first", iceErrors[0]);
            Application.logMessageReceived -= OnLog;
        }
    }
}
