using System;
using System.Collections.Generic;
using System.Globalization;

namespace ReDefinition.Atmospheres.Viewer
{
    // One view of the solo series: a type of the stack drawn alone and the pose it is judged from, with the share of the frame it is expected to fill.
    internal struct SoloView
    {
        internal string Name;
        // The role of the layer (WeatherPresets.Role) and the type's exact name in the stack.
        internal WeatherPresets.Role Role;
        internal string TypeName;
        // The forced strength (a uniform field's cover share, a cell's peak), a cell and its distance ahead of the camera in km.
        internal float Strength;
        internal bool Cell;
        internal double CellAheadKm;
        // The pose: the vessel's height above the ground, the camera's elevation, its distance from the vessel and its compass heading RELATIVE TO THE SUN: the sun's measured
        // azimuth plus HeadingFromSunDeg (180: the sun stands straight behind the camera, the sunlit view of RE section 0.5; 0: the camera looks into the sun, a named backlit view).
        internal double AglM, ElevationDeg, HeadingFromSunDeg, CameraDistM;
        // A view that looks into the sun on purpose (a named backlit row); every other view is a sunlit one and fails when the camera looks within SunBehindMinDeg of the sun.
        internal bool Backlit { get { return Math.Abs(HeadingFromSunDeg) < 90.0; } }
        // The compass heading of the view for a measured sun azimuth.
        internal double HeadingFor(double sunAzimuthDeg) { return ViewerAim.Wrap(sunAzimuthDeg + HeadingFromSunDeg); }
        // The share of the frame the cloud is expected to fill (the frame without the vessel's box), and the share of the frame's height it reaches from its lowest to its highest row
        // (NaN: not judged).
        internal double ShareMin, ShareMax, SpanMin, SpanMax;
        // The reference expectation (RE-cloud-types-2026-11-15.md) section and checklist the picture is judged by.
        internal string Reference;
    }

    // The probe of series K-S1 (start "solo", rows tests/series/rows/SOLOPROBE.json): every type of the cloud stack that can be judged alone, drawn alone over a clear sky at a fixed
    // sun elevation, from the pose where it fills a stated share of the frame. The cloud viewer's solo control (CloudViewer.SetSolo) is the mechanism: it forces one layer's map, type
    // and strength, draws only that layer (cloud.soloLayer), switches the deck rule and the forced rain off and, for a type whose tower feeds an anvil, makes the map one cell ahead
    // of the camera. Per view the probe
    //   1. places the pose and waits until the camera stands still, selects the type, takes the BASELINE picture (the same solo with strength 0: the sky with every layer empty) and
    //      the solo picture, both by the viewer's own capture (the window is hidden through the frame);
    //   2. reads back from the game what isolates the thing: the layer mask the pass gave its shader is the layer's bit alone, every other layer's forced map reads 0 and its cover at
    //      the vessel is 0, no rain is forced, and in the baseline every layer reads 0 (the background is clear);
    //   3. measures the share of the frame the cloud fills and how far it reaches up and across it (PictureMask: the solo picture against the baseline, colour distance above 0.03),
    //      and fails where the share or the reach is outside the band stated for the view: an object that is not in view, or fills the whole frame, fails.
    // Before the first view the probe sets the sun's elevation (the viewer's time offset, searched in steps of a quarter of Kerbin's day), freezes the time and the clouds; at the end it
    // reads the sun again (it must not have moved) and closes the viewer (the solo keys must be gone). The pictures are for a person to judge by the checklist of the reference.
    [AtmospheresModule(991)]
    internal sealed class SoloProbe : SeriesProbe
    {
        internal const string Series = "K-S1";
        internal const string Prefix = "SOLO.";

        // The sun: the elevation the series is shot at, the band the search accepts, the offsets tried (a thirty-sixth of Kerbin's 21600 s day, 10 degrees of the sun's hour angle, which
        // is the width of the band) and how far it may move over the series.
        internal const double SunTargetDeg = 45.0, SunBandDeg = 6.0, SunStepS = 600.0, SunDriftDeg = 1.0;
        internal const int SunSteps = 36;
        // A forced map reads as this near (the pass's reader and the CPU mirror), and a layer reads as empty below this cover at the vessel.
        internal const double MapReadTolerance = 0.005, CoverEmpty = 0.02, CoverReach = 0.05;
        // Two baseline pictures of one pose differ by at most this share of the frame (the noise floor every share is measured above).
        internal const double NoiseFloorShare = 0.01;
        internal const double PollPlaceS = 60.0, PollMapS = 20.0, PollPictureS = 15.0, SettleS = 3.0, AimStillDeg = 0.05;
        // The pose the game reached: the camera's elevation within this of the view's, at least this high above the ground; Kerbin's radius for the horizon's row and the margin above it.
        internal const double PoseElevationDeg = 3.0, PoseHeadingDeg = 5.0, SunBehindMinDeg = 60.0, MinCameraAglM = 5.0, BodyRadiusM = 600000.0, HorizonMarginV = 0.01;
        internal const int SettleFrames = 60, AimStillMaxFrames = 80, MinPictureBytes = 1000;

        // The views. The pose and the bands are stated before the start (the start review): a uniform field at the cover share it is forced at, seen from the ground at the elevation that
        // puts the layer across the frame; the cell ahead of the camera so that its tower and the anvil it spreads fill the frame (cb_near, 25 km) or stand on the horizon (cb_far, 40 km: the far end of the side view of RE section 1.2, CB-S 10-40 km).
        internal static readonly SoloView[] Views =
        {
            Cell("cb_near", "Cumulonimbus incus", 25.0, 200.0, 15.0, 0.40, 0.90, 0.40, 1.0, "section 1 (CB-G, CB-S): tower flanks, flat dark base, anvil spreading overhead; checklist 1-4, 8"),
            Cell("cb_far", "Cumulonimbus incus", 40.0, 300.0, 5.0, 0.04, 0.85, 0.20, 0.80, "section 1 (CB-S, side view 10-40 km): the whole cell on the horizon, anvil flat at the top; checklist 1-5, 8"),
            Field("sc", WeatherPresets.Role.Cumulus, "Stratocumulus", 0.8f, 200.0, 45.0, 0.35, 1.0, "section 2 (SC-G): lumps and gaps, dark parts, no filament"),
            Field("humilis", WeatherPresets.Role.Cumulus, "Cumulus humilis", 0.5f, 200.0, 35.0, 0.15, 0.90, "section 5 (CU-G): detached, flat dark base, sharp outline, flat and low"),
            Field("mediocris", WeatherPresets.Role.Cumulus, "Cumulus mediocris", 0.5f, 200.0, 35.0, 0.30, 0.97, "section 5 (CU-G): detached, cauliflower tops, about as tall as wide"),
            Field("congestus", WeatherPresets.Role.Cumulus, "Cumulus congestus", 0.4f, 200.0, 35.0, 0.40, 1.0, "section 5 (CU-G): towering above the field of view, sharp outline"),
            Field("altocumulus", WeatherPresets.Role.Cumulus, "Altocumulus", 0.6f, 200.0, 50.0, 0.20, 0.95, "section 6 (AC-G): patch or layer of rounded elements, regular order"),
            Field("cirrus", WeatherPresets.Role.Cirrus, "Cirrus", 0.5f, 200.0, 55.0, 0.03, 0.85, "section 3 (CI-G): white delicate filaments, thin, the sky visible through"),
            Field("cirrostratus", WeatherPresets.Role.Cirrus, "Cirrostratus", 1.0f, 200.0, 55.0, 0.05, 1.0, "section 3 (CI-G): a thin whitish veil over the sky"),
            Field("stratus", WeatherPresets.Role.Deck, "Stratus", 1.0f, 90.0, 45.0, 0.70, 1.0, "section 4 (NS-G): a grey layer of fairly uniform base"),
            Field("nimbostratus", WeatherPresets.Role.Deck, "Nimbostratus", 1.0f, 90.0, 45.0, 0.85, 1.0, "section 4 (NS-G): featureless dark grey ceiling")
        };

        private static SoloView Cell(string name, string type, double aheadKm, double aglM, double elevationDeg, double shareMin, double shareMax, double spanMin, double spanMax, string reference)
        {
            return new SoloView
            {
                Name = name, Role = WeatherPresets.Role.Cumulonimbus, TypeName = type, Strength = 1f, Cell = true, CellAheadKm = aheadKm, AglM = aglM, ElevationDeg = elevationDeg, HeadingFromSunDeg = 180.0, CameraDistM = 100.0,
                ShareMin = shareMin, ShareMax = shareMax, SpanMin = spanMin, SpanMax = spanMax, Reference = reference
            };
        }

        private static SoloView Field(string name, WeatherPresets.Role role, string type, float strength, double aglM, double elevationDeg, double shareMin, double shareMax, string reference)
        {
            return new SoloView
            {
                Name = name, Role = role, TypeName = type, Strength = strength, Cell = false, AglM = aglM, ElevationDeg = elevationDeg, HeadingFromSunDeg = 180.0, CameraDistM = 100.0,
                ShareMin = shareMin, ShareMax = shareMax, SpanMin = double.NaN, SpanMax = double.NaN, Reference = reference
            };
        }

        private ISoloRig rig;
        private bool reached;

        internal ISoloRig Rig
        {
            get { return rig ?? (rig = new GameSoloRig()); }
            set { rig = value; }
        }

        protected override string SeriesName
        {
            get { return Series; }
        }

        protected override double MaxSeconds
        {
            get { return 15 * 60; }
        }

        protected override string LostGround()
        {
            if (!reached) return null;
            return Rig.FlightReady ? null : "the flight scene was lost during the scenario";
        }

        private static string F(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private bool Verdict(string name, string what, bool ok, string expected, string read)
        {
            LogLine("solo: " + what + ": expected " + expected + ", read " + read + (ok ? " - ok" : " - FAILED"));
            Emit(Prefix + name + "_ok", ok);
            Check(Prefix + name, ok, ok ? null : "expected " + expected + ", read " + read);
            return ok;
        }

        private void Info(string name, double value)
        {
            Emit(Prefix + name, value);
        }

        private bool polled;

        private IEnumerable<ProbeWait> Poll(Func<bool> condition, double seconds)
        {
            double end = Rig.RealS + seconds;
            while (!condition() && Rig.RealS < end) yield return ProbeWait.Frames(1);
            polled = condition();
        }

        private static double Circular(double a, double b)
        {
            double d = Math.Abs(a - b) % 360.0;
            return d > 180.0 ? 360.0 - d : d;
        }

        // Waits until the camera's aim stands still (two readings two frames apart within AimStillDeg).
        private IEnumerable<ProbeWait> StillAim()
        {
            ViewerPoseReading last = Rig.Pose;
            for (int i = 0; i < AimStillMaxFrames; i++)
            {
                yield return ProbeWait.Frames(2);
                ViewerPoseReading now = Rig.Pose;
                bool still = Circular(last.AzimuthDeg, now.AzimuthDeg) <= AimStillDeg && Math.Abs(last.ElevationDeg - now.ElevationDeg) <= AimStillDeg;
                last = now;
                if (still) yield break;
            }
        }

        // One picture by the viewer's capture; the file name is in taken (null where none was written).
        private string taken;

        private IEnumerable<ProbeWait> Take()
        {
            taken = null;
            string before = Rig.LastCapture;
            Rig.RequestCapture();
            yield return ProbeWait.Frames(1);
            int guard = 0;
            while (Rig.Capturing && guard++ < 120) yield return ProbeWait.Frames(1);
            foreach (ProbeWait w in Poll(() => Rig.LastCapture != before && Rig.LastCapture.Length > 0 && Rig.FileSize(Rig.CapturePicturePath(Rig.LastCapture)) > MinPictureBytes, PollPictureS)) yield return w;
            if (polled) taken = Rig.LastCapture;
            // The file is written asynchronously: a few frames more so that a reader does not get half of it.
            yield return ProbeWait.Frames(5);
        }

        private static SoloEntry? Find(IList<SoloEntry> entries, SoloView v)
        {
            foreach (SoloEntry e in entries)
            {
                var layer = new ViewerLayer { Name = e.LayerName, Group = e.Group };
                if (WeatherPresets.RoleOf(layer) == v.Role && string.Equals(e.TypeName, v.TypeName, StringComparison.OrdinalIgnoreCase)) return e;
            }
            return null;
        }

        // The map values and the covers the pass holds, read back from the game: whether every layer other than `layer` is empty (forced 0, cover 0 at the vessel) and, as the
        // solo layer, what its forced map reads. The text names the first layer that is not.
        private bool OthersEmpty(IList<ViewerLayer> layers, int layer, out string reason)
        {
            reason = "";
            for (int l = 0; l < layers.Count; l++)
            {
                if (l == layer) continue;
                float forced = Rig.PassMapOverride(layers[l].Name);
                float cover = Rig.LayerCover(l);
                if (Math.Abs(forced) > MapReadTolerance || float.IsNaN(cover) || cover > CoverEmpty)
                {
                    reason = layers[l].Name + ": forced map " + F(forced) + ", cover " + F(cover);
                    return false;
                }
            }
            return true;
        }

        private bool OthersEmptyNow(IList<ViewerLayer> layers, int layer)
        {
            string ignored;
            return OthersEmpty(layers, layer, out ignored);
        }

        private bool NoRainForced()
        {
            return Math.Abs(Rig.Setting(AtmospheresSettings.Keys.ForceLevel)) < 1e-6;
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            SetSetting(AtmospheresSettings.Keys.CloudSource, (double)(int)CloudSourceKind.Ours);
            SetSetting(AtmospheresSettings.Keys.OwnClouds, 1.0);
            // The layers' bases are the cfg's: no humidity physics, no variation, no ragged boundary (a deck's base would move by up to 500 m and the camera could stand inside it).
            SetSetting(AtmospheresSettings.Keys.CloudBasePhysics, 0.0);
            SetSetting(AtmospheresSettings.Keys.CloudBaseVariation, 0.0);
            SetSetting(AtmospheresSettings.Keys.BaseRagged, 0.0);
            yield return ProbeWait.Until(() => Rig.FlightReady && Rig.PassDrawing, 300, "the flight scene and the own cloud pass drawing");
            reached = true;
            // The vessel is counted once it is loaded and unpacked (the viewer places nothing before) and again at the end.
            foreach (ProbeWait w in WaitVesselReady()) yield return w;
            IList<ViewerLayer> layers = Rig.Layers;
            Info("layers", layers.Count);
            double forceLevel0 = Rig.Setting(AtmospheresSettings.Keys.ForceLevel), forceType0 = Rig.Setting(AtmospheresSettings.Keys.ForceType);

            // --- The viewer, the clouds held, the sun ---
            Rig.ToggleWindow();
            foreach (ProbeWait w in Poll(() => Rig.WindowOpen && Rig.SessionActive, 10.0)) yield return w;
            if (!Verdict("window_opens", "the viewer opens", polled, "window and session within 10 s", "window " + Rig.WindowOpen + ", session " + Rig.SessionActive)) yield break;
            Rig.FreezeClouds(true);
            // The vessel is placed and held before the clock jumps (K-T2 sets the time before it places; the viewer's hold keeps the vessel on the turning ground).
            SoloView firstView = Views[0];
            Rig.PlacePose(new ViewerPose { Name = firstView.Name, AglM = firstView.AglM, ElevationDeg = firstView.ElevationDeg, HeadingDeg = 90.0, CameraDistM = firstView.CameraDistM });
            yield return ProbeWait.Frames(2);
            foreach (ProbeWait w in Poll(() => !Rig.PlacementPending && Rig.HoldStable, PollPlaceS)) yield return w;
            // The first offset at which the sun stands in the band and goes down (afternoon). The camera's heading of every view is then taken from the sun's measured azimuth.
            double sun = double.NaN, previous = double.NaN;
            for (int k = 0; k <= SunSteps && double.IsNaN(sun); k++)
            {
                Rig.SetTimeOffset(k * SunStepS);
                yield return ProbeWait.Seconds(1.5);
                double e = Rig.SunElevationDeg;
                if (!double.IsNaN(e) && !double.IsNaN(previous) && Math.Abs(e - SunTargetDeg) <= SunBandDeg && e < previous) sun = e;
                previous = e;
            }
            bool sunFound = !double.IsNaN(sun);
            Verdict("sun_found", "an offset of the time puts the sun at the stated elevation", sunFound, F(SunTargetDeg) + " +-" + F(SunBandDeg) + " deg, going down, within a day", sunFound ? F(sun) + " deg" : "none of " + SunSteps + " offsets");
            if (!sunFound) yield break;
            Rig.FreezeTime(true);
            yield return ProbeWait.Seconds(1);
            Info("sun_elevation_deg", sun);
            sunAzimuth = Rig.SunAzimuthDeg;
            Info("sun_azimuth_deg", sunAzimuth);
            if (!Verdict("sun_azimuth", "the sun's compass azimuth is read, so that every view can be turned away from it", !double.IsNaN(sunAzimuth), "a number", F(sunAzimuth))) yield break;
            IList<SoloEntry> entries = Rig.SoloEntries;
            Info("solo_types", entries.Count);
            Info("fov_deg", Rig.FieldOfViewDeg);

            int viewsRun = 0;
            bool first = true;
            foreach (SoloView v in Views)
            {
                foreach (ProbeWait w in RunView(v, layers, entries, first)) yield return w;
                if (done) viewsRun++;
                first = false;
            }
            Verdict("views_all_run", "every view of the table ran to its picture", viewsRun == Views.Length, Views.Length + " views", viewsRun + " views");

            // --- The sun stood still; the viewer closes and the solo keys are gone ---
            double sunEnd = Rig.SunElevationDeg;
            Verdict("sun_fixed", "the sun's elevation did not move over the series", !double.IsNaN(sunEnd) && Math.Abs(sunEnd - sun) <= SunDriftDeg, F(sun) + " +-" + F(SunDriftDeg) + " deg", F(sunEnd) + " deg");
            Rig.ToggleWindow();
            foreach (ProbeWait w in Poll(() => !Rig.WindowOpen && !Rig.SessionActive, 10.0)) yield return w;
            yield return ProbeWait.Frames(10);
            bool gone = !Rig.HasOverride(CloudLookTuning.TuneSoloLayer) && !Rig.HasOverride(CloudLookTuning.TuneCellKm) && !Rig.HasOverride(CloudLookTuning.TuneCoverCombine)
                        && !Rig.HasOverride(CloudMotion.TuneScale);
            foreach (ViewerLayer l in layers) gone &= !Rig.HasOverride(CloudLookTuning.TuneMapOverride + l.Name) && !Rig.HasOverride(CloudLookTuning.TuneTypeOverride + l.Name) && !Rig.HasOverride(CloudLookTuning.TuneDeckRule + l.Name);
            bool settingsBack = Math.Abs(Rig.Setting(AtmospheresSettings.Keys.ForceLevel) - forceLevel0) < 1e-6 && (int)Rig.Setting(AtmospheresSettings.Keys.ForceType) == (int)forceType0;
            Verdict("restore_options", "the viewer's close takes the solo keys and the forced rain back", polled && gone && settingsBack, "no solo override left, forceLevel " + F(forceLevel0),
                    "window closed " + polled + ", overrides gone " + gone + ", forceLevel " + F(Rig.Setting(AtmospheresSettings.Keys.ForceLevel)));
            yield return ProbeWait.Seconds(AfterCloseWaitS);
            JudgeVesselIntact(Prefix);
            Info("elapsed_s", Elapsed);
        }

        // Whether the last view reached its picture (the count the scenario judges at the end).
        private bool done;
        // The sun's measured azimuth (the time is frozen: it stays), which every view's heading is taken from.
        private double sunAzimuth = double.NaN;
        // The share two pictures of this view's empty frame differ in.
        private double floorShare = double.NaN;
        internal const double NoiseFloorFactor = 3.0;

        // The lower bound of a share: the band's, or the noise floor of the view times NoiseFloorFactor where that is higher (an unread floor leaves the band's).
        internal static double EffectiveShareMin(double bandMin, double floor)
        {
            return double.IsNaN(floor) ? bandMin : Math.Max(bandMin, NoiseFloorFactor * floor);
        }

        // Whether the angle between the camera's direction and the sun's suits the view: a sunlit view looks more than SunBehindMinDeg away from the sun, a backlit one less.
        internal static bool SunAngleSuits(bool backlit, double angleDeg)
        {
            if (double.IsNaN(angleDeg)) return false;
            return backlit ? angleDeg <= SunBehindMinDeg : angleDeg > SunBehindMinDeg;
        }

        private IEnumerable<ProbeWait> RunView(SoloView v, IList<ViewerLayer> layers, IList<SoloEntry> entries, bool first)
        {
            done = false;
            SoloEntry? found = Find(entries, v);
            Verdict(v.Name + "_type_found", v.Name + ": the type is in the stack", found.HasValue, v.Role + " / " + v.TypeName, found.HasValue ? found.Value.LayerName + " / " + found.Value.TypeName : "absent");
            if (!found.HasValue) yield break;
            SoloEntry entry = found.Value;
            int bit = 1 << entry.Layer;

            // The pose, then the type alone: the cell's centre is ahead of the camera as it stands.
            double heading = v.HeadingFor(sunAzimuth);
            Rig.PlacePose(new ViewerPose { Name = v.Name, AglM = v.AglM, ElevationDeg = v.ElevationDeg, HeadingDeg = heading, CameraDistM = v.CameraDistM });
            yield return ProbeWait.Frames(2);
            foreach (ProbeWait w in Poll(() => !Rig.PlacementPending && Rig.HoldStable, PollPlaceS)) yield return w;
            yield return ProbeWait.Frames(SettleFrames);
            foreach (ProbeWait w in StillAim()) yield return w;
            // The pose the frame shares are stated for, read from the game: the camera looks where the view says and stands above the ground (a camera under the terrain is lifted by the game
            // and looks elsewhere). The horizon's row follows from the measured aim; the rows below it are the ground, which the measure leaves out.
            ViewerPoseReading pose = Rig.Pose;
            // Heading as well as elevation: a camera that looks the other way (K-S1 start 1: 180 degrees round, the sun in the frame, the cell behind the aim) is not at the pose.
            bool poseOk = Math.Abs(pose.ElevationDeg - v.ElevationDeg) <= PoseElevationDeg && Circular(pose.AzimuthDeg, heading) <= PoseHeadingDeg && pose.CameraAglM >= MinCameraAglM;
            Verdict(v.Name + "_pose", v.Name + ": the camera stands above the ground and looks where the view says", poseOk, "elevation " + F(v.ElevationDeg) + " +-" + F(PoseElevationDeg) + " deg, heading " + F(heading) + " +-" + F(PoseHeadingDeg)
                    + " deg (the sun's azimuth " + F(sunAzimuth) + " + " + F(v.HeadingFromSunDeg) + "), camera at least " + F(MinCameraAglM) + " m over the ground",
                    "elevation " + F(pose.ElevationDeg) + " deg, heading " + F(pose.AzimuthDeg) + " deg, camera " + F(pose.CameraAglM) + " m over the ground" + (Rig.AimNote != null ? "; " + Rig.AimNote : ""));
            // The angle between where the camera looks and the sun, read from the game's own vectors: the sunlit views must not look into the sun (K-S1 start 1 did, 180 degrees round).
            double sunAngle = Rig.CameraSunAngleDeg;
            Info(v.Name + "_sun_angle_deg", sunAngle);
            Verdict(v.Name + "_sun_angle", v.Name + ": the camera looks " + (v.Backlit ? "towards the sun (a named backlit view)" : "away from the sun (a sunlit view)"), SunAngleSuits(v.Backlit, sunAngle),
                    v.Backlit ? "at most " + F(SunBehindMinDeg) + " deg between the camera's direction and the sun" : "more than " + F(SunBehindMinDeg) + " deg between the camera's direction and the sun", F(sunAngle) + " deg");
            double skyFromV = Math.Max(0.0, Math.Min(1.0, PictureMask.HorizonRow(pose.ElevationDeg, Rig.FieldOfViewDeg, pose.CameraAglM, BodyRadiusM) + HorizonMarginV));
            Rig.SetSoloCellAhead(v.CellAheadKm > 0.0 ? v.CellAheadKm : CloudViewer.SoloCellDefaultKm);
            Rig.SetSolo(entry.Layer, entry.Type);
            Rig.SetSoloStrength(0f);

            // The baseline: the same solo with strength 0, so that the frame is the sky of this pose and sun with every layer empty.
            foreach (ProbeWait w in Poll(() => Math.Abs(Rig.PassMapOverride(entry.LayerName)) <= MapReadTolerance && Rig.PassLayerMask == bit && OthersEmptyNow(layers, entry.Layer) && Rig.LayerCover(entry.Layer) <= CoverEmpty, PollMapS)) yield return w;
            bool baselineMapped = polled;
            yield return ProbeWait.Frames(SettleFrames);
            yield return ProbeWait.Seconds(SettleS);
            string reason = "";
            bool clear = baselineMapped;
            if (!baselineMapped) reason = "the solo layer's map did not read 0 or the pass's layer mask is not the layer's";
            else if (!OthersEmpty(layers, entry.Layer, out reason)) clear = false;
            else if (Rig.LayerCover(entry.Layer) > CoverEmpty)
            {
                clear = false;
                reason = entry.LayerName + ": cover " + F(Rig.LayerCover(entry.Layer));
            }
            Verdict(v.Name + "_background_clear", v.Name + ": the baseline frame has every layer empty", clear && NoRainForced(), "every layer's forced map 0, cover 0 at the vessel, no rain forced",
                    clear ? "forceLevel " + F(Rig.Setting(AtmospheresSettings.Keys.ForceLevel)) : reason);
            foreach (ProbeWait w in Take()) yield return w;
            string baseline = taken;
            if (baseline == null)
            {
                Verdict(v.Name + "_baseline_taken", v.Name + ": the baseline picture is written", false, "a picture of more than " + MinPictureBytes + " bytes", "none");
                yield break;
            }
            // The noise floor of this view: a second picture of the same empty frame. The first view's must be next to nothing (the floor every share is measured above); every
            // view's own floor lifts the lower bound of its share (NoiseFloorFactor times it), so that a band whose floor lies near the noise cannot pass on the noise alone.
            foreach (ProbeWait w in Take()) yield return w;
            MaskReading floor = default(MaskReading);
            bool floorRead = taken != null && Rig.MeasurePictures(taken, baseline, skyFromV, out floor);
            floorShare = floorRead ? floor.Share : double.NaN;
            Info(v.Name + "_noise_floor_share", floorShare);
            if (first)
            {
                Verdict("noise_floor", "two pictures of the same empty frame differ by next to nothing", floorRead && floor.Share <= NoiseFloorShare, "at most " + F(NoiseFloorShare) + " of the frame", floorRead ? F(floor.Share) : "a picture could not be read");
                Info("noise_floor_share", floorShare);
            }

            // The solo picture: the type at its strength.
            Rig.SetSoloStrength(v.Strength);
            foreach (ProbeWait w in Poll(() => Math.Abs(Rig.PassMapOverride(entry.LayerName) - v.Strength) <= MapReadTolerance && Rig.PassLayerMask == bit && OthersEmptyNow(layers, entry.Layer), PollMapS)) yield return w;
            bool soloMapped = polled;
            if (!v.Cell)
            {
                foreach (ProbeWait w in Poll(() => Math.Abs(Rig.LayerCover(entry.Layer) - v.Strength) <= CoverReach, PollMapS)) yield return w;
                soloMapped &= polled;
            }
            yield return ProbeWait.Frames(SettleFrames);
            yield return ProbeWait.Seconds(SettleS);
            string othersReason;
            bool others = OthersEmpty(layers, entry.Layer, out othersReason);
            SoloSelection sel = Rig.Solo;
            int mask = Rig.PassLayerMask;
            bool isolated = soloMapped && mask == bit && others && NoRainForced() && sel.On && sel.Layer == entry.Layer && sel.Type == entry.Type && sel.Cell == v.Cell;
            Verdict(v.Name + "_isolated", v.Name + ": only this layer and type is drawn", isolated, "the pass's layer mask " + bit + ", forced map " + F(v.Strength) + ", every other layer 0, no forced rain",
                    "mask " + mask + ", forced map " + F(Rig.PassMapOverride(entry.LayerName)) + ", solo " + (sel.On ? sel.Layer + "/" + sel.Type + (sel.Cell ? " as a cell" : " as a uniform field") : "off")
                    + (soloMapped ? "" : ", the layer's cover at the vessel stood at " + F(Rig.LayerCover(entry.Layer)) + " (" + (v.Cell ? "the cell's map was not read" : "a uniform field reads its strength everywhere") + ")")
                    + (others ? "" : ", " + othersReason) + ", forceLevel " + F(Rig.Setting(AtmospheresSettings.Keys.ForceLevel)));
            foreach (ProbeWait w in Take()) yield return w;
            string picture = taken;
            MaskReading m = default(MaskReading);
            bool measured = picture != null && Rig.MeasurePictures(picture, baseline, skyFromV, out m);
            Verdict(v.Name + "_picture", v.Name + ": the solo picture is written and can be read against the baseline", measured, "a picture of more than " + MinPictureBytes + " bytes, readable", picture ?? "none");
            LogLine("solo: " + v.Name + ": baseline " + baseline + ", picture " + picture + ", pose " + F(v.AglM) + " m, elevation " + F(v.ElevationDeg) + " deg, heading " + F(heading) + " deg, sun angle " + F(sunAngle) + " deg, field of view "
                    + F(Rig.FieldOfViewDeg) + " deg; reference " + v.Reference);
            if (!measured) yield break;
            Info(v.Name + "_share", m.Share);
            Info(v.Name + "_row_span", m.RowSpan);
            double lower = EffectiveShareMin(v.ShareMin, floorShare);
            Verdict(v.Name + "_share", v.Name + ": the cloud fills the stated share of the frame", m.Share >= lower && m.Share <= v.ShareMax,
                    F(lower) + " (the band's " + F(v.ShareMin) + ", or " + F(NoiseFloorFactor) + " times this view's noise floor " + F(floorShare) + ") to " + F(v.ShareMax) + " of the frame",
                    F(m.Share) + " (" + m.Masked + " of " + m.Counted + " pixels)");
            if (!double.IsNaN(v.SpanMin))
                Verdict(v.Name + "_span", v.Name + ": the cloud reaches the stated share of the frame's height", m.RowSpan >= v.SpanMin && m.RowSpan <= v.SpanMax, F(v.SpanMin) + " to " + F(v.SpanMax) + " of the height",
                        F(m.RowSpan) + " (rows " + F(m.RowLow) + " to " + F(m.RowHigh) + " from the bottom)");
            done = true;
        }
    }
}
