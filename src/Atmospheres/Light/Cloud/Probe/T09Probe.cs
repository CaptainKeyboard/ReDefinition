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
    // plain (own pass, resolution level 1: the half-size march target, a quarter of the pixels), recon2, recon3 and recon4 (the levels of the slider cloudResolution: one pixel of every 2 x 2, 3 x 3 or 4 x 4 block
    // per frame; recon2 and recon3 in the views ground, inside and above only, for the time); then the A/B variants that need one view only (the "inside" view): cache (the light cache
    // on: its saving = plain minus cache, fill included), cap2 (the layer cap at 2), entry (plain plus the entry refinement), and, when asked for with -atmoVariants but not in the
    // default list (time): edge (the edge-adaptive mode, T12) and heightoff (the four height switches off, T13: cost of the height variation = plain minus heightoff); and gain (no
    // dwell: the sun and moon gains reach the pass).
    // The views: ground (200 m above the pad, looking up), inside (inside the lowest cumulus layer), above (between the cumulus tops and the cirrus,
    // looking down), orbit_edge (300 km, looking at the limb), sky3 (200 m, higher pitch under all layers).
    // The weather is the field's at one universal time, chosen as K-T2d chooses its picks (ChooseUt): from a fixed base (the next full hour of the clock at the start) the hourly
    // candidates and a far window of 10 days; the candidate whose cover over the site at 3 km is nearest to 0.65 (at least 0.5) with the sun 30 to 55 degrees up (the wide band 15 to 60
    // only where the narrow one holds none); the same for every variant and, as the base is the same, for every start. The timing window of each view starts after the hold's
    // stable gate (StableGate). TVC draws its own layer set (the pack's layers plus the painter's layer at 2700 m), the own stack its
    // four layers: the columns compare two systems at the same camera, not equal clouds.
    // Pictures (T09Pictures.cs): after the dwell of each view and variant, in a short extra window after the "T09 view end" line ("T09 capture begin" and
    // "end" lines around it, rows T09.view.<view>.pictures / capture_s / capture_bytes), one natural picture and, for the own pass, one aid picture
    // (the layer shader's optical-depth aid). A fault of the pictures is a named warning ("Probe: warning <name>", row T09.warning.<name>), never a failure.
    // Pose (start 42 showed that the placement check alone is not enough: the vessel left its pose within seconds, shed its stages, and the pictures showed space
    // and a dot): the hold (T09World.cs) puts the vessel back at its anchor, the dwell reads the pose every 10 s and at its end (T09.pose_held_<view>_<variant>,
    // rows T09.view.<view>.hold_corrections / hold_worst_drift_m), and every picture reads it when the capture starts and when it is taken
    // (T09.pose_picture_<name>, rows T09.pic.<name>.alt_m, alt_agl_m, cam_alt_m, cam_agl_m, elev_deg, heading_deg, cam_dist_m, drift_m); a view or picture whose pose
    // deviates fails named and the picture is not saved. The camera is forced to FREE before the aim (AUTO is ORBITAL above the atmosphere).
    // Not run in the game yet [VERIFY]: the hold's position correction (Vessel.SetPosition every few frames) and the FREE mode's aim at 300 km.
    [AtmospheresModule(985)]
    // Series K-T2d (T09ProbeDiag.cs, the same class): the diagnosis of the black own clouds of start 42, one start of the variant plain with numeric readouts; the game's
    // command line (-atmoSeries K-T2d) picks it, -atmoProbe names this class in both series.
    internal sealed partial class T09Probe : SeriesProbe
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
            get { return IsDiag ? DiagSeries : Series; }
        }

        protected override double MaxSeconds
        {
            get { return IsDiag ? DiagMaxSeconds : 19 * 60; }
        }

        protected override IList<string> DefaultVariants
        {
            get { return IsDiag ? new[] { "plain" } : new[] { "off", "plain", "recon2", "recon3", "recon4", "cache", "cap2", "entry", "gain" }; }
        }

        // --- Variants ---------------------------------------------------------------------------------------------------------------------

        private static bool IsOwn(string variant)
        {
            return variant != "tvc" && variant != "off";
        }

        // The variants that are measured in the "inside" view only.
        private static bool InsideOnly(string variant)
        {
            return variant == "edge" || variant == "heightoff" || variant == "cap2" || variant == "cache" || variant == "entry";
        }

        // recon2 and recon3 are measured in the three views that carry the clouds against a surface (ground, inside, above), recon4 and the baselines in all five: the time budget
        // of the probe (19 minutes, a view window costs about 36 s) does not hold two more levels in five views. The ratios to plain are read over the common three views.
        private static bool ThreeViewsOnly(string variant)
        {
            return variant == "recon2" || variant == "recon3";
        }

        private static bool Measured(string variant, View view)
        {
            if (InsideOnly(variant)) return view.Name == "inside";
            if (ThreeViewsOnly(variant)) return view.Name == "ground" || view.Name == "inside" || view.Name == "above";
            return true;
        }

        // What the module reports as its mode for the variant while it draws.
        private static string ExpectedMode(string variant)
        {
            return IsReconLevel(variant) || variant == "edge" ? "reconstruct" : "plain";
        }

        // The cloud resolution levels (the slider cloudResolution): plain is level 1, recon2 to recon4 march one pixel of every 2 x 2, 3 x 3 or 4 x 4 block.
        private static bool IsReconLevel(string variant)
        {
            return ReconLevel(variant) > 1;
        }

        internal static int ReconLevel(string variant)
        {
            switch (variant)
            {
                case "recon2": return 2;
                case "recon3": return 3;
                case "recon4": return 4;
                default: return 1;
            }
        }

        // The variants whose picture noise is read (T09Noise.cs): the plain pass and every reconstruction level, in the views that show clouds against a surface.
        private static bool NoiseVariant(string variant)
        {
            return variant == "plain" || IsReconLevel(variant);
        }

        private static bool NoiseView(View view)
        {
            return view.Name == "ground" || view.Name == "above";
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
                case "recon2":
                case "recon3":
                case "recon4":
                    UseOwn();
                    SetSetting(AtmospheresSettings.Keys.CloudResolution, (double)ReconLevel(name));
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

        // The switches the weather field and the cloud renderers depend on (CloudGates.cs), set before the clock step and logged with their effective values.
        // False only in a test that runs the probe as it was before start 41 (it did not set these switches).
        internal bool SetsProductSwitches = true;

        private void ApplyProductSwitches()
        {
            if (!SetsProductSwitches) return;
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            SetSetting(AtmospheresSettings.Keys.GenEnabled, 1.0);
            AtmospheresLog.Say("T09 gates: " + CloudGates.Describe(AtmospheresSettings.Number));
            EmitText("T09.gates", CloudGates.Describe(AtmospheresSettings.Number));
            Check("the weather generator's field is installed by its gate (enabled, weatherEffects, genEnabled)", CloudGates.GeneratorInstalled(AtmospheresSettings.Number),
                  CloudGates.Describe(AtmospheresSettings.Number));
        }

        // After a variant's switches are set: the gate of the renderer the variant measures is open (own: ownClouds with the source Ours; TVC: EVE's painter).
        private void CheckVariantGates(string variant)
        {
            string described = CloudGates.Describe(AtmospheresSettings.Number);
            AtmospheresLog.Say("T09 gates (" + variant + "): " + described);
            if (variant == "off") return;
            if (variant == "tvc")
            {
                Check("EVE's painter gate is open (tvc)", CloudGates.EvePainterOn(AtmospheresSettings.Number), described);
                return;
            }
            Check("the own pass's switches are open (" + variant + ")", CloudGates.OwnPassSwitches(AtmospheresSettings.Number, false), described);
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
            try { Game.AidOff(); } catch (Exception) { /* the aid view is a picture aid; the game may be gone */ }
            // The noise read draws no layer for a few frames (T09ProbeNoise.cs); a scenario that ends inside it leaves the mask open.
            if (!IsDiag)
            {
                try { Game.SetLayerMask(0); } catch (Exception) { /* the game may be gone */ }
            }
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
            return IsDiag ? DiagScenario() : StandardScenario();
        }

        // False after a Prelude that ended the scenario (no atmosphere, a window of another size): the scenario returns without more.
        private bool preludeOk;

        // The steps both series start with: the flight scene, the atmosphere, the window, no sync and no cap, the product switches.
        private IEnumerable<ProbeWait> Prelude()
        {
            preludeOk = false;
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

            // The product switches the chain needs, whatever the player's settings say (weatherEffects is off by default: the generator's field is then
            // never installed and the clock step waits for a NullField, start 41).
            ApplyProductSwitches();
            preludeOk = true;
        }

        private IEnumerator<ProbeWait> StandardScenario()
        {
            foreach (ProbeWait w in Prelude()) yield return w;
            if (!preludeOk) yield break;

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
            Emit("T09.sun_band_narrow", utNarrow);
            Check("the sky is covered at the chosen time", coverChosen >= MinCover, coverChosen.ToString("0.00"));
            Check("the sun stands in the band at the chosen time", sunChosen >= SunMinDeg && sunChosen <= SunMaxDeg, sunChosen.ToString("0.0") + " degrees");
            // The maps follow a clock jump at the slew limit (0.08 a second): the same wait as K-T2d's before its first view.
            AtmospheresLog.Say("T09 clock: ut " + utChosen.ToString("0") + ", cover " + coverChosen.ToString("0.00") + ", sun " + sunChosen.ToString("0.0") + " degrees ("
                               + (utNarrow ? "narrow" : "wide") + " band); waiting " + MapSettleS.ToString("0") + " s for the maps");
            yield return ProbeWait.Seconds(MapSettleS);
            CheckPicturesFolder();

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
                CheckVariantGates(variant);
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
                        if (!Measured(variant, view)) continue;
                        if (!IsDiag && view.Name == "orbit_edge" && orbitEdgeFailedIn != null)
                        {
                            // Its placement failed once (4 attempts of about a minute): the later variants do not spend the scenario's minutes on it again.
                            LogLine("Probe: info orbit_edge skipped in " + variant + " (its placement failed in " + orbitEdgeFailedIn + ")");
                            EmitText("T09.info.orbit_edge_skipped_" + variant, "placement failed in " + orbitEdgeFailedIn);
                            continue;
                        }
                        foreach (ProbeWait w in PlaceChecked(siteLat, siteLon, view)) yield return w;
                        if (!placeOk)
                        {
                            if (view.Name == "orbit_edge" && orbitEdgeFailedIn == null) orbitEdgeFailedIn = variant;
                            continue;
                        }
                        foreach (ProbeWait w in StableGate(view, variant)) yield return w;
                        foreach (ProbeWait w in Dwell(siteLat, siteLon, view, variant, InsideOnly(variant) ? InsideDwellS : DwellS)) yield return w;
                        foreach (ProbeWait w in Pictures(view, variant)) yield return w;
                        if (NoiseVariant(variant) && NoiseView(view))
                        {
                            foreach (ProbeWait w in Noise(view, variant)) yield return w;
                        }
                    }
                }
                if (IsOwn(variant)) EmitVariantTotals(variant);
                EndVariant();
            }
            Game.StopHold();
            Emit("T09.pictures.saved", picturesSaved);
            Emit("T09.pictures.bytes", picturesBytes);
            Emit("T09.pictures.warnings", pictureWarnings);
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
            Emit("T09.height_build_main_ms", own.Stats.WorstHeightMainMs);
            Emit("T09.map_fetch_main_ms", own.Stats.WorstMapMainMs);
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
            // The dwell in slices: the pose is read at the end of each (the placement check says only where the vessel stood a few seconds before the dwell; in
            // start 42 it had left the pose within seconds), the first deviation fails the view named and the pictures of it are not taken.
            viewPoseOk = true;
            viewWorstDriftM = 0.0;
            double dwelled = 0.0;
            while (dwelled < seconds - 1e-9)
            {
                double slice = Math.Min(HeldSliceS, seconds - dwelled);
                yield return ProbeWait.Seconds(slice);
                dwelled += slice;
                CheckHeld(view, variant, dwelled);
            }
            Emit("T09.view." + view.Name + ".hold_corrections", Game.HoldCorrections);
            Emit("T09.view." + view.Name + ".hold_worst_drift_m", Math.Max(viewWorstDriftM, Game.HoldWorstDriftM));
            if (viewPoseOk) Check("T09.pose_held_" + view.Name + "_" + variant, true, "pose kept for " + seconds.ToString("0") + " s, worst drift " + viewWorstDriftM.ToString("0.0") + " m, hold corrections " + Game.HoldCorrections);
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

        // --- The pose at the end of a dwell and around a capture ---------------------------------------------------------------------------

        // The vessel is held within 5 m (T09Hold.ToleranceM) at the capture; 20 m leaves room for the sampling and is far below the 200 to 500 m of start 44. The placement
        // (both series) and K-T2d's frames, which are compared pixel for pixel, use 20 m; the dwell of K-T2 keeps its 100 m (DriftToleranceM): a timing window does not depend
        // on where the vessel stands within a hundred metres, and the hold's own trace says how it stood.
        private const double PlacedDriftToleranceM = 20.0, DiagDriftToleranceM = 20.0;
        private const double HeldSliceS = 10.0, DriftToleranceM = 100.0, DistanceToleranceFraction = 0.25, DistanceToleranceMinM = 10.0, HeadingToleranceDeg = 15.0;
        private bool viewPoseOk;
        private double viewWorstDriftM;

        // What deviates from the view's target in this pose (null where nothing does): the vessel's height over sea level against the one asked for, the camera's
        // elevation and heading, the camera's distance from the vessel (a vessel that is a dot has left the camera: start 42's pictures) and the vessel's distance from
        // the point the hold keeps it at.
        private string PoseProblem(View view, T09Pose p)
        {
            var problems = new List<string>();
            double tolerance = Math.Max(AltitudeToleranceM, 0.02 * wantedAltitude);
            if (Math.Abs(p.VesselAsl - wantedAltitude) > tolerance)
                problems.Add("altitude " + p.VesselAsl.ToString("0") + " m (" + p.VesselAgl.ToString("0") + " over the terrain) against " + wantedAltitude.ToString("0"));
            if (Math.Abs(p.ElevationDeg - wantedPitch) > AimToleranceDeg)
                problems.Add("camera elevation " + p.ElevationDeg.ToString("0.0") + " against " + wantedPitch.ToString("0.0"));
            if (Math.Abs(wantedPitch) < 80.0 && Math.Abs(WrapDeg(p.AzimuthDeg - view.HeadingDeg)) > HeadingToleranceDeg)
                problems.Add("camera heading " + p.AzimuthDeg.ToString("0") + " against " + view.HeadingDeg.ToString("0"));
            double distanceTolerance = Math.Max(DistanceToleranceMinM, DistanceToleranceFraction * view.CameraDistM);
            if (p.CameraToVesselM > 0.0 && Math.Abs(p.CameraToVesselM - view.CameraDistM) > distanceTolerance)
                problems.Add("camera " + p.CameraToVesselM.ToString("0") + " m from the vessel against " + view.CameraDistM.ToString("0"));
            if (p.DriftM > (IsDiag ? DiagDriftToleranceM : DriftToleranceM)) problems.Add("the vessel is " + p.DriftM.ToString("0") + " m from the point it is held at");
            return problems.Count == 0 ? null : string.Join(", ", problems.ToArray());
        }

        private void CheckHeld(View view, string variant, double dwelled)
        {
            T09Pose p = Game.ReadPose();
            viewWorstDriftM = Math.Max(viewWorstDriftM, p.DriftM);
            string problem = PoseProblem(view, p);
            if (problem == null || !viewPoseOk) return;
            viewPoseOk = false;
            Check("T09.pose_held_" + view.Name + "_" + variant, false, problem + " after " + dwelled.ToString("0") + " s of the dwell (the placement was ok; hold corrections " + Game.HoldCorrections
                  + ", worst drift " + Game.HoldWorstDriftM.ToString("0") + " m, camera mode " + Game.CameraModeText + ")");
        }

        // The rows of one picture: where the camera and the vessel were when it was read.
        private void EmitPose(string prefix, T09Pose p)
        {
            Emit(prefix + "alt_m", p.VesselAsl);
            Emit(prefix + "alt_agl_m", p.VesselAgl);
            Emit(prefix + "cam_alt_m", p.CameraAsl);
            Emit(prefix + "cam_agl_m", p.CameraAgl);
            Emit(prefix + "elev_deg", p.ElevationDeg);
            Emit(prefix + "heading_deg", p.AzimuthDeg);
            Emit(prefix + "cam_dist_m", p.CameraToVesselM);
            Emit(prefix + "drift_m", p.DriftM);
        }

        // --- The pictures -----------------------------------------------------------------------------------------------------------------

        // Real seconds before the scenario's limit from which no picture is taken (the measurement is what the start is for).
        private const double PictureReserveS = 60.0;
        private int picturesSaved, pictureWarnings, pictureFaults;
        private long picturesBytes;
        private bool picturesStopped;
        private Color[] naturalPixels;

        // A fault of the pictures is a named warning, never a failure: the timing series is what the start is for. Row T09.warning.<name>, log line "Probe: warning".
        private void Warn(string name, string detail)
        {
            pictureWarnings++;
            LogLine("Probe: warning " + name + ": " + detail);
            EmitText("T09.warning." + name, detail);
        }

        // A folder that cannot take pictures is known before the first view: the start takes none and says why.
        private void CheckPicturesFolder()
        {
            string problem;
            try { problem = Game.PicturesFolderProblem(); }
            catch (Exception e) { problem = "the folder check threw " + e.GetType().Name + " " + e.Message; }
            if (problem == null) return;
            picturesStopped = true;
            Warn("pictures_folder", problem + "; the start takes no pictures");
        }

        // After the dwell of a view (so after its "T09 view end" line and outside the window the timing rows are cut from): the natural picture and,
        // for the own pass, the aid picture, with the cost in rows. A picture that fails is a warning and the next one is tried.
        private IEnumerable<ProbeWait> Pictures(View view, string variant)
        {
            if (picturesStopped) yield break;
            if (!viewPoseOk)
            {
                Warn("pose_mismatch", view.Name + " " + variant + ": the pose left the view during the dwell (T09.pose_held_" + view.Name + "_" + variant + "); no picture of a pose that is not the view's");
                yield break;
            }
            if (Elapsed > MaxSeconds - PictureReserveS)
            {
                picturesStopped = true;
                Warn("pictures_time", "the scenario is at " + Elapsed.ToString("0") + " s of " + MaxSeconds.ToString("0") + " s; the pictures stop so that the measurement ends in time");
                yield break;
            }
            double began = Elapsed;
            int savedBefore = picturesSaved;
            long bytesBefore = picturesBytes;
            string size = Game.ScreenWidth + "x" + Game.ScreenHeight;
            AtmospheresLog.Say("T09 capture begin: " + view.Name + " " + variant + " " + size);
            naturalPixels = null;
            foreach (ProbeWait w in Shoot(view, variant, T09Pictures.Natural)) yield return w;
            if (T09Pictures.HasAid(variant) && !picturesStopped)
            {
                foreach (ProbeWait w in Shoot(view, variant, T09Pictures.AidKind)) yield return w;
            }
            naturalPixels = null;
            AtmospheresLog.Say("T09 capture end: " + view.Name + " " + variant + " " + size);
            string prefix = "T09.view." + view.Name + ".";
            Emit(prefix + "pictures", picturesSaved - savedBefore);
            Emit(prefix + "capture_s", Elapsed - began);
            Emit(prefix + "capture_bytes", picturesBytes - bytesBefore);
        }

        private IEnumerable<ProbeWait> Shoot(View view, string variant, string kind)
        {
            bool aid = kind != T09Pictures.Natural;
            string what = view.Name + " " + variant + " " + kind;
            if (aid)
            {
                bool on = false;
                try { on = Game.AidOn(); }
                catch (Exception e) { Warn("aid_view_fault", what + ": " + e.GetType().Name + " " + e.Message); }
                if (!on)
                {
                    Warn("no_aid_view", what + ": the own pass has no aid view now (module missing, renderer not ready or pass off); the natural picture stands alone");
                    yield break;
                }
                yield return ProbeWait.Frames(T09Pictures.SettleFrames(variant));
            }
            T09Image image = null;
            string fault = null;
            bool started = false;
            bool half = !T09Pictures.FullSize(view.Name, variant);
            string name = T09Pictures.Name(StartId, view.Name, variant, Game.ScreenWidth, Game.ScreenHeight, kind, half);
            // The pose of the camera the picture is read from (the flight camera the pass and the timing use), at the moment the read starts.
            // The pose has to have been stable for a stretch of frames: wait for it (at most 60 frames) rather than read a vessel that is still settling.
            for (int i = 0; i < 12 && Game.HoldReached && !Game.HoldStable; i++) yield return ProbeWait.Frames(5);
            T09Pose before = Game.ReadPose();
            try { started = Game.CaptureBegin(); }
            catch (Exception e) { fault = "the read threw " + e.GetType().Name + " " + e.Message; }
            if (started)
            {
                double giveUp = Elapsed + T09Pictures.ReadWaitS;
                while (CaptureBusy(ref fault) && Elapsed < giveUp) yield return ProbeWait.Frames(1);
                if (fault == null && CaptureBusy(ref fault)) fault = "the read did not finish in " + T09Pictures.ReadWaitS.ToString("0") + " s";
                if (fault == null)
                {
                    try { image = Game.CaptureTake(); }
                    catch (Exception e) { fault = "taking the read threw " + e.GetType().Name + " " + e.Message; }
                    if (fault == null && (image == null || image.Pixels == null || image.Pixels.Length != image.Width * image.Height || image.Pixels.Length == 0)) fault = "the read gave no picture";
                }
            }
            else if (fault == null) fault = "no read could start (one is still under way)";
            if (aid)
            {
                try { Game.AidOff(); }
                catch (Exception e) { Warn("aid_view_fault", what + ": switching it off threw " + e.GetType().Name + " " + e.Message); }
            }
            if (fault != null)
            {
                Warn("capture_failed", what + ": " + fault);
                NoteFault();
                yield break;
            }
            if (aid && naturalPixels != null && !Differs(naturalPixels, image.Pixels))
            {
                Warn("aid_equals_natural", what + ": the aid picture equals the natural one (the aid mode did not reach the pass)");
            }
            // The pose when the read was taken: both ends must be the view's pose, else the picture shows another place than its name says.
            T09Pose after = Game.ReadPose();
            string poseRows = "T09.pic." + name + ".";
            EmitPose(poseRows, before);
            Emit(poseRows + "drift_after_m", after.DriftM);
            string poseProblem = PoseProblem(view, before) ?? PoseProblem(view, after);
            if (poseProblem != null)
            {
                Check("T09.pose_picture_" + name, false, poseProblem + " (read starting " + before.VesselAsl.ToString("0") + " m, ending " + after.VesselAsl.ToString("0") + " m)");
                Warn("pose_mismatch", name + ": " + poseProblem + "; the picture is not saved");
                NoteFault();
                yield break;
            }
            if (!aid) naturalPixels = image.Pixels;
            long bytes;
            try { bytes = Game.SavePicture(name, image, half ? 2 : 1); }
            catch (Exception e)
            {
                Warn("picture_not_saved", name + ".png: " + e.GetType().Name + " " + e.Message);
                NoteFault();
                yield break;
            }
            pictureFaults = 0;
            picturesSaved++;
            picturesBytes += bytes;
            LogLine("Probe: picture " + name + ".png (" + image.Width + "x" + image.Height + (half ? " read, half size written" : "") + ", " + bytes + " bytes)");
        }

        private bool CaptureBusy(ref string fault)
        {
            try { return Game.CaptureBusy; }
            catch (Exception e)
            {
                fault = "waiting for the read threw " + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        // Faults in a row stop the pictures (a full disk would encode every picture for nothing); one picture that works resets the count.
        private void NoteFault()
        {
            pictureFaults++;
            if (pictureFaults < T09Pictures.FaultLimit) return;
            picturesStopped = true;
            Warn("pictures_stopped", pictureFaults + " pictures in a row failed; the start takes no more");
        }

        // Whether two pictures of one size differ anywhere on a coarse grid (by more than 0.002 in a channel, linear).
        private static bool Differs(Color[] a, Color[] b)
        {
            if (a.Length != b.Length) return true;
            int step = Math.Max(1, a.Length / 4096);
            for (int i = 0; i < a.Length; i += step)
            {
                if (Math.Abs(a[i].r - b[i].r) > 0.002f || Math.Abs(a[i].g - b[i].g) > 0.002f || Math.Abs(a[i].b - b[i].b) > 0.002f) return true;
            }
            return false;
        }

        // --- The clock --------------------------------------------------------------------------------------------------------------------

        private string utFailure;
        // The cover the clock aims at (the field's at 3 km): a covered sky that is not a closed sheet either (K-T2d start 45: 0.67 gave discrete cumulus, 0.47 a closed deck).
        private const double CoverTarget = 0.65;
        private bool utNarrow;

        // One universal time for every variant and view, chosen as K-T2d chooses its picks: the candidates are the hours from the fixed base (the next full hour of the clock at
        // the start) and a far window up to 10 days (SampleCoverCandidates), the field is asked at the site and 3 km without moving the clock, and the candidate whose cover is
        // nearest to CoverTarget (at least MinCover) with the sun in the narrow band 30 to 55 degrees is taken; the wide band 15 to 60 only where the narrow one holds none.
        // A higher sun gives the light K-T2d measured the pass at (start 45: 45.5 degrees), a lower one the warm sunset colours of start 44. The clock is set to each candidate
        // to read the sun's elevation (the cover is the field's, the sun the game's), at most MaxClockTries per band, and left at the chosen one.
        private IEnumerable<ProbeWait> ChooseUt(int bodyIndex, double lat, double lon)
        {
            utOk = false;
            utFailure = null;
            double start = Game.UniversalTime;
            foreach (ProbeWait w in SampleCoverCandidates(bodyIndex, lat, lon)) yield return w;
            Emit("T09.candidates", coverCandidates.Count);
            Emit("T09.candidates_far", candFarAnswered);
            var order = new List<CoverCandidate>();
            foreach (CoverCandidate c in coverCandidates)
            {
                if (c.Cover >= MinCover) order.Add(c);
            }
            // Nearest to the target first; the earlier time wins a tie, so that a start is repeatable.
            order.Sort((a, b) =>
            {
                int byCover = Math.Abs(a.Cover - CoverTarget).CompareTo(Math.Abs(b.Cover - CoverTarget));
                return byCover != 0 ? byCover : a.Ut.CompareTo(b.Ut);
            });
            int covered = order.Count;
            for (int band = 0; band < 2 && !utOk; band++)
            {
                double sunMin = band == 0 ? NarrowSunMinDeg : SunMinDeg, sunMax = band == 0 ? NarrowSunMaxDeg : SunMaxDeg;
                int tried = 0;
                foreach (CoverCandidate c in order)
                {
                    if (tried++ >= MaxClockTries) break;
                    ForceUniversalTime(c.Ut);
                    yield return ProbeWait.Frames(6);
                    double elevation = Game.SunElevationDeg();
                    if (elevation < sunMin || elevation > sunMax) continue;
                    utChosen = c.Ut;
                    coverChosen = c.Cover;
                    sunChosen = elevation;
                    utNarrow = band == 0;
                    utOk = true;
                    Emit("T09.ut_jump_s", c.Ut - start);
                    break;
                }
            }
            if (utOk)
            {
                if (candPasses > 1) AtmospheresLog.Say("T09 clock: the field answered after " + candWaitedS.ToString("0") + " s (" + candPasses + " passes)");
                yield break;
            }
            AtmospheresLog.Say("T09 clock: no candidate (" + candUnsampled + " not sampled, " + covered + " covered but not in the sun band, " + candPasses + " passes)");
            if (candUnsampled > 0)
            {
                IWeatherField field = WeatherHub.Field;
                utFailure = "the weather field did not answer at the site for " + candUnsampled + " of " + Candidates + " candidate times within " + FieldWaitS.ToString("0")
                            + " s (field " + (field != null ? field.GetType().Name + ", readiness " + field.Readiness : "none") + "; gates: " + CloudGates.Describe(AtmospheresSettings.Number)
                            + "; " + covered + " answered with cover and none of them stood in the sun band); the clock cannot be chosen";
            }
            else if (covered == 0)
            {
                utFailure = "no universal time in " + Candidates + " hours from the base or in the " + candFarAnswered + " later candidates up to 10 days has a cover of at least " + MinCover
                            + " over the site (" + coverCandidates.Count + " answered); the timing would not describe a covered sky";
            }
            else
            {
                utFailure = "no universal time with a cover of at least " + MinCover + " over the site has the sun " + SunMinDeg + " to " + SunMaxDeg + " degrees up (" + covered
                            + " of " + coverCandidates.Count + " candidates had the cover, " + (2 * Math.Min(covered, MaxClockTries)) + " were tried); the timing would not describe a covered daylight sky";
            }
        }

        // --- Placement --------------------------------------------------------------------------------------------------------------------

        private bool placeOk;
        // The variant in which the orbit_edge view could not be placed (the later variants leave it out), or null.
        private string orbitEdgeFailedIn;
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
                T09Pose placed = Game.ReadPose();
                double vesselAltitude = placed.VesselAsl;
                double az = placed.AzimuthDeg, el = placed.ElevationDeg;
                double altitudeError = Math.Abs(vesselAltitude - wantedAltitude), tolerance = Math.Max(AltitudeToleranceM, 0.02 * wantedAltitude);
                double aimError = Math.Abs(el - wantedPitch), headingError = Math.Abs(WrapDeg(az - view.HeadingDeg));
                // The altitude is the centre of mass's, which the hold now brings to the target within 5 m: a drift of more than 20 m is a placement that has to be repeated.
                bool altitudeOk = altitudeError <= tolerance && placed.DriftM <= PlacedDriftToleranceM, aimOk = aimError <= AimToleranceDeg && (Math.Abs(el) > 80.0 || headingError <= 5.0 * AimToleranceDeg);
                detail = "altitude " + vesselAltitude.ToString("0") + " m against " + wantedAltitude.ToString("0") + ", camera elevation " + el.ToString("0.0") + " against "
                         + wantedPitch.ToString("0.0") + ", heading " + az.ToString("0") + " against " + view.HeadingDeg.ToString("0") + ", " + Game.VesselState
                         + ", " + placed.VesselAgl.ToString("0") + " m over the terrain, camera " + placed.CameraAsl.ToString("0") + " m (" + placed.CameraAgl.ToString("0") + " over the terrain) "
                         + placed.CameraToVesselM.ToString("0") + " m from the vessel, " + placed.DriftM.ToString("0.0") + " m from the held point, mode " + Game.CameraModeText + (aimNote != null && !(altitudeOk && aimOk) ? ", aim: " + aimNote : "");
                AtmospheresLog.Say("T09 view placed: " + view.Name + " attempt " + (attempt + 1) + " of " + PlaceAttempts + ": " + detail + (altitudeOk && aimOk ? " (ok)" : " (not yet)"));
                string prefix = "T09.view." + view.Name + ".";
                Emit(prefix + "altitude_m", vesselAltitude);
                Emit(prefix + "elevation_deg", el);
                Emit(prefix + "azimuth_deg", az);
                placeOk = altitudeOk && aimOk;
            }
            if (placeOk) yield break;
            // K-T2 is a timing series: a view that cannot be placed fails it. K-T2d is a diagnosis: the frames are read where the vessel stands, flagged, and the start goes on.
            if (IsDiag) Warn("pose_placed_" + view.Name, detail + " (the readouts of this view are flagged pose_ok 0 and taken anyway)");
            else Check("T09.pose_placed_" + view.Name + "_" + CurrentVariant, false, detail);
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
            // The game applies the placement and refreshes the vessel's centre of mass a physics step later: the hold anchors only once the vessel stands at the placed point
            // (start 43 anchored the old position, 85 m). At most 150 more frames; the pose check that follows names a vessel that did not arrive.
            for (int i = 0; i < 30 && !Game.HoldReached; i++) yield return ProbeWait.Frames(5);
            // And stands there for a stretch of frames (start 44: the vessel left the hold by 200 to 500 m within a second of a view's placement check).
            for (int i = 0; i < 12 && Game.HoldReached && !Game.HoldStable; i++) yield return ProbeWait.Frames(5);
            AtmospheresLog.Say("T09 hold: " + Game.HoldStatus + " for the " + view.Name + " view (wanted " + wantedAltitude.ToString("0") + " m)");
            wantedPitch = view.PitchDeg;
            if (double.IsNaN(wantedPitch))
            {
                // The limb: the horizon lies below the horizontal by acos(R / (R + h)); the camera looks at it. On Kerbin (R 600 km) at 300 km that is 48.2 degrees,
                // not the 17.6 degrees of a planet the size of the Earth. The height is the one asked for, not a read of the vessel: a vessel that drifts must not
                // move the target with it.
                wantedPitch = -HorizonDipDeg(Game.BodyRadius, wantedAltitude);
            }
            yield return ProbeWait.Frames(12);
        }

        private static double WrapDeg(double d)
        {
            while (d > 180.0) d -= 360.0;
            while (d < -180.0) d += 360.0;
            return d;
        }

        // The angle below the horizontal at which the horizon lies for an eye at the height over a body of the radius (degrees, positive).
        internal static double HorizonDipDeg(double radius, double heightM)
        {
            return Math.Acos(radius / (radius + Math.Max(0.0, heightM))) * 180.0 / Math.PI;
        }

        // The aim of the last Aim call: the rounds it needed and, where it did not converge, why (named in the placement line and the failure).
        private int aimRounds;
        private string aimNote;
        private const int MaxAimRounds = 8, SettleMaxSteps = 40;
        private const double SettleStillDeg = 0.05, MaxAimStepDeg = 60.0;

        // Frames until the flight camera's aim has stopped moving (two reads two frames apart agree within SettleStillDeg), at most SettleMaxSteps x 2 frames. The
        // camera follows its heading and pitch with a lag, so an aim read right after a command is the old one; correcting from it winds up (start 42: elevations of
        // -86, -82, -81 and -75 degrees against -48.2 in four attempts).
        private IEnumerable<ProbeWait> SettleAim()
        {
            double az, el;
            Game.CameraAim(out az, out el);
            for (int i = 0; i < SettleMaxSteps; i++)
            {
                yield return ProbeWait.Frames(2);
                double az2, el2;
                Game.CameraAim(out az2, out el2);
                bool still = Math.Abs(WrapDeg(az2 - az)) < SettleStillDeg && Math.Abs(el2 - el) < SettleStillDeg;
                az = az2;
                el = el2;
                if (still) yield break;
            }
        }

        // The loop aims for AimGoalDeg (a camera that turns against the horizon by 0.0167 degrees a second in the orbital frame moves half a degree in a dwell); it
        // only reports a note where the error stays above half the tolerance.
        private const double CalibrationStepDeg = 15.0, AimGoalDeg = 0.75;
        // The result of Move: the aim after the settle and the move the game stored (it clamps the pitch).
        private double moveAz, moveEl, madeH, madeP;

        private IEnumerable<ProbeWait> Move(double headingStepDeg, double pitchStepDeg)
        {
            double oldH = Game.CameraHeading * 180.0 / Math.PI, oldP = Game.CameraPitch * 180.0 / Math.PI;
            Command(oldH + headingStepDeg, oldP + pitchStepDeg);
            foreach (ProbeWait w in SettleAim()) yield return w;
            Game.CameraAim(out moveAz, out moveEl);
            madeH = WrapDeg(Game.CameraHeading * 180.0 / Math.PI - oldH);
            madeP = Game.CameraPitch * 180.0 / Math.PI - oldP;
        }

        private void Command(double headingDeg, double pitchDeg)
        {
            Game.CameraHeading = (float)(headingDeg * Math.PI / 180.0);
            Game.CameraPitch = (float)(pitchDeg * Math.PI / 180.0);
        }

        // Turns the flight camera to the compass heading and elevation: it orbits the vessel and looks at it, so the vessel sits at the centre of the picture.
        // The camera is forced to FREE (above the atmosphere AUTO resolves to ORBITAL, whose heading axis is the inertial pole: at the equator a change of heading
        // moves the elevation, and the per-axis loop that learnt one sign per axis could not converge). The aim is then solved as two equations in two unknowns:
        // the response of the measured (azimuth, elevation) to the commanded (heading, pitch) is a 2 x 2 matrix, started from the convention of the ground views (the
        // camera at heading + 180 degrees, pitch minus the elevation) and corrected after every settled move (Broyden), so an inverted sign, swapped or mixed axes and a
        // lagging camera all converge, and a camera that does not respond is named. aimRounds and aimNote say what happened.
        private IEnumerable<ProbeWait> Aim(double azimuthDeg, double elevationDeg, double distance)
        {
            aimRounds = 0;
            aimNote = null;
            Game.SetCameraDistance(distance);
            Game.UseFreeCamera();
            double h = WrapDeg(azimuthDeg + 180.0), p = -elevationDeg;
            Command(h, p);
            foreach (ProbeWait w in SettleAim()) yield return w;
            double az, el;
            Game.CameraAim(out az, out el);
            double j11 = 1.0, j12 = 0.0, j21 = 0.0, j22 = -1.0;
            // Within the placement tolerance the convention is right (a small error is a lag or a limit): no calibration.
            bool inTolerance = Math.Abs(WrapDeg(azimuthDeg - az)) < AimToleranceDeg && Math.Abs(elevationDeg - el) < AimToleranceDeg;
            if (!inTolerance)
            {
                // The convention of the ground views did not give the aim: measure the response with one move of each axis before solving (an inverted or mixed axis
                // cannot be learnt from one rank-1 update; 4 rounds were not enough in the fake).
                double az0 = az, el0 = el;
                foreach (ProbeWait w in Move(CalibrationStepDeg, 0.0)) yield return w;
                if (Math.Abs(madeH) < 0.5 * CalibrationStepDeg)
                {
                    aimNote = "the game did not take the heading command (heading stayed, aim " + moveAz.ToString("0.0") + "/" + moveEl.ToString("0.0") + ")";
                    yield break;
                }
                j11 = WrapDeg(moveAz - az0) / madeH;
                j21 = (moveEl - el0) / madeH;
                double az1 = moveAz, el1 = moveEl;
                foreach (ProbeWait w in Move(0.0, CalibrationStepDeg)) yield return w;
                if (Math.Abs(madeP) < 0.5 * CalibrationStepDeg)
                {
                    // At the pitch limit: the other way.
                    az1 = moveAz;
                    el1 = moveEl;
                    foreach (ProbeWait w in Move(0.0, -CalibrationStepDeg)) yield return w;
                }
                if (Math.Abs(madeP) >= 0.5 * CalibrationStepDeg)
                {
                    j12 = WrapDeg(moveAz - az1) / madeP;
                    j22 = (moveEl - el1) / madeP;
                }
                az = moveAz;
                el = moveEl;
            }
            for (int round = 0; round < MaxAimRounds; round++)
            {
                bool useAz = Math.Abs(elevationDeg) < 80.0;
                double ez = useAz ? WrapDeg(azimuthDeg - az) : 0.0, ee = elevationDeg - el;
                if (Math.Abs(ez) < AimGoalDeg && Math.Abs(ee) < AimGoalDeg) break;
                double det = j11 * j22 - j12 * j21;
                if (Math.Abs(det) < 0.05)
                {
                    aimNote = "the camera's aim does not respond to heading and pitch (response matrix determinant " + det.ToString("0.000") + " after " + aimRounds + " rounds, aim "
                              + az.ToString("0.0") + "/" + el.ToString("0.0") + ")";
                    yield break;
                }
                double dh = (ez * j22 - j12 * ee) / det, dp = (j11 * ee - j21 * ez) / det;
                double big = Math.Max(Math.Abs(dh), Math.Abs(dp));
                if (big > MaxAimStepDeg)
                {
                    dh *= MaxAimStepDeg / big;
                    dp *= MaxAimStepDeg / big;
                }
                aimRounds++;
                foreach (ProbeWait w in Move(dh, dp)) yield return w;
                double az2 = moveAz, el2 = moveEl;
                // What the game stored (it clamps the pitch) is the move that was made.
                double n = madeH * madeH + madeP * madeP;
                if (n > 1e-9)
                {
                    // Broyden's rank-one update, only where the move answered unlike the model (a lagging, drifting camera answers within noise and must not spoil it).
                    double r2 = (el2 - el) - (j21 * madeH + j22 * madeP);
                    double r1 = useAz && Math.Abs(el2) < 80.0 ? WrapDeg(az2 - az) - (j11 * madeH + j12 * madeP) : 0.0;
                    double predicted = Math.Sqrt(Math.Pow(j11 * madeH + j12 * madeP, 2) + Math.Pow(j21 * madeH + j22 * madeP, 2));
                    if (Math.Sqrt(r1 * r1 + r2 * r2) > 0.2 * predicted + 0.5)
                    {
                        j21 += r2 * madeH / n;
                        j22 += r2 * madeP / n;
                        j11 += r1 * madeH / n;
                        j12 += r1 * madeP / n;
                    }
                }
                else
                {
                    aimNote = "the game did not take the command (heading and pitch stayed, aim " + az2.ToString("0.0") + "/" + el2.ToString("0.0") + ")";
                    yield break;
                }
                az = az2;
                el = el2;
            }
            double finalEz = Math.Abs(elevationDeg) < 80.0 ? Math.Abs(WrapDeg(azimuthDeg - az)) : 0.0, finalEe = Math.Abs(elevationDeg - el);
            if (finalEz >= AimToleranceDeg / 2.0 || finalEe >= AimToleranceDeg / 2.0)
            {
                aimNote = "after " + aimRounds + " rounds the aim is " + az.ToString("0.0") + "/" + el.ToString("0.0") + " against " + azimuthDeg.ToString("0.0") + "/" + elevationDeg.ToString("0.0");
            }
        }

        // --- The gains --------------------------------------------------------------------------------------------------------------------

        // The pass's sun colour over the scene's sun (LayerFrame.OutputWhite is the scene sun's luminance): sunUnit times the gain, whatever the weather writer did to the
        // scene's sun intensity (the sun factor of rain and sleet changes it from frame to frame).
        internal static float PassSunPerSceneSun(LayerFrame f)
        {
            return f.SunColour.grayscale / Math.Max(1e-4f, f.OutputWhite);
        }

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
            float sunBase = PassSunPerSceneSun(own.Frame), rawBase = own.Frame.SunColour.grayscale;
            ForceTuning(OwnCloudModule.TuneSunGain, 2f);
            yield return ProbeWait.Frames(15);
            float sunDouble = PassSunPerSceneSun(own.Frame), rawDouble = own.Frame.SunColour.grayscale;
            Emit("T09.gain.sun_ratio", sunBase > 1e-6f ? sunDouble / sunBase : 0.0);
            // The raw ratio of the two reads is the gain times the change of the scene's sun between them: start 46 read 2.307, the weather writer's sun factor having gone from 0.85
            // to 0.98 (a level flapping around the 0.04 threshold) in the two seconds between the reads (0.98 / 0.85 = 1.153, 2 * 1.153 = 2.307).
            Emit("T09.gain.sun_ratio_raw", rawBase > 1e-6f ? rawDouble / rawBase : 0.0);
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
