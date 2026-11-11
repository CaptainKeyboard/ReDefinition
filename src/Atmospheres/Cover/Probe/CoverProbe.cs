using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The probe of package D34 inside the integrated series K-W3b (start "cover"): the surface cover against the running game, as the
    // rows D34.* of tests/series/rows/D34.json. A vessel with wheels (a rover) on the ground in flight is needed; the series' Medium
    // profile and the main mod present. The probe measures:
    //   - the pieces: both shaders loaded, the game-code assumptions hold, the cover PQS mod is in the active body, the map is published
    //     and how long the resample took on the worker;
    //   - the ground: a forced snow system over the vessel (the decorator ProbeCoverField, because the generator's maps ignore forced
    //     discs) leaves a patch in the CoverMap and on the vessel's quad, and the patch fades with a test time constant;
    //   - the seasons: the share of the north and the south that carries cover at four times a quarter of a year apart, from the real
    //     field, and the orbit mask of each (OrbitMask.Fill on the same frame: cover on against off, mean alpha over the polar bands);
    //   - the vessel: with the rise and melt times shortened by the tuning override, the shells exist, the amount rises to white in the
    //     forced fall and is bare again when the scripted temperature goes above freezing;
    //   - the tracks: the rover drives a circle; marks, the width (pressed area over the distance driven), the strips and rays of the
    //     draw, the fade with a shortened tau, the in-memory save and load, the tile memory;
    //   - the orbit overlay: the host exists, calls the registered layers, and the cover layer is among them;
    //   - the switches: groundCover, vehicleCover and tracks flipped through all their values leave no exception in the log from the
    //     cover code;
    //   - the cost: the mean frame time with ground cover on against off, and the PQS vertex spacing near the vessel.
    // NOT MEASURED here (rows with required false and a note in the rows file): the RT terrain refit cost after a storm-scale repaint
    // (U10), the shell against Deferred, TUFX, FSR, DLSS, FG (the player and the interaction matrix), docking.
    [AtmospheresModule(984)]
    internal sealed class CoverProbe : SeriesProbe
    {
        internal const string Series = "K-W3b";
        internal const string Start = "cover";

        protected override string SeriesName
        {
            get { return Series; }
        }

        protected override double MaxSeconds
        {
            get { return 17 * 60; }
        }

        private readonly List<string> cheapErrors = new List<string>();

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error) return;
            string where = (stackTrace ?? "") + (condition ?? "");
            if (where.Contains("ReDefinition.Atmospheres") && (where.Contains("Cover") || where.Contains("Track") || where.Contains("Snow") || where.Contains("Orbit")))
                cheapErrors.Add((condition ?? "").Length > 120 ? condition.Substring(0, 120) : condition);
        }

        private static bool InFlight()
        {
            return HighLogic.LoadedSceneIsFlight && GameGlobals.ActiveVessel != null && GameGlobals.ActiveVessel.loaded && GameGlobals.ActiveVessel.mainBody != null;
        }

        private static double Share(CoverMap.Frame frame, double latFrom, double latTo, double threshold01)
        {
            int total = 0, covered = 0;
            for (double lat = latFrom; lat <= latTo; lat += 2.0)
                for (double lon = -180; lon < 180; lon += 4.0)
                {
                    total++;
                    if (frame.Sample01(lat, lon) > threshold01) covered++;
                }
            return total == 0 ? 0 : covered / (double)total;
        }

        private static double MeanAlpha(byte[] rgba, int w, int h, double latFrom, double latTo)
        {
            double sum = 0;
            int n = 0;
            for (int row = 0; row < h; row++)
            {
                double lat = OrbitMask.LatitudeDegrees((row + 0.5) / h);
                if (lat < latFrom || lat > latTo) continue;
                for (int c = 0; c < w; c++)
                {
                    sum += rgba[(row * w + c) * 4 + 3] / 255.0;
                    n++;
                }
            }
            return n == 0 ? 0 : sum / n;
        }

        // The mean cover of the built quad under a place (NaN where no quad of the cover holds it).
        private static double QuadMeanAt(double lat, double lon)
        {
            var quads = new List<PQ>();
            CoverQuads.Snapshot(quads);
            foreach (PQ quad in quads)
            {
                QuadCover cover;
                if (!CoverQuads.TryGet(quad, out cover)) continue;
                if (lat >= cover.LatMin && lat <= cover.LatMax && lon >= cover.LonMin && lon <= cover.LonMax) return cover.LastMean;
            }
            return double.NaN;
        }

        // The bearing of the vessel's ground velocity in radians clockwise from north (its heading where it stands still).
        private static double BearingOf(Vessel v)
        {
            Vector3d velocity = v.srf_velocity;
            if (velocity.sqrMagnitude < 0.01) velocity = v.transform.forward;
            return Math.Atan2(Vector3d.Dot(velocity, v.east), Vector3d.Dot(velocity, v.north));
        }

        // Whether the ray-traced scene of the main mod leaves the cover shell's shader out: asked of RtFilter by reflection (the
        // atmospheres assembly does not reference the main mod). False where the type or the method is not found.
        private static bool RtLeavesOutShell()
        {
            try
            {
                foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = a.GetType("ReDefinition.Raytracing.RtFilter", false);
                    if (t == null) continue;
                    System.Reflection.MethodInfo m = t.GetMethod("LeftOut", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    return m != null && (bool)m.Invoke(null, new object[] { VehicleCoverGate.ShellShaderName });
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        // The spacing of the vertices of the built quad nearest to the vessel, in metres (a vertex to its neighbour in the row).
        private static double SpacingNear(Vessel v)
        {
            var quads = new List<PQ>();
            CoverQuads.Snapshot(quads);
            double best = double.MaxValue, spacing = double.NaN;
            foreach (PQ quad in quads)
            {
                if (quad == null || !quad.isBuilt || quad.verts == null || quad.verts.Length < 3 || quad.quadTransform == null) continue;
                Vector3 centre = quad.quadTransform.TransformPoint(quad.verts[quad.verts.Length / 2]);
                double d = (centre - v.transform.position).sqrMagnitude;
                if (d >= best) continue;
                best = d;
                spacing = (quad.quadTransform.TransformPoint(quad.verts[1]) - quad.quadTransform.TransformPoint(quad.verts[0])).magnitude;
            }
            return spacing;
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            yield return ProbeWait.Until(() => InFlight(), 330, "the flight");
            if (!InFlight())
            {
                Fail("D34: no flight to measure in");
                yield break;
            }
            Application.logMessageReceived += OnLog;
            Vessel vessel = GameGlobals.ActiveVessel;
            CelestialBody body = vessel.mainBody;
            int index = body.flightGlobalsIndex;
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            SetSetting(AtmospheresSettings.Keys.GroundCover, 1.0);
            SetSetting(AtmospheresSettings.Keys.CoverStrength, 1.0);
            SetSetting(AtmospheresSettings.Keys.Tracks, 1.0);
            SetSetting(AtmospheresSettings.Keys.OrbitOverlay, 1.0);
            SetSetting(AtmospheresSettings.Keys.VehicleCover, (double)(int)VehicleCoverMode.Landed);
            CoverBodyState state = CoverBodies.Of(body);
            Emit("D34.body_has_cover", state != null);
            if (state == null)
            {
                Fail("D34: the active body carries no cover layer");
                yield break;
            }

            // ---- the pieces ---------------------------------------------------------------------------------------------------------
            string error;
            Emit("D34.shader_loaded", AtmosphereShaders.Get(VehicleCover.ShaderName, out error) != null);
            Emit("D34.ribbon_shader_loaded", AtmosphereShaders.Get(TrackDraw.ShaderName, out error) != null);
            Emit("D34.assumptions_hold", AtmospheresAssumptions.Holds(AssumptionsD34.WheelId) && AtmospheresAssumptions.Holds(AssumptionsD34.QuadId));
            SnowRefresher refresher = UnityEngine.Object.FindObjectOfType<SnowRefresher>();
            yield return ProbeWait.Until(() => refresher != null && refresher.InstalledCount > 0, 60, "the cover PQS mod");
            Emit("D34.pqs_mod_installed", refresher != null && refresher.InstalledCount > 0);
            yield return ProbeWait.Until(() => state.Map.Current.Number > 0, 120, "the first cover map frame");
            Emit("D34.map_published", state.Map.Current.Number > 0);
            Emit("D34.map_resample_ms", CoverResampler.LastMilliseconds);
            yield return ProbeWait.Seconds(8);
            Emit("D34.pqs_vertex_spacing_m", SpacingNear(vessel));
            Emit("D34.quads_known", CoverQuads.Count);
            Emit("D34.tile_bytes_per_tile", TrackTiles.TexelsPerSide * TrackTiles.TexelsPerSide);

            // ---- the overlay host ----------------------------------------------------------------------------------------------------
            int roundsBefore = OrbitOverlayHost.Rounds;
            yield return ProbeWait.Seconds(3);
            bool hasLayer = false;
            foreach (IOrbitOverlayLayer layer in OrbitOverlayLayers.All) if (layer is OrbitCover) hasLayer = true;
            Emit("D34.orbit_overlay_hook", hasLayer && OrbitOverlayHost.Rounds > roundsBefore);
            Emit("D34.orbit_overlay_layers", OrbitOverlayLayers.All.Length);
            Emit("D34.orbit_overlay_last_drawn", OrbitOverlayHost.LastDrawn);

            // ---- the seasons from the real field, with the orbit mask of each ----------------------------------------------------------
            var seasonFrames = new CoverMap.Frame[4];
            var alphaNorth = new double[4];
            var alphaSouth = new double[4];
            double ut0;
            CoverClock.TryUt(out ut0);
            double year = Math.Max(1.0, state.Climate.YearSeconds);
            var probeMap = new WeatherMap();
            var seasonMap = new CoverMap();
            for (int k = 0; k < 4; k++)
            {
                double ut = ut0 + k * year / 4.0;
                int tries = 0;
                bool got = false;
                while (tries++ < 40 && !got)
                {
                    got = WeatherHub.Field.GetMap(index, WeatherMapKind.SnowCover, ut, probeMap) && Math.Abs(probeMap.Ut - ut) < 3600.0;
                    if (!got) yield return ProbeWait.Seconds(1);
                }
                Emit("D34.season." + k + ".map_ready", got);
                if (!got) continue;
                seasonFrames[k] = seasonMap.Resample(probeMap.Values, probeMap.Width, probeMap.Height, state.Def.MaxDepthM, probeMap.Version, ut);
                seasonMap.Publish(seasonFrames[k]);
                double north = Share(seasonFrames[k], 50, 90, 0.02), south = Share(seasonFrames[k], -90, -50, 0.02), equator = Share(seasonFrames[k], -20, 20, 0.02);
                Emit("D34.season." + k + ".north_share", north);
                Emit("D34.season." + k + ".south_share", south);
                Emit("D34.season." + k + ".equator_share", equator);
                // The orbit mask of the same frame, cover on (A) against cover off (B = all bare): the mean alpha over the polar bands is the
                // difference region's mean change.
                const int w = 256, h = 128;
                float[] lat, lon;
                OrbitMask.BuildCoordinates(w, h, state.Frame, out lat, out lon);
                var rgba = new byte[w * h * 4];
                OrbitMask.Fill(rgba, w, h, OrbitMask.BuildNoise(w, h), null, OrbitMask.HeightWidth, OrbitMask.HeightHeight, lat, lon, seasonFrames[k], state.Def, 1.0, 0.0, false);
                alphaNorth[k] = MeanAlpha(rgba, w, h, 50, 90);
                alphaSouth[k] = MeanAlpha(rgba, w, h, -90, -50);
                Emit("D34.season." + k + ".orbit_mean_alpha_north", alphaNorth[k]);
                Emit("D34.season." + k + ".orbit_mean_alpha_south", alphaSouth[k]);
            }
            if (seasonFrames[0] != null && seasonFrames[2] != null)
            {
                double a = Share(seasonFrames[0], 50, 90, 0.02) - Share(seasonFrames[2], 50, 90, 0.02);
                double b = Share(seasonFrames[0], -90, -50, 0.02) - Share(seasonFrames[2], -90, -50, 0.02);
                Emit("D34.season_swing_north", a);
                Emit("D34.season_swing_south", b);
                Emit("D34.seasons_differ", Math.Abs(a) + Math.Abs(b) > 0.02);
                // Seasons 0 and 2 are half a year apart: the hemispheres swing in opposite directions, each by a real amount, and the orbit
                // mask follows the cover (the polar band's alpha is larger in the season with the larger share, in each hemisphere).
                Emit("D34.season_swing_north_abs", Math.Abs(a));
                Emit("D34.season_swing_south_abs", Math.Abs(b));
                Emit("D34.season_hemispheres_opposed", a * b < 0.0);
                Emit("D34.orbit_alpha_follows_share_north", (alphaNorth[0] - alphaNorth[2]) * a > 0.0);
                Emit("D34.orbit_alpha_follows_share_south", (alphaSouth[0] - alphaSouth[2]) * b > 0.0);
            }

            // ---- a forced system over the vessel: a patch that fades ----------------------------------------------------------------------
            IWeatherField previous = WeatherHub.Field;
            var field = new ProbeCoverField(previous) { RadiusM = body.Radius };
            WeatherHub.Field = field;
            double lat0 = vessel.latitude, lon0 = vessel.longitude;
            double now;
            CoverClock.TryUt(out now);
            const double FadeTau = 60.0;
            field.Add(new ProbeCoverField.Disc { Body = index, LatDeg = lat0, LonDeg = lon0, RadiusKm = 12, RateMmPerH = 6f, FromUt = now - 2 * 3600.0, ToUt = now - 10.0, FadeTauS = FadeTau });
            ForceTuning("vehicle.riseSeconds", 20f);
            ForceTuning("vehicle.meltSeconds", 40f);
            ForceTuning("tracks.tauScale", 0.006f);
            ForceTuning(CoverRepainter.BudgetKey, 4f);
            long frameBefore = state.Map.Current.Number;
            yield return ProbeWait.Until(() => state.Map.Current.Number > frameBefore && state.Map.Current.Sample01(lat0, lon0) > 0.05, 120, "the forced patch in the cover map");
            double peakMap = state.Map.Current.Sample01(lat0, lon0);
            Emit("D34.patch_map_peak", peakMap);
            yield return ProbeWait.Seconds(6);
            double peakQuad = QuadMeanAt(lat0, lon0);
            Emit("D34.patch_ground_peak", double.IsNaN(peakQuad) ? -1.0 : peakQuad);
            Emit("D34.patch_quad_found", !double.IsNaN(peakQuad));
            Emit("D34.patch_in_depth_range", peakMap > 0.05 && peakMap <= 1.0);
            // The vehicle: the forced fall ended 10 s ago and the air is cold, so the cover stays and the vessel gathers it.
            VehicleCover vehicles = VehicleCover.Current;
            yield return ProbeWait.Until(() => vehicles != null && vehicles.AmountOf(vessel) >= 0.3f, 90, "the vehicle cover to rise");
            Emit("D34.vehicle_tracked", vehicles != null && vehicles.VesselCount > 0);
            Emit("D34.vehicle_amount_peak", vehicles != null ? vehicles.AmountOf(vessel) : -1.0);
            yield return ProbeWait.Seconds(6);
            Emit("D34.vehicle_shells", vehicles != null ? vehicles.ShellCount : -1);
            Emit("D34.vehicle_parts_built", vehicles != null ? vehicles.PartsBuilt : -1);
            Emit("D34.vehicle_unreadable_meshes", vehicles != null ? vehicles.Unreadable : -1);
            Emit("D34.vehicle_build_ms", vehicles != null ? vehicles.LastBuildMs : -1.0);
            Emit("D34.vehicle_foreign_bare", vehicles != null ? vehicles.ForeignBare : -1);
            Emit("D34.rt_shell_left_out", RtLeavesOutShell());
            // The method switch while the vessel still carries cover: ScreenSpace removes the shells and the pass draws for the vessel;
            // back to Shell the shells return.
            SetSetting(AtmospheresSettings.Keys.VehicleCoverMethod, (double)(int)VehicleCoverMethod.ScreenSpace);
            yield return ProbeWait.Seconds(4);
            Emit("D34.vehicle_screen_attached", vehicles != null && vehicles.ScreenAttached);
            Emit("D34.vehicle_screen_drawn", vehicles != null ? vehicles.ScreenDrawn : -1);
            Emit("D34.vehicle_shells_in_screen_method", vehicles != null ? vehicles.ShellCount : -1);
            SetSetting(AtmospheresSettings.Keys.VehicleCoverMethod, (double)(int)VehicleCoverMethod.Shell);
            yield return ProbeWait.Seconds(6);
            Emit("D34.vehicle_screen_detached", vehicles != null && !vehicles.ScreenAttached);
            Emit("D34.vehicle_shells_returned", vehicles != null ? vehicles.ShellCount : -1);

            // ---- the tracks: a circle on the cover ------------------------------------------------------------------------------------------
            TrackModule tracks = TrackModule.Current;
            Vector3d start = vessel.GetWorldPos3D();
            int marksBefore = tracks != null ? tracks.Marks : 0;
            FlightInputHandler.state.wheelThrottle = 0.35f;
            FlightInputHandler.state.wheelSteer = 0.5f;
            // The width of the tracks at three places of the circle: after 14, 27 and 40 seconds the runs of pressed texels across the
            // rover's heading at its place (its wheels have just marked there).
            var widths = new List<double>();
            for (int place = 1; place <= 3; place++)
            {
                yield return ProbeWait.Seconds(place == 1 ? 14 : 13);
                double utWidth;
                CoverClock.TryUt(out utWidth);
                double bearing = BearingOf(vessel);
                double meanRun = 0;
                int runs = 0;
                if (state.HasTracks) runs = state.Tracks.RunsAcross(vessel.latitude, vessel.longitude, bearing, 4.0, utWidth, out meanRun);
                Emit("D34.track_runs_place" + place, runs);
                Emit("D34.track_width_place" + place + "_m", meanRun);
                if (runs >= 2) widths.Add(meanRun);
            }
            if (widths.Count >= 2)
            {
                double lo = double.MaxValue, hi = 0, sum = 0;
                foreach (double wdt in widths) { lo = Math.Min(lo, wdt); hi = Math.Max(hi, wdt); sum += wdt; }
                Emit("D34.track_width_spread", (hi - lo) / (sum / widths.Count));
                // The trail seen from 30 m: its width in pixels on the flight camera's picture (the pixel is 2 d tan(fov / 2) / height).
                Camera flightCam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
                if (flightCam != null)
                {
                    double pixelAt30 = 2.0 * 30.0 * Math.Tan(flightCam.fieldOfView * 0.5 * Math.PI / 180.0) / Math.Max(1, flightCam.pixelHeight);
                    Emit("D34.track_width_px_at_30m", sum / widths.Count / pixelAt30);
                }
            }
            FlightInputHandler.state.wheelThrottle = 0f;
            FlightInputHandler.state.wheelSteer = 0f;
            double travelled = (vessel.GetWorldPos3D() - start).magnitude;
            Emit("D34.track_marks", tracks != null ? tracks.Marks - marksBefore : -1);
            Emit("D34.track_tiles_live", tracks != null ? tracks.LiveTiles : -1);
            Emit("D34.track_tile_total_bytes", (long)CoverBodies.TrackMemory.TotalLive * TrackTiles.TexelsPerSide * TrackTiles.TexelsPerSide);
            yield return ProbeWait.Seconds(3);
            if (tracks != null && state.HasTracks)
            {
                long pressed = 0;
                foreach (TrackTile tile in state.Tracks.Live) foreach (byte b in tile.Pressed) if (b != 0) pressed++;
                double length = Math.Max(1.0, tracks.Marks == 0 ? 1.0 : Math.Min(travelled * 4.0, 400.0));
                Emit("D34.track_pressed_texels", pressed);
                // Pressed area over the distance driven by the wheels of the rover (four wheels, each its own line): the mean width of
                // one wheel's line in metres.
                Emit("D34.track_mean_width_m", pressed * TrackTiles.TexelMetres * TrackTiles.TexelMetres / Math.Max(1.0, travelled * 4.0));
                Emit("D34.track_strips", tracks.Draw.StripCount);
                Emit("D34.track_draw_calls", tracks.Draw.Draws);
                Emit("D34.track_ray_misses", tracks.Draw.RayMisses);
                Emit("D34.track_rays", tracks.Draw.RaysCast);
                Emit("D34.track_build_ms", tracks.Draw.LastBuildMs);
                Emit("D34.track_stamp_ms", tracks.LastStampMs);
                // The fade: a mark made now shows 1 and, two test taus (about 11 s) later, e^-2.
                double lat1 = vessel.latitude, lon1 = vessel.longitude, ut1;
                CoverClock.TryUt(out ut1);
                state.Tracks.Stamp(lat1 + 0.0003, lon1, 0.25, ut1, state.Def.TracksTauSeconds * 0.006, 1.0);
                double shown0 = state.Tracks.Shown(lat1 + 0.0003, lon1, ut1);
                double tau = state.Def.TracksTauSeconds * 0.006;
                double shown2 = state.Tracks.Shown(lat1 + 0.0003, lon1, ut1 + 2 * tau);
                Emit("D34.track_fade_ratio", shown0 > 0 ? shown2 / shown0 : -1.0);
                // The save and load through the file format, in memory.
                byte[] file = state.Tracks.SaveAll(ut1);
                var copy = new TrackTiles(body.Radius);
                int kept = copy.LoadAll(file, ut1 + 1.0);
                Emit("D34.track_save_bytes", file.Length);
                Emit("D34.track_load_equal", kept == state.Tracks.LiveCount + state.Tracks.ParkedCount
                                                && Math.Abs(copy.Shown(lat1 + 0.0003, lon1, ut1 + tau) - state.Tracks.Shown(lat1 + 0.0003, lon1, ut1 + tau)) < 1e-6);
                // A revert: a tile stamped after the new time is dropped.
                int dropped = copy.DropAfter(ut1 - 1.0);
                Emit("D34.track_revert_drops_future", dropped > 0 && copy.Shown(lat1 + 0.0003, lon1, ut1 - 1.0) == 0.0);
            }

            // ---- the patch fades, the vessel goes bare ----------------------------------------------------------------------------------------
            field.Warm = true;
            yield return ProbeWait.Seconds(6 * (float)FadeTau * 0.5f);
            double lateQuad = QuadMeanAt(lat0, lon0);
            Emit("D34.patch_ground_faded", double.IsNaN(lateQuad) ? -1.0 : lateQuad);
            Emit("D34.patch_fade_ratio", !double.IsNaN(peakQuad) && peakQuad > 0.01 && !double.IsNaN(lateQuad) ? lateQuad / peakQuad : -1.0);
            yield return ProbeWait.Until(() => vehicles != null && vehicles.AmountOf(vessel) <= 0.05f, 150, "the vehicle cover to melt");
            Emit("D34.vehicle_amount_after", vehicles != null ? vehicles.AmountOf(vessel) : -1.0);

            // ---- switches: every value of the three settings, no exception from the cover code -------------------------------------------------
            int errorsBefore = cheapErrors.Count;
            foreach (VehicleCoverMode mode in new[] { VehicleCoverMode.Off, VehicleCoverMode.Always, VehicleCoverMode.Landed, VehicleCoverMode.Off, VehicleCoverMode.Landed })
            {
                SetSetting(AtmospheresSettings.Keys.VehicleCover, (double)(int)mode);
                yield return ProbeWait.Seconds(2);
            }
            foreach (VehicleCoverMethod method in new[] { VehicleCoverMethod.ScreenSpace, VehicleCoverMethod.Shell, VehicleCoverMethod.ScreenSpace, VehicleCoverMethod.Shell })
            {
                SetSetting(AtmospheresSettings.Keys.VehicleCoverMethod, (double)(int)method);
                yield return ProbeWait.Seconds(2);
            }
            foreach (double on in new[] { 0.0, 1.0, 0.0, 1.0 })
            {
                SetSetting(AtmospheresSettings.Keys.Tracks, on);
                SetSetting(AtmospheresSettings.Keys.GroundCover, on);
                yield return ProbeWait.Seconds(3);
            }
            SetSetting(AtmospheresSettings.Keys.GroundCover, 1.0);
            SetSetting(AtmospheresSettings.Keys.Tracks, 1.0);
            yield return ProbeWait.Seconds(4);
            Emit("D34.switch_cycle_exceptions", cheapErrors.Count - errorsBefore);
            Emit("D34.log_exceptions_total", cheapErrors.Count);
            if (cheapErrors.Count > 0) EmitText("D34.log_exceptions_first", cheapErrors[0]);

            // ---- the cost: frame time with the ground cover on against off ----------------------------------------------------------------
            double on1 = 0, off1 = 0;
            for (int round = 0; round < 2; round++)
            {
                SetSetting(AtmospheresSettings.Keys.GroundCover, round == 0 ? 0.0 : 1.0);
                yield return ProbeWait.Seconds(4);
                double sum = 0;
                int frames = 0;
                float until = Time.realtimeSinceStartup + 12f;
                while (Time.realtimeSinceStartup < until)
                {
                    sum += Time.unscaledDeltaTime;
                    frames++;
                    yield return ProbeWait.Frames(1);
                }
                if (round == 0) off1 = frames > 0 ? sum / frames * 1000.0 : 0;
                else on1 = frames > 0 ? sum / frames * 1000.0 : 0;
            }
            Emit("D34.frame_ms_cover_off", off1);
            Emit("D34.frame_ms_cover_on", on1);
            Emit("D34.ground_frame_ms_delta", on1 - off1);

            // ---- put everything back -----------------------------------------------------------------------------------------------------
            WeatherHub.Field = previous;
            ClearForced();
            Application.logMessageReceived -= OnLog;
        }
    }
}
