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

        // The boxes the pose measures: up to four of Candidates that SeaBoxes found on open sea for the camera's pose, in that order. The
        // repeat run's fixed boxes at x 0.15 / 0.85 gave 0 in both left boxes and 1 in both right ones while the ray test said all four
        // were on the sea: a box can lie on land that stands above the ray (a coast, a spit) which the sea-sphere hit does not see.
        private static double[][] Boxes = new double[0][];

        // The first four are the earlier fixed boxes; the rest lets a pose with land on one side still find four sea boxes.
        private static readonly double[][] Candidates =
        {
            new[] { 0.15, 0.25 }, new[] { 0.85, 0.25 }, new[] { 0.15, 0.45 }, new[] { 0.85, 0.45 },
            new[] { 0.28, 0.25 }, new[] { 0.72, 0.25 }, new[] { 0.28, 0.45 }, new[] { 0.72, 0.45 },
            new[] { 0.15, 0.35 }, new[] { 0.85, 0.35 }, new[] { 0.28, 0.35 }, new[] { 0.72, 0.35 },
            new[] { 0.15, 0.15 }, new[] { 0.85, 0.15 }, new[] { 0.28, 0.15 }, new[] { 0.72, 0.15 }
        };

        // Whether the five rays of a box (centre and corners) reach the sea sphere at a point the coast map calls sea, and the terrain
        // under the ray stays below the ray all the way (the ray is marched in 64 steps, closer ones denser).
        private static bool SeaBox(IceBodyState state, CelestialBody body, Camera cam, double cx, double cy)
        {
            Quaternion toSphere = Quaternion.Inverse(body.bodyTransform != null ? body.bodyTransform.rotation : body.transform.rotation);
            double seaRadius = state.SeaRadiusM;
            Vector3d centre = body.position;
            PQS pqs = body.pqsController;
            bool terrain = pqs != null && pqs.isStarted;
            for (int k = 0; k < 5; k++)
            {
                double vx = cx + (k == 1 || k == 3 ? -0.05 : k == 2 || k == 4 ? 0.05 : 0.0);
                double vy = cy + (k == 1 || k == 2 ? -0.05 : k == 3 || k == 4 ? 0.05 : 0.0);
                Ray ray = cam.ViewportPointToRay(new Vector3((float)vx, (float)vy, 0f));
                Vector3d o = (Vector3d)ray.origin - centre;
                Vector3d d = ((Vector3d)ray.direction).normalized;
                double bq = Vector3d.Dot(o, d);
                double c = Vector3d.Dot(o, o) - seaRadius * seaRadius;
                double disc = bq * bq - c;
                if (disc < 0.0) return false;
                double t = -bq - Math.Sqrt(disc);
                if (t <= 0.0) return false;
                Vector3 dirWorld = (Vector3)(o + d * t).normalized;
                Vector3 dir = toSphere * dirWorld;
                if (!ShoreGrid.IsSeaAt(state.Shore, dir.x, dir.y, dir.z)) return false;
                if (!terrain) continue;
                for (int i = 1; i < 64; i++)
                {
                    double f = i / 64.0;
                    Vector3d p = o + d * (t * f * f);
                    double r = p.magnitude;
                    Vector3 local = toSphere * (Vector3)(p / r);
                    if (pqs.GetSurfaceHeight(new Vector3d(local.x, local.y, local.z)) > r) return false;
                }
            }
            return true;
        }

        // Chooses the boxes of the current pose from Candidates (at most four) and returns how many were found.
        private static int ChooseBoxes(IceBodyState state, CelestialBody body)
        {
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            var chosen = new List<double[]>();
            if (cam != null && state != null && state.Shore != null)
                for (int i = 0; i < Candidates.Length && chosen.Count < 4; i++)
                    if (SeaBox(state, body, cam, Candidates[i][0], Candidates[i][1])) chosen.Add(Candidates[i]);
            Boxes = chosen.ToArray();
            return Boxes.Length;
        }

        // Draws the frame of every chosen box into a copy of the pixels (green) for the picture sheets.
        private static Color[] Outlined(ReconFrame frame)
        {
            var copy = (Color[])frame.Pixels.Clone();
            for (int b = 0; b < Boxes.Length; b++)
            {
                int x0 = Math.Max(0, (int)((Boxes[b][0] - 0.05) * frame.Width)), x1 = Math.Min(frame.Width - 1, (int)((Boxes[b][0] + 0.05) * frame.Width));
                int y0 = Math.Max(0, (int)((Boxes[b][1] - 0.05) * frame.Height)), y1 = Math.Min(frame.Height - 1, (int)((Boxes[b][1] + 0.05) * frame.Height));
                for (int x = x0; x <= x1; x++) { copy[y0 * frame.Width + x] = Color.green; copy[y1 * frame.Width + x] = Color.green; }
                for (int y = y0; y <= y1; y++) { copy[y * frame.Width + x0] = Color.green; copy[y * frame.Width + x1] = Color.green; }
            }
            return copy;
        }

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
            double worstCoast = 0.0;
            double maxCoastField = 0.0;
            int coastN = 0;
            for (int k = 0; k < 4; k++)
            {
                double ut = ut0 + k * year / 4.0;
                var fraction = new WeatherMap();
                var thickness = new WeatherMap();
                var snow = new WeatherMap();
                bool got = false;
                // The module's own read (every 3 s, for the clock's time) is held: the generator renders its surface maps for the time
                // asked last, so the two askers pulled the maps back and forth and phases 1 and 3 never held at the moment of the read
                // (K-W3b: phase 0 and 2 ready, 1 and 3 not after 40 s). The probe asks again every 0.25 s, which keeps its time the last.
                ForceTuning(IceTuning.HoldMapsKey, 1f);
                float mapsUntil = Time.realtimeSinceStartup + 90f;
                while (!got && Time.realtimeSinceStartup < mapsUntil)
                {
                    got = WeatherHub.Field.GetMap(index, WeatherMapKind.IceFraction, ut, fraction) && Math.Abs(fraction.Ut - ut) < 3600.0
                          && WeatherHub.Field.GetMap(index, WeatherMapKind.IceThickness, ut, thickness) && Math.Abs(thickness.Ut - ut) < 3600.0;
                    if (!got) yield return ProbeWait.Seconds(0.25f);
                }
                Emit("D35.phase." + k + ".map_seconds_wait", 90f - (mapsUntil - Time.realtimeSinceStartup));
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
                // The coast zone: the same comparison where the shore term is high, to see the bias of the mean preservation near shores.
                {
                    double iceCoast, fieldCoast;
                    int coastSamples;
                    IceOrbitPicture.CoastCoverage(inputs, -85.0, 85.0, -180.0, 180.0, 0.25, IceOrbitPicture.CoastZoneShore, out iceCoast, out fieldCoast, out coastSamples);
                    string coastPrefix = "D35.phase." + k + ".coast.";
                    Emit(coastPrefix + "sea_samples", coastSamples);
                    Emit(coastPrefix + "field_fraction", fieldCoast);
                    Emit(coastPrefix + "rendered_cpu", iceCoast);
                    if (coastSamples >= 400)
                    {
                        coastN++;
                        maxCoastField = Math.Max(maxCoastField, fieldCoast);
                        worstCoast = Math.Max(worstCoast, Math.Abs(iceCoast - fieldCoast));
                    }
                }
                phaseReady[k] = true;
                yield return ProbeWait.Frames(1);
            }
            ForceTuning(IceTuning.HoldMapsKey, 0f);
            Emit("D35.coverage_worst_error", worstError);
            Emit("D35.coast.phases_measured", coastN);
            Emit("D35.coast.error_cpu", worstCoast);
            Emit("D35.coast.max_field_fraction", maxCoastField);
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
                // The shell retries its calibration every IceOrbitLayer.CalibrationRetryS while the host draws it (the first run built
                // it during the scene load and it never fitted): give it time to fit, then record how it went.
                for (int k = 0; k < 600 && !shell.Calibrated; k++)
                {
                    CoverClock.TryUt(out utNow);
                    OrbitOverlayHost.Draw(body, true, utNow);
                    yield return ProbeWait.Frames(2);
                }
                Emit("D35.orbit_shell_calibration_tries", shell.CalibrationTries);
                Emit("D35.orbit_shell_calibration_worst_deg", shell.CalibrationWorstDeg);
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
                    int boxesOnSea = ChooseBoxes(state, body);
                    // The pictures of the pose: natural (full ice, no leads) and the aid view, each beside the heat map of its difference
                    // to the view without the sheet, the chosen boxes outlined in green. Where the boxes of a pose fail, the picture says
                    // what the camera saw there.
                    float[] baselineLuma = SeriesPictures.Luma(baseline.Pixels);
                    if (aid.Width == baseline.Width && aid.Height == baseline.Height)
                    {
                        SeriesPictures.SaveSheet("D35-near-" + names[d] + "-natural", Outlined(natural), baselineLuma, natural.Width, natural.Height);
                        SeriesPictures.SaveSheet("D35-near-" + names[d] + "-aid", Outlined(aid), baselineLuma, aid.Width, aid.Height);
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
                    if (Boxes.Length == 0) min = 0.0;
                    Emit(prefix + "aid_magenta_min", min);
                    double boxCount = Math.Max(1, Boxes.Length);
                    Emit(prefix + "aid_magenta_mean", sum / boxCount);
                    Emit(prefix + "control_magenta_max", controlMax);
                    Emit(prefix + "natural_luma_change", lumaDiff / boxCount);
                    Emit(prefix + "frame_changed", aid.LitFraction() > 0.0 && Math.Abs(sum / boxCount - controlMax) > 0.2);
                    Emit(prefix + "boxes_on_sea", boxesOnSea);
                    // 1 when the sheet hides the water in every box of a clean, on-water measurement; 0 otherwise. At 5 km this number is the
                    // answer to the open RECON P10/P11 question; the rows around it fail when the measurement is void.
                    Emit(prefix + "hides_water", boxesOnSea == Boxes.Length && controlMax <= 0.01 && min >= 0.95);
                }

                // The cost of the sheet in frame time at 500 m: on against off.
                PlaceCamera(1000f, 30f, 90f);
                ForceTuning(IceTuning.AidKey, 0f);
                ForceTuning(IceTuning.ForceFractionKey, 0.6f);
                ForceTuning(IceTuning.LeadOverrideKey, -1f);
                // Four rounds at the same pose: the sea ice off, the DEFAULT path (setting seaIce Orbit: the orbit picture only, nothing near
                // the ground), the sea ice off again (the noise of two equal rounds), and the OPTIONAL ground sheet (seaIce
                // OrbitAndGround with the overlay on). The first run measured only the last against the first, so its 2.07 ms was the
                // optional sheet's cost, not the default's.
                var roundMs = new double[4];
                for (int round = 0; round < 4; round++)
                {
                    int mode = round == 1 ? (int)SeaIceMode.Orbit : round == 3 ? (int)SeaIceMode.OrbitAndGround : (int)SeaIceMode.Off;
                    SetSetting(AtmospheresSettings.Keys.SeaIce, (double)mode);
                    ForceTuning(IceTuning.GroundOverlayKey, round == 3 ? 1f : 0f);
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
                    roundMs[round] = frames > 0 ? sum / frames * 1000.0 : 0;
                }
                double offMs = 0.5 * (roundMs[0] + roundMs[2]);
                Emit("D35.frame_ms_off", offMs);
                Emit("D35.frame_ms_noise", Math.Abs(roundMs[0] - roundMs[2]));
                Emit("D35.frame_ms_default_path", roundMs[1]);
                Emit("D35.ground_frame_ms_default_delta", roundMs[1] - offMs);
                Emit("D35.frame_ms_overlay_on", roundMs[3]);
                Emit("D35.ground_frame_ms_delta", roundMs[3] - offMs);
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
