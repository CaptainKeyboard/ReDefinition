using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The probe of series K-T2 (package T09, the owner's order of 2026-11-12: the performance of TVC against the own cloud pass). It places the
    // vessel at rest in the air over the launch site at the height of each view, aims the flight camera, and dwells long enough for the GPU profiler
    // (-redefinitionGpuProfile, one report every 10 s) to log at least two clean reports inside the view. It writes a begin and an end line per view
    // (T09 view begin / end) and the module's counters; tools/weather_metrics/kt2.py reads the log (the runner calls it, "metrics" in K-T2.json).
    // The cost of the clouds in a view is the frame of the variant minus the frame of the variant "off" at the same view (label independent),
    // and by labels (the own pass puts "ReDefinition clouds: begin" and "...: end" around itself through the GpuMark seam), never a timer around
    // the probe.
    //
    // Variants of one start (-atmoVariants, in this order): off (no clouds from us, cloudSource None; the own starts run with EVE removed, so nothing
    // else draws clouds), tvc (cloudSource Eve: EVE's raymarched layers draw the maps of our field; its own start, it needs EVE and Scatterer),
    // plain (own pass, reconstruction off), recon4 (reconstruction in 4 x 4 blocks), entry (plain plus the entry refinement); then the A/B variants
    // that need one view only (the "inside" view): edge (the edge-adaptive mode, T12), heightoff (the four height switches off, T13: cost of the
    // height variation = plain minus heightoff), cap2 (the layer cap at 2), cache (the light cache on: its saving = plain minus cache, fill included), and gain (no dwell: the sun and moon gains reach the pass).
    // The views: ground (200 m above the pad, looking up), inside (inside the lowest cumulus layer), above (between the cumulus tops and the cirrus,
    // looking down), orbit_edge (300 km, looking at the limb), sky3 (200 m, higher pitch under all layers).
    // The weather is the field's at one universal time: from a fixed base (the next full hour of the clock at the start, one hour apart), the first
    // time whose cover over the site at 3 km is at least half AND whose sun stands 15 to 60 degrees over the site; the same for every variant and, as
    // the base is the same, for every start. TVC draws its own layer set (the pack's layers plus the painter's layer at 2700 m), the own stack its
    // four layers: the columns compare two systems at the same camera, not equal clouds.
    // Not run in the game yet [VERIFY]: placement of the vessel at 300 km (the hold works at the heights of K-W3b).
    [AtmospheresModule(985)]
    internal sealed class T09Probe : SeriesProbe
    {
        internal const string Series = "K-T2";
        private const double DwellS = 30.0, InsideDwellS = 25.0, CandidateStepS = 3600.0, BaseStepS = 3600.0;
        private const int Candidates = 48, PlaceAttempts = 4, SteadyFrames = 120;
        private const double MinCover = 0.5, SunMinDeg = 15.0, SunMaxDeg = 60.0, PlaceRetryWaitS = 4.0, AimToleranceDeg = 3.0;
        // vessel.altitude is the centre of mass; the position is set for the vessel's transform, and a tall rocket differs by several metres: 15 m at 200 m (7 percent),
        // still below any difference the cloud layers (hundreds of metres thick) can make.
        private const double AltitudeToleranceM = 15.0;

        internal struct View
        {
            internal string Name;
            // Metres above the terrain at the site, pitch in degrees above the horizon, heading in degrees, the camera's distance from the vessel.
            internal double AglM, PitchDeg, HeadingDeg, CameraDistM;
        }

        // The views of the series. The orbit view's pitch is the horizon's dip at that height, computed when it is placed. The two ground views are
        // 200 m up with the camera 100 m from the vessel, so that the camera (at most 100 x sin 38 = 62 m below it) stays above the terrain.
        internal static readonly View[] Views =
        {
            new View { Name = "ground", AglM = 200.0, PitchDeg = 18.0, HeadingDeg = 90.0, CameraDistM = 100.0 },
            new View { Name = "inside", AglM = 4550.0, PitchDeg = 8.0, HeadingDeg = 90.0, CameraDistM = 150.0 },
            new View { Name = "above", AglM = 8300.0, PitchDeg = -25.0, HeadingDeg = 90.0, CameraDistM = 150.0 },
            new View { Name = "orbit_edge", AglM = 300000.0, PitchDeg = double.NaN, HeadingDeg = 90.0, CameraDistM = 150.0 },
            new View { Name = "sky3", AglM = 200.0, PitchDeg = 38.0, HeadingDeg = 270.0, CameraDistM = 100.0 }
        };

        // The game the scenario reads and drives; a test replaces it with a fake world (IT09World).
        internal IT09World Game = new KspT09World();
        // The most real seconds the clock step waits for the weather field to answer at the site.
        internal const double FieldWaitS = 120.0;
        private int fewestFrames = int.MaxValue;
        private double fewestRatio = double.MaxValue;
        private double utChosen, coverChosen, sunChosen;
        private bool utOk;

        protected override string SeriesName
        {
            get { return Series; }
        }

        protected override double MaxSeconds
        {
            get { return 19 * 60; }
        }

        protected override IList<string> DefaultVariants
        {
            get { return new[] { "off", "plain", "recon4", "entry", "edge", "heightoff", "cap2", "cache", "gain" }; }
        }

        // --- Variants ---------------------------------------------------------------------------------------------------------------------

        private static bool IsOwn(string variant)
        {
            return variant != "tvc" && variant != "off";
        }

        // The variants that are measured in the "inside" view only.
        private static bool InsideOnly(string variant)
        {
            return variant == "edge" || variant == "heightoff" || variant == "cap2" || variant == "cache";
        }

        // What the module reports as its mode for the variant while it draws.
        private static string ExpectedMode(string variant)
        {
            return variant == "recon4" || variant == "edge" ? "reconstruct" : "plain";
        }

        protected override void ApplyVariant(string name)
        {
            switch (name)
            {
                case "tvc":
                    SetSetting(AtmospheresSettings.Keys.CloudSource, (double)(int)CloudSourceKind.Eve);
                    SetSetting(AtmospheresSettings.Keys.OwnClouds, 0.0);
                    break;
                case "off":
                    SetSetting(AtmospheresSettings.Keys.CloudSource, (double)(int)CloudSourceKind.None);
                    SetSetting(AtmospheresSettings.Keys.OwnClouds, 0.0);
                    break;
                case "recon4":
                    UseOwn();
                    SetSetting(AtmospheresSettings.Keys.CloudResolution, 4.0);
                    break;
                case "entry":
                    UseOwn();
                    SetSetting(AtmospheresSettings.Keys.CloudEntryRefinement, 1.0);
                    break;
                case "edge":
                    UseOwn();
                    SetSetting(AtmospheresSettings.Keys.CloudResolution, 4.0);
                    ForceTuning(EdgeSettings.TuneEdgeAdaptive, 1f);
                    break;
                case "heightoff":
                    UseOwn();
                    SetSetting(AtmospheresSettings.Keys.CloudBaseVariation, 0.0);
                    SetSetting(AtmospheresSettings.Keys.CloudBasePhysics, 0.0);
                    SetSetting(AtmospheresSettings.Keys.CloudTopFromUpdraft, 0.0);
                    SetSetting(AtmospheresSettings.Keys.BaseRagged, 0.0);
                    break;
                case "cap2":
                    UseOwn();
                    SetSetting(AtmospheresSettings.Keys.LayerCap, 2.0);
                    break;
                case "cache":
                    UseOwn();
                    SetSetting(AtmospheresSettings.Keys.CloudLightCache, 1.0);
                    break;
                default:
                    UseOwn();
                    break;
            }
        }

        // The own pass with every switch of the series at its starting value (the defaults of the product but for the ones the variants change).
        private void UseOwn()
        {
            ClearForced();
            SetSetting(AtmospheresSettings.Keys.CloudSource, (double)(int)CloudSourceKind.Ours);
            SetSetting(AtmospheresSettings.Keys.OwnClouds, 1.0);
            SetSetting(AtmospheresSettings.Keys.CloudReconMode, (double)(int)ReconMode.Full);
            SetSetting(AtmospheresSettings.Keys.CloudResolution, 1.0);
            SetSetting(AtmospheresSettings.Keys.CloudEntryRefinement, 0.0);
            SetSetting(AtmospheresSettings.Keys.CloudRefineAtContours, 0.0);
            SetSetting(AtmospheresSettings.Keys.CloudLightCache, 0.0);
            SetSetting(AtmospheresSettings.Keys.LayerCap, (double)CloudLayerDef.MaxLayers);
            SetSetting(AtmospheresSettings.Keys.CloudBaseVariation, 1.0);
            SetSetting(AtmospheresSettings.Keys.CloudBasePhysics, 1.0);
            SetSetting(AtmospheresSettings.Keys.CloudTopFromUpdraft, 1.0);
            SetSetting(AtmospheresSettings.Keys.BaseRagged, 1.0);
        }

        protected override void RevertVariant(string name)
        {
            ClearForced();
        }

        // --- The scenario -----------------------------------------------------------------------------------------------------------------

        private bool flightReached;

        // Every later step reads the vessel and the flight camera: when the scene changes or the vessel is gone, the probe ends with that reason.
        protected override string LostGround()
        {
            if (!flightReached) return null;
            string scene = Game.SceneNotFlight();
            if (scene != null) return "the flight scene was left during the scenario (scene " + scene + ")";
            if (!InFlightReady() || !Game.FlightCameraPresent()) return "the active vessel or the flight camera is gone during the scenario";
            return null;
        }

        protected override void ForceUniversalTime(double ut)
        {
            Game.UniversalTime = ut;
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            // The scene first: outside the flight the active vessel dereferences an object that does not exist (the first K-T2 start failed so in the
            // main menu). The game is read through Game (T09World.cs), whose members assume the flight; this wait is the guard.
            yield return ProbeWait.Until(() =>
            {
                if (!InFlightReady() || !Game.FlightCameraPresent()) return false;
                return Game.VesselUnpacked();
            }, 330, "the flight scene with an unpacked vessel");
            flightReached = true;
            yield return ProbeWait.Frames(120);
            if (!Game.BodyHasAtmosphere)
            {
                Emit("T09.skipped_no_atmosphere", true);
                yield break;
            }

            // The window: the size the start asked for (a popup window larger than the desktop is clamped, and two switches on the command line have no
            // documented winner), and no vertical sync (the GPU frame time of a synced frame is the refresh period whatever the clouds cost).
            string asked = CommandLine.After(Args(), "-screen-width", true);
            int askedWidth;
            EmitText("T09.screen", Game.ScreenWidth + "x" + Game.ScreenHeight + " (asked " + (asked ?? "nothing") + ")");
            if (asked != null && int.TryParse(asked, out askedWidth) && Game.ScreenWidth != askedWidth)
            {
                Fail("the window is " + Game.ScreenWidth + "x" + Game.ScreenHeight + " and the start asked for a width of " + askedWidth + "; the timing keys would not match the rows");
                yield break;
            }
            Game.VSyncCount = 0;
            Game.TargetFrameRate = -1;
            Emit("T09.vsync_count", Game.VSyncCount);
            Check("vertical sync is off", Game.VSyncCount == 0, "vSyncCount " + Game.VSyncCount);
            Emit("T09.target_frame_rate", Game.TargetFrameRate);
            Check("no frame cap is set", NoFrameCap(), "targetFrameRate " + Game.TargetFrameRate);

            double siteLat = Game.VesselLatitude, siteLon = Game.VesselLongitude;
            foreach (ProbeWait w in ChooseUt(Game.BodyIndex, siteLat, siteLon)) yield return w;
            if (!utOk)
            {
                Fail(utFailure);
                yield break;
            }
            Emit("T09.ut", utChosen);
            Emit("T09.cover", coverChosen);
            Emit("T09.sun_elev_deg", sunChosen);
            Check("the sky is covered at the chosen time", coverChosen >= MinCover, coverChosen.ToString("0.00"));
            Check("the sun stands in the band at the chosen time", sunChosen >= SunMinDeg && sunChosen <= SunMaxDeg, sunChosen.ToString("0.0") + " degrees");
            yield return ProbeWait.Seconds(3);

            foreach (string variant in Variants)
            {
                if (variant == "tvc")
                {
                    // A start without Scatterer freezes the frame in EVE's raymarch pass (EveRenderCompat): it is not entered.
                    bool scatterer = Game.ScattererLoaded, eve = Game.EveAvailable;
                    Emit("T09.tvc.scatterer_loaded", scatterer);
                    Emit("T09.tvc.eve_available", eve);
                    Emit("T09.tvc.volumetric_layers", Game.VolumetricLayerCount);
                    if (!scatterer || !eve)
                    {
                        Fail("the TVC variant needs EVE and Scatterer (Scatterer loaded " + scatterer + ", EVE usable " + eve + ")");
                        yield break;
                    }
                }
                BeginVariant(variant);
                fewestFrames = int.MaxValue;
                fewestRatio = double.MaxValue;
                yield return ProbeWait.Seconds(4);
                if (variant == "tvc")
                {
                    // EVE's layers are found by the painter's scan (every few seconds) and bound to the maps; wait for it, then state what stands.
                    yield return ProbeWait.Until(() => Game.EveLayerBound, 60, "EVE's layer to be bound to the maps of the field (TVC)");
                    EmitText("T09.tvc.status", Game.EveStatusText);
                    Emit("T09.tvc.layers_bound", Game.EveLayersBound);
                    Check("TVC draws: EVE's renderer is ready and a layer is bound", Game.EveRendererReady && Game.EveLayersBound > 0,
                          Game.EveStatusText + ", bound " + Game.EveLayersBound);
                }
                else if (IsOwn(variant))
                {
                    // The noise volumes are made on a worker and the first frames carry the shader warm-up: nothing is timed before the pass has drawn
                    // a steady stretch.
                    OwnCloudModule m = OwnCloudModule.Current;
                    int drawnFrom = m != null ? m.Stats.FramesDrawn : 0;
                    yield return ProbeWait.Until(() =>
                    {
                        OwnCloudModule o = OwnCloudModule.Current;
                        return o != null && o.Stats.Mode != "off" && o.Stats.FramesDrawn - drawnFrom > SteadyFrames;
                    }, 150, "the own pass to draw a steady stretch (" + variant + ")");
                }

                if (variant == "gain")
                {
                    foreach (ProbeWait w in GainScenario(siteLat, siteLon)) yield return w;
                }
                else
                {
                    foreach (View view in Views)
                    {
                        if (InsideOnly(variant) && view.Name != "inside") continue;
                        foreach (ProbeWait w in PlaceChecked(siteLat, siteLon, view)) yield return w;
                        if (!placeOk) continue;
                        foreach (ProbeWait w in Dwell(siteLat, siteLon, view, variant, InsideOnly(variant) ? InsideDwellS : DwellS)) yield return w;
                    }
                }
                if (IsOwn(variant)) EmitVariantTotals(variant);
                EndVariant();
            }
            Game.StopHold();
        }

        // The counters of the module after a variant. The worst times leave the first call out (the JIT is in it); the first ones are emitted apart.
        private void EmitVariantTotals(string variant)
        {
            OwnCloudModule own = OwnCloudModule.Current;
            if (own == null) return;
            if (fewestFrames != int.MaxValue) Emit("T09.view.frames_drawn", fewestFrames);
            if (fewestRatio != double.MaxValue) Emit("T09.view.frames_ratio", fewestRatio);
            Emit("T09.height_build_worst_ms", own.Stats.WorstHeightMs);
            Emit("T09.height_upload_worst_ms", own.Stats.WorstUploadMs);
            Emit("T09.map_fetch_worst_ms", own.Stats.WorstMapMs);
            Emit("T09.height_build_first_ms", own.Stats.FirstHeightMs);
            Emit("T09.map_fetch_first_ms", own.Stats.FirstMapMs);
            Emit("T09.buffer_attaches", own.Stats.BufferAttaches);
        }

        // --- One view ---------------------------------------------------------------------------------------------------------------------

        // No cap on the frame: a cap (KSP's FRAMERATE_LIMIT, set again by KSP's settings) would hide the cost of a cheap view in idle time.
        private bool NoFrameCap()
        {
            return Game.TargetFrameRate <= 0 && Game.VSyncCount == 0;
        }

        // The size the scene is rendered at, per view: the own pass's depth target (the camera it draws into) while it draws, else the flight camera's
        // pixel size (the upscaler redirects the camera into its render target). The series' keys are window sizes, so the two must be equal
        // (NativeAA); with the upscaler on Auto the 2560x1440 window renders 1707x960 and the second resolution measures nothing new.
        private void CheckRenderSize(View view, string variant)
        {
            OwnCloudModule own = OwnCloudModule.Current;
            int w, h;
            if (own != null && variant != "off" && variant != "tvc" && own.Stats.RenderWidth > 0) { w = own.Stats.RenderWidth; h = own.Stats.RenderHeight; }
            else
            {
                if (!Game.CameraPixelSize(out w, out h)) { Check("the render size is known in the " + view.Name + " view (" + variant + ")", false, "no flight camera"); return; }
            }
            string prefix = "T09.view." + view.Name + ".";
            EmitText(prefix + "render_size", w + "x" + h);
            Emit(prefix + "render_width", w);
            Emit(prefix + "render_height", h);
            Check("the scene renders at the window size in the " + view.Name + " view (" + variant + ")", w == Game.ScreenWidth && h == Game.ScreenHeight,
                  "render " + w + "x" + h + ", window " + Game.ScreenWidth + "x" + Game.ScreenHeight + " (the series pins quality NativeAA)");
        }

        private IEnumerable<ProbeWait> Dwell(double lat, double lon, View view, string variant, double seconds)
        {
            OwnCloudModule own = OwnCloudModule.Current;
            int drawnBefore = own != null ? own.Stats.FramesDrawn : 0;
            int framesBefore = Game.FrameCount;
            string size = Game.ScreenWidth + "x" + Game.ScreenHeight;
            AtmospheresLog.Say("T09 view begin: " + view.Name + " " + variant + " " + size);
            yield return ProbeWait.Seconds(seconds);
            AtmospheresLog.Say("T09 view end: " + view.Name + " " + variant + " " + size);
            int frames = Math.Max(1, Game.FrameCount - framesBefore);
            string prefix = "T09.view." + view.Name + ".";
            Emit(prefix + "frames", frames);
            Emit(prefix + "fps", frames / Math.Max(0.001, seconds));
            Check("no frame cap in the " + view.Name + " view (" + variant + ")", NoFrameCap(), "targetFrameRate " + Game.TargetFrameRate + ", vSyncCount " + Game.VSyncCount);
            CheckRenderSize(view, variant);
            if (variant == "tvc")
            {
                Check("TVC kept drawing in the " + view.Name + " view", Game.EveRendererReady && Game.EveLayersBound > 0 && Game.EveFailureLines == 0,
                      Game.EveStatusText + ", bound " + Game.EveLayersBound + ", invalid-pass lines " + Game.EveFailureLines);
                yield break;
            }
            int drawn = own != null ? own.Stats.FramesDrawn - drawnBefore : 0;
            Emit(prefix + "frames_drawn", drawn);
            EmitText(prefix + "mode", own != null ? own.Stats.Mode : "none");
            if (variant == "off")
            {
                Check("the own pass stood off in the " + view.Name + " view (off)", drawn == 0, drawn + " frames drawn");
                yield break;
            }
            double ratio = (double)drawn / frames;
            fewestFrames = Math.Min(fewestFrames, drawn);
            fewestRatio = Math.Min(fewestRatio, ratio);
            string why = own != null ? own.Stats.Why : "no module";
            Check("the own pass drew in the " + view.Name + " view (" + variant + ")", drawn >= 60 && ratio >= 0.95, drawn + " of " + frames + " frames, why: " + why);
            bool cacheOn = own != null && own.Stats.LightCacheOn;
            Emit(prefix + "light_cache_on", cacheOn);
            Check("the light cache is " + (variant == "cache" ? "filled" : "off") + " in the " + view.Name + " view (" + variant + ")", cacheOn == (variant == "cache"), "cache on: " + cacheOn);
            string mode = own != null ? own.Stats.Mode : "none";
            Check("the pass ran as " + ExpectedMode(variant) + " in the " + view.Name + " view (" + variant + ")", mode == ExpectedMode(variant), mode);
            double altitude = Math.Min(20000.0, view.AglM);
            WeatherSample s;
            if (WeatherHub.Sample(Game.BodyIndex, lat, lon, altitude, Game.UniversalTime, out s)) Emit(prefix + "cover_at_view_height", s.CloudCover01);
        }

        // --- The clock --------------------------------------------------------------------------------------------------------------------

        private string utFailure;

        // The first candidate time from the fixed base on whose sky over the site is covered by at least half of the layers' cover AND whose sun
        // stands in the band; the clock is set to it. The cover is asked of the field without moving the clock; only candidates with cover set it.
        // The field answers false at the site until it has built the body's state (the hub holds a NullField that reports Ready until the atmospheres
        // assign the real one, and the real one reports Ready before the first body state exists), so a pass with candidates the field did not
        // answer for is repeated every second, up to FieldWaitS, until a candidate fits or all 48 are answered; a field that answers for some times
        // only is covered by the same rule. The candidates are not sampled once and abandoned.
        private IEnumerable<ProbeWait> ChooseUt(int bodyIndex, double lat, double lon)
        {
            utOk = false;
            utFailure = null;
            double start = Game.UniversalTime;
            double baseUt = Math.Ceiling(start / BaseStepS) * BaseStepS;
            double giveUpAt = Elapsed + FieldWaitS;
            int unsampled, covered, passes = 0;
            while (true)
            {
                passes++;
                unsampled = 0;
                covered = 0;
                for (int k = 0; k < Candidates; k++)
                {
                    double t = baseUt + k * CandidateStepS;
                    WeatherSample s;
                    if (!WeatherHub.Sample(bodyIndex, lat, lon, 3000.0, t, out s))
                    {
                        unsampled++;
                        continue;
                    }
                    if (s.CloudCover01 < MinCover) continue;
                    covered++;
                    ForceUniversalTime(t);
                    yield return ProbeWait.Frames(6);
                    double elevation = Game.SunElevationDeg();
                    if (elevation < SunMinDeg || elevation > SunMaxDeg) continue;
                    utChosen = t;
                    coverChosen = s.CloudCover01;
                    sunChosen = elevation;
                    utOk = true;
                    Emit("T09.ut_jump_s", t - start);
                    if (passes > 1) AtmospheresLog.Say("T09 clock: the field answered after " + (Elapsed - (giveUpAt - FieldWaitS)).ToString("0") + " s (" + passes + " passes)");
                    yield break;
                }
                if (unsampled == 0 || Elapsed >= giveUpAt) break;
                yield return ProbeWait.Seconds(1);
            }
            AtmospheresLog.Say("T09 clock: no candidate (" + unsampled + " not sampled, " + covered + " covered but not in the sun band, " + passes + " passes)");
            if (unsampled > 0)
            {
                IWeatherField field = WeatherHub.Field;
                utFailure = "the weather field did not answer at the site for " + unsampled + " of " + Candidates + " candidate times within " + FieldWaitS.ToString("0")
                            + " s (field " + (field != null ? field.GetType().Name + ", readiness " + field.Readiness : "none") + "; " + covered
                            + " answered with cover and none of them stood in the sun band); the clock cannot be chosen";
            }
            else
            {
                utFailure = "no universal time in " + Candidates + " hours from the base has a cover of at least " + MinCover + " over the site and the sun " + SunMinDeg + " to " + SunMaxDeg
                            + " degrees up (" + covered + " had the cover); the timing would not describe a covered daylight sky";
            }
        }

        // --- Placement --------------------------------------------------------------------------------------------------------------------

        private bool placeOk;
        private double wantedAltitude, wantedPitch;

        // Place, then check that the vessel is at the height asked for (within 2 percent, at least 5 m) and the camera looks where asked (within
        // AimToleranceDeg), and place again (up to PlaceAttempts times) when not; a view that cannot be placed fails the probe and is left out, because
        // a view measured at the pad would pass as the orbit view. The real altitude and aim are emitted.
        private IEnumerable<ProbeWait> PlaceChecked(double lat, double lon, View view)
        {
            placeOk = false;
            string detail = "";
            for (int attempt = 0; attempt < PlaceAttempts && !placeOk; attempt++)
            {
                if (attempt > 0) yield return ProbeWait.Seconds(PlaceRetryWaitS);
                foreach (ProbeWait w in Place(lat, lon, view)) yield return w;
                foreach (ProbeWait w in Aim(view.HeadingDeg, wantedPitch, view.CameraDistM)) yield return w;
                double vesselAltitude = Game.VesselAltitude;
                double az, el;
                Game.CameraAim(out az, out el);
                double altitudeError = Math.Abs(vesselAltitude - wantedAltitude), tolerance = Math.Max(AltitudeToleranceM, 0.02 * wantedAltitude);
                double aimError = Math.Abs(el - wantedPitch), headingError = Math.Abs(WrapDeg(az - view.HeadingDeg));
                bool altitudeOk = altitudeError <= tolerance, aimOk = aimError <= AimToleranceDeg && (Math.Abs(el) > 80.0 || headingError <= 5.0 * AimToleranceDeg);
                detail = "altitude " + vesselAltitude.ToString("0") + " m against " + wantedAltitude.ToString("0") + ", camera elevation " + el.ToString("0.0") + " against "
                         + wantedPitch.ToString("0.0") + ", heading " + az.ToString("0") + " against " + view.HeadingDeg.ToString("0") + ", " + Game.VesselState;
                AtmospheresLog.Say("T09 view placed: " + view.Name + " attempt " + (attempt + 1) + " of " + PlaceAttempts + ": " + detail + (altitudeOk && aimOk ? " (ok)" : " (not yet)"));
                string prefix = "T09.view." + view.Name + ".";
                Emit(prefix + "altitude_m", vesselAltitude);
                Emit(prefix + "elevation_deg", el);
                Emit(prefix + "azimuth_deg", az);
                placeOk = altitudeOk && aimOk;
            }
            if (!placeOk) Check("T09.pose_placed_" + view.Name + "_" + CurrentVariant, false, detail);
        }

        // The vessel at rest in the air over the site at the view's height, upright, held.
        private IEnumerable<ProbeWait> Place(double lat, double lon, View view)
        {
            double terrain = 0.0;
            try { terrain = Game.TerrainAltitude(lat, lon); } catch (Exception) { terrain = 0.0; }
            wantedAltitude = Math.Max(0.0, terrain) + view.AglM;
            Game.PlaceVessel(lat, lon, wantedAltitude);
            Game.StartHold();
            yield return ProbeWait.Frames(20);
            wantedPitch = view.PitchDeg;
            if (double.IsNaN(wantedPitch))
            {
                // The limb: the horizon lies below the horizontal by acos(R / (R + h)); the camera looks at it.
                double h = Game.VesselAltitude;
                double radius = Game.BodyRadius;
                wantedPitch = -Math.Acos(radius / (radius + Math.Max(0.0, h))) * 180.0 / Math.PI;
            }
            yield return ProbeWait.Frames(12);
        }

        private static double WrapDeg(double d)
        {
            while (d > 180.0) d -= 360.0;
            while (d < -180.0) d += 360.0;
            return d;
        }

        // Turns the flight camera to the compass heading and elevation: it orbits the vessel and looks at it, so the vessel sits at the centre of the
        // picture. The camera's real aim is read back and corrected; the sign of each correction is learnt from whether the error shrank.
        private IEnumerable<ProbeWait> Aim(double azimuthDeg, double elevationDeg, double distance)
        {
            Game.SetCameraDistance(distance);
            // A camera below the vessel (a negative camPitch) looks up.
            Game.CameraPitch = -(float)(elevationDeg * Math.PI / 180.0);
            Game.CameraHeading = (float)(WrapDeg(azimuthDeg + 180.0) * Math.PI / 180.0);
            float signH = 1f, signP = 1f;
            double lastEz = double.NaN, lastEe = double.NaN;
            for (int i = 0; i < 12; i++)
            {
                yield return ProbeWait.Frames(3);
                double az, el;
                Game.CameraAim(out az, out el);
                double ez = WrapDeg(azimuthDeg - az), ee = elevationDeg - el;
                if (Math.Abs(ez) < AimToleranceDeg / 2.0 && Math.Abs(ee) < AimToleranceDeg / 2.0) break;
                if (!double.IsNaN(lastEz))
                {
                    if (Math.Abs(ez) > Math.Abs(lastEz) + 0.5) signH = -signH;
                    if (Math.Abs(ee) > Math.Abs(lastEe) + 0.5) signP = -signP;
                }
                lastEz = ez;
                lastEe = ee;
                Game.CameraHeading += signH * (float)(ez * Math.PI / 180.0);
                Game.CameraPitch -= signP * (float)(ee * Math.PI / 180.0);
            }
            yield return ProbeWait.Frames(3);
        }

        // --- The gains --------------------------------------------------------------------------------------------------------------------

        // No dwell: the sun's gain (cloud.sunGain) and the moon's (cloud.moonGain) must reach the pass. Read off the frame the module fills: the sun
        // colour's luminance at gain 1 and at gain 2 (expected ratio 2); the moon's light colour likewise where a moon or planet lights the clouds at
        // this time (the provider answers, and the cloudMoonLights count lets a light through). The gains are tuning keys: the visual calibration of
        // the sun's unit against the sky is the row T09.exposure.cloud_over_sky, which this probe does not read.
        private IEnumerable<ProbeWait> GainScenario(double lat, double lon)
        {
            View inside = Views[1];
            foreach (ProbeWait w in PlaceChecked(lat, lon, inside)) yield return w;
            OwnCloudModule own = OwnCloudModule.Current;
            if (own == null) Check("the own cloud module exists for the gain check", false, "OwnCloudModule.Current is null");
            if (!placeOk || own == null) yield break;
            yield return ProbeWait.Seconds(2);
            float sunBase = own.Frame.SunColour.grayscale;
            ForceTuning(OwnCloudModule.TuneSunGain, 2f);
            yield return ProbeWait.Frames(15);
            float sunDouble = own.Frame.SunColour.grayscale;
            Emit("T09.gain.sun_ratio", sunBase > 1e-6f ? sunDouble / sunBase : 0.0);
            Check("cloud.sunGain 2 doubles the sun colour of the pass", sunBase > 1e-6f && Math.Abs(sunDouble / sunBase - 2f) < 0.1f, sunBase.ToString("0.000") + " -> " + sunDouble.ToString("0.000"));
            ClearForced();

            bool answers = Game.MoonProviderAnswers();
            Emit("T09.moon.provider_answers", answers);
            yield return ProbeWait.Frames(10);
            float moonBase = own.Frame.Light.Lights[0].Colour.grayscale;
            if (!answers || moonBase <= 1e-6f)
            {
                EmitText("T09.gain.moon_note", answers ? "the provider answers but cloudMoonLights lets no light through" : "no moon or planet lights the clouds at this time");
                yield break;
            }
            ForceTuning(OwnCloudModule.TuneMoonGain, 2f);
            yield return ProbeWait.Frames(15);
            float moonDouble = own.Frame.Light.Lights[0].Colour.grayscale;
            Emit("T09.gain.moon_ratio", moonDouble / moonBase);
            Check("cloud.moonGain 2 doubles the moon light of the pass", Math.Abs(moonDouble / moonBase - 2f) < 0.1f, moonBase.ToString("0.0000") + " -> " + moonDouble.ToString("0.0000"));
            ClearForced();
        }
    }
}
