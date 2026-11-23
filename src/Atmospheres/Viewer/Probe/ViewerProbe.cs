using System;
using System.Collections.Generic;
using System.Globalization;

namespace ReDefinition.Atmospheres.Viewer
{
    // The probe of series K-V1 (start "viewer", rows tests/series/rows/VPROBE.json): does the in-game cloud viewer work in the running game? It drives the viewer through the
    // calls the window's controls make (CloudViewerWindow is a thin layer over CloudViewer, which holds the session and decides; a mouse click is not simulated) and reads the
    // effect back where the game shows it, not in the viewer's own numbers:
    //   a. the window: Toggle (the call of the hotkey and of the settings window's button) opens it and starts the session, a second Toggle ends it, a third opens it again;
    //      the key list holds the registry's keys and the per-layer keys of the stack that stands;
    //   b. the A/B flip: a tuning key (cloud.exposure) and a setting (cloudSoftness) are set, stored as A and changed; the flip puts the stored values back in the table, the
    //      own pass's frame reads them (LayerFrame.OutputExposure) and the read check sees the pass read the key in the frame just run;
    //   c. the altitude control: the ground pose, the log slider's value and a typed height put the vessel where they say (height above the terrain) and keep the camera's aim;
    //   d. the cloud speed (cloud.motionScale): the noise clock and the layers' drift of the own pass advance by that many times the real time, freeze at 0 and resume;
    //   e. the time controls (offset, freeze, resume) against the game's own clock and the sun's elevation, and the forced weathers against the pass's own map readers
    //      (cloud.mapOverride.<layer>, the CPU mirror of the layer's cover) and the settings forceLevel and forceType;
    //   f. the capture: the picture and its sidecar are written, the window stays hidden through the frame the screen is read in and the sidecar names what was set.
    // Then the window is closed and everything it changed has to be back. Every check logs "viewer: <what>: expected ..., read ... - ok|FAILED" and the probe fails itself.
    // What it cannot prove: how the window looks and whether its controls respond to the mouse (one screenshot of the open window is saved for a person to look at), the
    // hotkey (it has no default binding), and the look of the forced weathers or of the picture.
    [AtmospheresModule(992)]
    internal sealed partial class ViewerProbe : SeriesProbe
    {
        internal const string Series = "K-V1";
        internal const string Prefix = "VIEWER.";

        // The tolerances of the checks (they are in tests/series rows' notes and in the start review).
        internal const double AltitudeAbsM = 15.0, AltitudeRel = 0.015, CameraDistRel = 0.25, CameraDistAbsM = 15.0, AimAzimuthDeg = 5.0, AimElevationDeg = 3.0;
        internal const double PhysicsRatioMin = 0.85, PhysicsRatioMax = 1.15, ResumedShare = 0.5, PhysicsWindowMinS = 1.0;
        internal const double UtAbsS = 2.0, FreezeHeldS = 0.25, SunMovesDeg = 3.0, OffsetSeconds = 5400.0;
        internal const double FlowRateMin = 0.5, FlowRateMax = 1.5, SpeedScale = 4.0, RatioMin = 3.4, RatioMax = 4.6, FrozenFlowS = 0.001;
        internal const double DriftMeasurableM = 0.5, DriftRatioMin = 3.0, DriftRatioMax = 5.0, DriftFrozenShare = 0.1;
        internal const double ExposureStep = 0.75, ExposureEps = 1e-4, CoverHigh = 0.7, CoverLow = 0.2, CoverRise = 0.15;
        internal const double RestoreVesselM = 40.0, SidecarUtS = 5.0, SidecarAglM = 25.0;
        internal const double MeasureS = 3.0, PollMapS = 60.0, PollPlaceS = 60.0, PollShortS = 10.0;
        internal const int MinKeys = 100, MinPictureBytes = 1000, SettleFrames = 30;
        // The aim is read once it stands still (two readings two frames apart within AimStillDeg, at most AimStillMaxFrames frames); the keep-aim check sets this aim by hand first.
        internal const double AimStillDeg = 0.05, KeepAimHeadingDeg = 200.0, KeepAimElevationDeg = 7.0, TurnedAwayDeg = 30.0, TurnedAwayElevationDeg = 5.0;
        internal const int AimStillMaxFrames = 80;
        // The game's clock against the start's UT plus the physics seconds the game ran since (Rig.PhysicsTimeS: the game's time runs on physics steps of at most 0.04 s a frame,
        // so at a low frame rate it falls behind the real time; the line prints both). The band is a few physics steps and the frame the clock was read in.
        internal const double RestoreTimeS = 3.0;
        // A precipitation level above this at the pad: the low stratiform layer's rain floor keeps the Fair cover up.
        internal const double RainAtPadLevel = 0.01;

        // The game the scenario reads and drives; a test replaces it with a fake rig. Made when the scenario first asks (every start of the game makes this module, only the
        // series' start uses it).
        private IViewerRig rig;
        private bool reached;

        internal IViewerRig Rig
        {
            get { return rig ?? (rig = new GameViewerRig()); }
            set { rig = value; }
        }

        protected override string SeriesName
        {
            get { return Series; }
        }

        protected override double MaxSeconds
        {
            get { return 10 * 60; }
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

        // One check: the row VIEWER.<name>_ok, the log line with what was expected and what was read, and the probe's own verdict.
        private bool Verdict(string name, string what, bool ok, string expected, string read)
        {
            LogLine("viewer: " + what + ": expected " + expected + ", read " + read + (ok ? " - ok" : " - FAILED"));
            Emit(Prefix + name + "_ok", ok);
            Check(Prefix + name, ok, ok ? null : "expected " + expected + ", read " + read);
            return ok;
        }

        private bool Near(string name, string what, double expected, double read, double tolerance)
        {
            bool ok = !double.IsNaN(read) && Math.Abs(read - expected) <= tolerance;
            return Verdict(name, what, ok, F(expected) + " +-" + F(tolerance), F(read));
        }

        private void Info(string name, double value)
        {
            Emit(Prefix + name, value);
        }

        // The frames until a condition holds or the real seconds are over; the answer is in polled (a timeout is a verdict of its own, not the end of the probe, so that one
        // start returns every row it can).
        private bool polled;

        private IEnumerable<ProbeWait> Poll(Func<bool> condition, double seconds)
        {
            double end = Rig.RealS + seconds;
            while (!condition() && Rig.RealS < end) yield return ProbeWait.Frames(1);
            polled = condition();
        }

        // A check that cannot tell here (the field's own weather hides the effect): logged and counted as information, never a failure.
        private void Unclear(string what, string expected, string read, string why)
        {
            LogLine("viewer: " + what + ": expected " + expected + ", read " + read + " - UNCLEAR (" + why + ")");
            // The runner collects these lines into the report's warnings: a green table with a check that did not run is not silent.
            LogLine("Probe: warning " + what + ": not judged, " + why);
        }

        // Waits until the camera's aim stands still: two readings two frames apart within AimStillDeg (the camera follows with a lag, as K-T2's SettleAim waits for).
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

        private static double Circular(double a, double b)
        {
            double d = Math.Abs(a - b) % 360.0;
            return d > 180.0 ? 360.0 - d : d;
        }

        private static ViewerPose PoseNamed(IList<ViewerLayer> layers, string name)
        {
            foreach (ViewerPose p in PosePresets.For(layers)) if (p.Name == name) return p;
            return new ViewerPose { Name = name };
        }

        private static ViewerLayer? FirstWithRole(IList<ViewerLayer> layers, WeatherPresets.Role role, out int index)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                if (WeatherPresets.RoleOf(layers[i]) == role)
                {
                    index = i;
                    return layers[i];
                }
            }
            index = -1;
            return null;
        }

        // The side of a sidecar's "key = value" lines (the scene's, then the settings' and the tuning keys' that differ from their defaults); the first of a key wins.
        internal static Dictionary<string, string> ParseSidecar(string text)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (text == null) return map;
            foreach (string raw in text.Split('\n'))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string key = raw.Substring(0, eq).Trim();
                string value = raw.Substring(eq + 1);
                int comment = value.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0) value = value.Substring(0, comment);
                if (key.Length > 0 && !map.ContainsKey(key)) map[key] = value.Trim();
            }
            return map;
        }

        // The cloud pass's noise clock and layer drift over a window of real seconds: flow seconds per real second, and the layers' drift in metres per second (sum of the
        // layers' displacement vectors' lengths).
        private double flowRate, driftRate;

        private IEnumerable<ProbeWait> Measure(double seconds)
        {
            double flow0 = Rig.FlowTimeS, real0 = Rig.RealS;
            IList<double> drift0 = Rig.DriftM;
            yield return ProbeWait.Seconds(seconds);
            double real = Rig.RealS - real0;
            flowRate = real > 0.0 ? (Rig.FlowTimeS - flow0) / real : double.NaN;
            IList<double> drift1 = Rig.DriftM;
            double sum = 0.0;
            for (int l = 0; l + 2 < drift1.Count && l + 2 < drift0.Count; l += 3)
            {
                double dx = drift1[l] - drift0[l], dy = drift1[l + 1] - drift0[l + 1], dz = drift1[l + 2] - drift0[l + 2];
                sum += Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            driftRate = real > 0.0 ? sum / real : double.NaN;
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            // The switches the series declares for this start (productSwitches).
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            SetSetting(AtmospheresSettings.Keys.CloudSource, (double)(int)CloudSourceKind.Ours);
            SetSetting(AtmospheresSettings.Keys.OwnClouds, 1.0);
            yield return ProbeWait.Until(() => Rig.FlightReady && Rig.PassDrawing, 300, "the flight scene and the own cloud pass drawing");
            reached = true;
            // The vessel is counted once it is loaded and unpacked (the viewer places nothing before) and again at the end.
            foreach (ProbeWait w in WaitVesselReady()) yield return w;
            // The reference of restore_time that does not come from the viewer: the game's UT before the viewer opened and the physics time the game lets pass since (Time.fixedTime,
            // not the real time: the game's clock runs slower than the real time wherever the frames are longer than its physics steps allow, 0.4 of it at 10 fps).
            double utStart = Rig.GameUt, realStart = Rig.RealS, physicsStart = Rig.PhysicsTimeS;
            IList<ViewerLayer> layers = Rig.Layers;
            Info("layers", layers.Count);
            int drawnAtStart = Rig.PassFramesDrawn;
            double startAsl = Rig.Pose.VesselAslM;
            Info("start_vessel_asl_m", startAsl);
            double softness0 = Rig.Setting(AtmospheresSettings.Keys.CloudSoftness);
            double forceLevel0 = Rig.Setting(AtmospheresSettings.Keys.ForceLevel), forceType0 = Rig.Setting(AtmospheresSettings.Keys.ForceType);
            Verdict("closed_at_start", "the viewer is closed before it is opened", !Rig.WindowOpen && !Rig.SessionActive, "no window, no session", "window " + Rig.WindowOpen + ", session " + Rig.SessionActive);

            // --- The game's own frame time with the viewer closed and idle: the baseline of the cost readout's frame time in the workbench section ---
            foreach (ProbeWait w in IdleFrameTime()) yield return w;

            // --- a. The window ---
            Verdict("addon_running", "the viewer's frame addon runs in the flight scene", Rig.AddonRunning, "running", Rig.AddonRunning ? "running" : "not started (no main mod?)");
            Rig.ToggleWindow();
            foreach (ProbeWait w in Poll(() => Rig.WindowOpen && Rig.SessionActive && Rig.WindowVisible, PollShortS)) yield return w;
            Verdict("window_opens", "Toggle opens the window and starts the session", polled, "window open, visible, session active",
                    "window " + Rig.WindowOpen + ", visible " + Rig.WindowVisible + ", session " + Rig.SessionActive);
            Info("keys_listed", Rig.KeyCount);
            Verdict("keys_listed", "the key list holds the registry's keys and the stack's per-layer keys", Rig.KeyCount == Rig.KeysExpected && Rig.KeyCount > MinKeys,
                    Rig.KeysExpected + " keys (more than " + MinKeys + ")", Rig.KeyCount + " keys");
            Rig.ToggleWindow();
            foreach (ProbeWait w in Poll(() => !Rig.WindowOpen && !Rig.SessionActive, PollShortS)) yield return w;
            Verdict("window_closes", "a second Toggle closes the window and ends the session", polled, "no window, no session", "window " + Rig.WindowOpen + ", session " + Rig.SessionActive);
            yield return ProbeWait.Frames(5);
            Rig.ToggleWindow();
            foreach (ProbeWait w in Poll(() => Rig.WindowOpen && Rig.SessionActive && Rig.WindowVisible, PollShortS)) yield return w;
            Verdict("window_reopens", "a third Toggle opens it again", polled, "window open, session active", "window " + Rig.WindowOpen + ", session " + Rig.SessionActive);
            if (!Rig.SessionActive)
            {
                Fail("the viewer's session did not start: nothing further can be checked");
                yield break;
            }

            // --- c. The pose, the camera, the altitude control ---
            ViewerPose ground = PoseNamed(layers, "ground");
            Rig.PlacePose(ground);
            yield return ProbeWait.Frames(2);
            foreach (ProbeWait w in Poll(() => !Rig.PlacementPending && Rig.HoldStable, PollPlaceS)) yield return w;
            Verdict("pose_settles", "the ground pose is placed and the vessel held", polled, "placement done, hold stable", "pending " + Rig.PlacementPending + ", hold stable " + Rig.HoldStable);
            yield return ProbeWait.Frames(SettleFrames);
            foreach (ProbeWait w in StillAim()) yield return w;
            ViewerPoseReading p = Rig.Pose;
            Near("pose_height", "the ground pose's height above the terrain", ground.AglM, p.VesselAglM, Math.Max(AltitudeAbsM, AltitudeRel * ground.AglM));
            Near("pose_camera_distance", "the camera's distance from the vessel", ground.CameraDistM, p.CameraToVesselM, Math.Max(CameraDistAbsM, CameraDistRel * ground.CameraDistM));
            Near("pose_aim_elevation", "the camera's elevation", ground.ElevationDeg, p.ElevationDeg, AimElevationDeg);
            double az = Circular(ground.HeadingDeg, p.AzimuthDeg);
            Verdict("pose_aim_azimuth", "the camera's heading", az <= AimAzimuthDeg, F(ground.HeadingDeg) + " +-" + F(AimAzimuthDeg) + " deg", F(p.AzimuthDeg) + " deg");
            Info("pose_aim_azimuth_deg", p.AzimuthDeg);
            if (Rig.AimNote != null) LogLine("viewer: the camera's aim stopped short of the pose's: " + Rig.AimNote);
            // The window stands wholly inside the screen (it has been open through the placement above, so its list and its position have settled).
            WindowFitReading win = ViewerWindowCheck.Read(Rig);
            Verdict("window_inside_screen", "the window lies wholly inside the screen", win.Inside, win.Expected, win.Read);
            Info("screen_width_px", win.ScreenW);
            Info("screen_height_px", win.ScreenH);
            Info("ui_scale", win.UiScale);
            Info("window_x_min_px", win.Box.XMin);
            Info("window_x_max_px", win.Box.XMax);
            Info("window_y_min_px", win.Box.YMin);
            Info("window_y_max_px", win.Box.YMax);
            // One screenshot of the open window at this pose, for a person.
            string shot = Rig.Screenshot("viewer-window-open.png");
            yield return ProbeWait.Frames(20);
            long shotBytes = Rig.FileSize(shot);
            Verdict("window_screenshot", "the screenshot of the open window is written", shotBytes > MinPictureBytes, "a file of more than " + MinPictureBytes + " bytes", shotBytes + " bytes");

            double max = AltitudeScale.MaxAglM(70000.0);
            double sliderTarget = AltitudeScale.FromSlider(AltitudeScale.ToSlider(2000.0), max);
            // An aim other than the pose's, turned by hand: a placement that did not keep the aim would put the pose's back (a placement that leaves the camera alone cannot be told
            // from one that keeps it where the aim is the pose's own).
            Rig.SetCameraAim(KeepAimHeadingDeg, KeepAimElevationDeg);
            yield return ProbeWait.Frames(SettleFrames);
            foreach (ProbeWait w in StillAim()) yield return w;
            ViewerPoseReading before = Rig.Pose;
            // Turned: the aim read back stands well away from the pose's (heading and elevation), so that a placement that put the pose's aim back would show. How far it stands from
            // the heading asked for is not judged here: the pose rows judge the viewer's own aim, and this row the keeping of whatever aim the player's hand left.
            bool aimTurned = Circular(before.AzimuthDeg, ground.HeadingDeg) >= TurnedAwayDeg && Math.Abs(before.ElevationDeg - ground.ElevationDeg) >= TurnedAwayElevationDeg;
            Rig.RequestAltitude(sliderTarget);
            yield return ProbeWait.Frames(2);
            foreach (ProbeWait w in Poll(() => !Rig.PlacementPending && Rig.HoldStable, PollPlaceS)) yield return w;
            yield return ProbeWait.Frames(SettleFrames);
            foreach (ProbeWait w in StillAim()) yield return w;
            p = Rig.Pose;
            double distanceAfterSlider = p.CameraToVesselM;
            Near("slider_height", "the altitude slider's height above the terrain", sliderTarget, p.VesselAglM, Math.Max(AltitudeAbsM, AltitudeRel * sliderTarget));
            Verdict("slider_keeps_aim", "the camera's heading and pitch stay where the player turned them (not the pose's)", aimTurned && Circular(before.AzimuthDeg, p.AzimuthDeg) <= AimAzimuthDeg && Math.Abs(before.ElevationDeg - p.ElevationDeg) <= AimElevationDeg,
                    "turned by hand away from the pose's " + F(ground.HeadingDeg) + " / " + F(ground.ElevationDeg) + " by at least " + F(TurnedAwayDeg) + " / " + F(TurnedAwayElevationDeg) + " deg (read " + F(before.AzimuthDeg) + " / " + F(before.ElevationDeg)
                    + "), and still there after the change within " + F(AimAzimuthDeg) + " / " + F(AimElevationDeg) + " deg",
                    "heading " + F(p.AzimuthDeg) + ", elevation " + F(p.ElevationDeg) + (aimTurned ? "" : "; the camera did not take the turned aim"));
            const double typed = 8000.0;
            Rig.RequestAltitude(typed);
            yield return ProbeWait.Frames(2);
            foreach (ProbeWait w in Poll(() => !Rig.PlacementPending && Rig.HoldStable, PollPlaceS)) yield return w;
            yield return ProbeWait.Frames(SettleFrames);
            foreach (ProbeWait w in StillAim()) yield return w;
            p = Rig.Pose;
            Near("typed_height", "the typed height above the terrain", typed, p.VesselAglM, Math.Max(AltitudeAbsM, AltitudeRel * typed));
            // The camera stands where the pose put it from the vessel after both height changes: a camera left behind would be far from it.
            double distanceTolerance = Math.Max(CameraDistAbsM, CameraDistRel * ground.CameraDistM);
            Verdict("camera_follows", "the camera stays at the pose's distance from the vessel after the slider and the typed height",
                    Math.Abs(distanceAfterSlider - ground.CameraDistM) <= distanceTolerance && Math.Abs(p.CameraToVesselM - ground.CameraDistM) <= distanceTolerance,
                    F(ground.CameraDistM) + " +-" + F(distanceTolerance) + " m both times", F(distanceAfterSlider) + " m after the slider, " + F(p.CameraToVesselM) + " m after the typed height");

            // --- e. Time: the offset, the freeze, the sun ---
            double ut0 = Rig.GameUt, phys0 = Rig.PhysicsTimeS, sun0 = Rig.SunElevationDeg;
            Rig.SetTimeOffset(OffsetSeconds);
            yield return ProbeWait.Seconds(1.5);
            double ut1 = Rig.GameUt, phys1 = Rig.PhysicsTimeS, sun1 = Rig.SunElevationDeg;
            Near("time_offset", "the time offset moves the game's clock", OffsetSeconds + (phys1 - phys0), ut1 - ut0, UtAbsS);
            Rig.SetTimeOffset(-OffsetSeconds);
            yield return ProbeWait.Seconds(1.5);
            double ut2 = Rig.GameUt, phys2 = Rig.PhysicsTimeS, sun2 = Rig.SunElevationDeg;
            Near("time_offset_back", "a negative offset moves it back", -2.0 * OffsetSeconds + (phys2 - phys1), ut2 - ut1, UtAbsS);
            double moved = Math.Max(Math.Abs(sun1 - sun0), Math.Abs(sun2 - sun0));
            Info("sun_elevation_start_deg", sun0);
            Info("sun_elevation_moved_deg", moved);
            Verdict("sun_moves", "the sun's elevation follows the clock", moved > SunMovesDeg, "a change of more than " + F(SunMovesDeg) + " deg", F(sun0) + " -> " + F(sun1) + " / " + F(sun2) + " deg");
            Rig.SetTimeOffset(0.0);
            yield return ProbeWait.Seconds(1);
            Rig.FreezeTime(true);
            yield return ProbeWait.Frames(3);
            double frozenUt = Rig.ClockFrozenUt, natural0 = Rig.ClockNatural, physF = Rig.PhysicsTimeS;
            yield return ProbeWait.Seconds(MeasureS);
            // The window is the physics time, not the wall time: at a low frame rate the physics clock runs slower than the wall clock (0.82 s in 3 s at 104 ms frames), so the window is
            // kept open until it holds PhysicsWindowMinS of physics time (the ratio of a shorter one is not read).
            foreach (ProbeWait w in Poll(() => Rig.PhysicsTimeS - physF > PhysicsWindowMinS, PollMapS)) yield return w;
            double heldUt = Rig.GameUt, naturalRan = Rig.ClockNatural - natural0, physRan = Rig.PhysicsTimeS - physF;
            Verdict("time_freeze", "Freeze Time holds the game's clock while the natural time runs on", Rig.ClockFrozen && Math.Abs(heldUt - frozenUt) <= FreezeHeldS && Math.Abs(naturalRan - physRan) <= UtAbsS,
                    "clock at " + F(frozenUt) + " +-" + F(FreezeHeldS) + ", natural +" + F(physRan) + " s (the game's physics time)", "clock " + F(heldUt) + ", natural +" + F(naturalRan) + " s, frozen " + Rig.ClockFrozen);
            // The reference of every time row is the physics time: the game's clock must advance with it (measured, not assumed; the viewer's natural time follows the game's clock).
            double ratio = physRan > PhysicsWindowMinS ? naturalRan / physRan : double.NaN;
            Info("natural_per_physics", ratio);
            Verdict("natural_tracks_physics", "the game's clock advances with its physics time (the reference of the time rows)", !double.IsNaN(ratio) && ratio >= PhysicsRatioMin && ratio <= PhysicsRatioMax,
                    F(PhysicsRatioMin) + " to " + F(PhysicsRatioMax) + " game seconds per physics second", F(naturalRan) + " / " + F(physRan) + " = " + F(ratio) + (physRan > PhysicsWindowMinS ? "" : " (the window held no more than " + F(PhysicsWindowMinS) + " s of physics time)"));
            Rig.FreezeTime(false);
            double physU = Rig.PhysicsTimeS, heldAt = Rig.GameUt;
            yield return ProbeWait.Seconds(MeasureS);
            double resumed = Rig.GameUt, physSince = Rig.PhysicsTimeS - physU;
            // The clock must have run on, by at least half the physics time that passed: at a low frame rate that is about a second, below the band of the position check.
            Verdict("time_resumes", "the clock runs on from where it was held", Math.Abs(resumed - (frozenUt + physSince)) <= UtAbsS && resumed - heldAt >= ResumedShare * physSince,
                    "from " + F(frozenUt) + " by the physics time since (" + F(physSince) + " s) +-" + F(UtAbsS) + ", at least " + F(ResumedShare * physSince) + " s more than at the resume", F(resumed) + " (" + F(resumed - heldAt) + " s more than at the resume)");
            Rig.SetTimeOffset(0.0);
            yield return ProbeWait.Seconds(1);

            // --- e. Forced weather ---
            int lowStratiformIndex, cumulusIndex;
            ViewerLayer? lowStratiform = FirstWithRole(layers, WeatherPresets.Role.LowStratiform, out lowStratiformIndex);
            ViewerLayer? cumulus = FirstWithRole(layers, WeatherPresets.Role.Cumulus, out cumulusIndex);
            Info("weather_roles_missing", (lowStratiform.HasValue ? 0 : 1) + (cumulus.HasValue ? 0 : 1));
            float cumulusFair = float.NaN, lowStratiformUnderCumulus = float.NaN;
            // What the field's own weather is at the pad before anything is forced: a low stratiform layer already closed shows nothing of Overcast, rain keeps the Fair cover up.
            float lowStratiformBaseline = lowStratiform.HasValue ? Rig.LayerCover(lowStratiformIndex) : float.NaN;
            double padRain = Rig.PrecipitationLevel;
            Info("weather_field_low_stratiform_cover", lowStratiformBaseline);
            Info("weather_pad_precipitation", padRain);
            ViewerWeather[] sequence = { ViewerWeather.Overcast, ViewerWeather.Fair, ViewerWeather.Cumulus, ViewerWeather.RainLowStratiform, ViewerWeather.Field };
            foreach (ViewerWeather weather in sequence)
            {
                string tag = weather.ToString().ToLowerInvariant();
                Rig.SetWeather(weather);
                yield return ProbeWait.Frames(5);
                // What the pass reads: every layer's forced map value and the combination switched off (the field's weather: no override).
                var mismatches = new List<string>();
                foreach (KeyValuePair<string, float> pair in WeatherPresets.Overrides(weather, layers))
                {
                    if (!pair.Key.StartsWith(CloudLookTuning.TuneMapOverride, StringComparison.Ordinal)) continue;
                    string layer = pair.Key.Substring(CloudLookTuning.TuneMapOverride.Length);
                    float expected = float.IsNaN(pair.Value) ? -1f : pair.Value;
                    float read = Rig.PassMapOverride(layer);
                    if (Math.Abs(read - expected) > 0.004f) mismatches.Add(layer + " " + F(read) + " (wanted " + F(expected) + ")");
                }
                bool combineOk = weather == ViewerWeather.Field ? !Rig.HasOverride(CloudLookTuning.TuneCoverCombine) : Rig.TuningValue(CloudLookTuning.TuneCoverCombine, 1f) == 0f;
                Verdict("weather_" + tag + "_forced", weather + ": the pass reads every layer's forced map and the combination to the field's cover", mismatches.Count == 0 && combineOk,
                        "every cloud.mapOverride.<layer> as the preset, cloud.coverCombine " + (weather == ViewerWeather.Field ? "not overridden" : "0"),
                        mismatches.Count == 0 ? "cloud.coverCombine " + F(Rig.TuningValue(CloudLookTuning.TuneCoverCombine, 1f)) : string.Join("; ", mismatches.ToArray()));
                if (weather == ViewerWeather.Field) break;

                // What the pass drew from it: the layer's cover in the CPU mirror at the vessel, once a map round has come (a low stratiform layer is 1 or 0, the cumulus layer moves with the preset).
                Func<bool> arrived;
                float lowStratiformWanted = weather == ViewerWeather.Overcast || weather == ViewerWeather.RainLowStratiform ? (float)CoverHigh : (float)CoverLow;
                bool lowStratiformUp = weather == ViewerWeather.Overcast || weather == ViewerWeather.RainLowStratiform;
                if (weather == ViewerWeather.Cumulus) arrived = () => !cumulus.HasValue || Rig.LayerCover(cumulusIndex) >= cumulusFair + CoverRise;
                else arrived = () => !lowStratiform.HasValue || (lowStratiformUp ? Rig.LayerCover(lowStratiformIndex) >= lowStratiformWanted : Rig.LayerCover(lowStratiformIndex) <= lowStratiformWanted);
                foreach (ProbeWait w in Poll(arrived, PollMapS)) yield return w;
                float lowStratiformRead = lowStratiform.HasValue ? Rig.LayerCover(lowStratiformIndex) : float.NaN, cumulusRead = cumulus.HasValue ? Rig.LayerCover(cumulusIndex) : float.NaN;
                if (weather == ViewerWeather.Fair) cumulusFair = cumulusRead;
                float lowStratiformBefore = lowStratiformUnderCumulus;
                if (weather == ViewerWeather.Cumulus) lowStratiformUnderCumulus = lowStratiformRead;
                string wanted = weather == ViewerWeather.Cumulus ? "cumulus cover at least " + F(cumulusFair + CoverRise) : "low stratiform cover " + (lowStratiformUp ? "at least " : "at most ") + F(lowStratiformWanted);
                string seen = "low stratiform " + F(lowStratiformRead) + ", cumulus " + F(cumulusRead);
                string unclear = null;
                if (weather == ViewerWeather.Overcast && lowStratiform.HasValue && lowStratiformBaseline >= CoverHigh)
                    unclear = "the field's own low stratiform cover at the pad was " + F(lowStratiformBaseline) + " before anything was forced: forcing it shows no change";
                else if (weather == ViewerWeather.Fair && !polled && padRain > RainAtPadLevel)
                    unclear = "it rains at the pad (level " + F(padRain) + "): the low stratiform layer's rain floor keeps the cover up, which the Fair preset does not switch off";
                else if (weather == ViewerWeather.RainLowStratiform && lowStratiform.HasValue && lowStratiformBefore >= CoverHigh)
                    unclear = "the low stratiform layer stood at " + F(lowStratiformBefore) + " under the Cumulus preset (the rain floor of rain at the pad): forcing it shows no change";
                if (weather == ViewerWeather.Overcast || weather == ViewerWeather.Fair || weather == ViewerWeather.RainLowStratiform) Info("weather_" + tag + "_unclear", unclear != null ? 1.0 : 0.0);
                if (unclear != null) Unclear("weather_" + tag + "_cover", wanted + (weather == ViewerWeather.Overcast ? " (from " + F(lowStratiformBaseline) + ")" : ""), seen, unclear);
                else Verdict("weather_" + tag + "_cover", weather + ": the pass draws it (the layer's cover at the vessel)", polled,
                             wanted + (weather == ViewerWeather.Overcast ? ", from the field's " + F(lowStratiformBaseline) : ""), seen);
                Info("weather_" + tag + "_low_stratiform_cover", lowStratiformRead);
                Info("weather_" + tag + "_cumulus_cover", cumulusRead);
                if (weather == ViewerWeather.RainLowStratiform)
                {
                    Verdict("weather_rainlowstratiform_precipitation", "Nimbostratus Rain forces rain at the camera (forceLevel and forceType)", Math.Abs(Rig.Setting(AtmospheresSettings.Keys.ForceLevel) - 0.6) < 1e-6
                            && (int)Rig.Setting(AtmospheresSettings.Keys.ForceType) == (int)ForceType.Rain, "level 0.6, type Rain",
                            "level " + F(Rig.Setting(AtmospheresSettings.Keys.ForceLevel)) + ", type " + F(Rig.Setting(AtmospheresSettings.Keys.ForceType)));
                }
            }
            Verdict("weather_field_restores_precipitation", "the field's weather gives the forced precipitation back", Math.Abs(Rig.Setting(AtmospheresSettings.Keys.ForceLevel) - forceLevel0) < 1e-6
                    && (int)Rig.Setting(AtmospheresSettings.Keys.ForceType) == (int)forceType0, "level " + F(forceLevel0) + ", type " + F(forceType0),
                    "level " + F(Rig.Setting(AtmospheresSettings.Keys.ForceLevel)) + ", type " + F(Rig.Setting(AtmospheresSettings.Keys.ForceType)));

            // --- b. A/B ---
            const string exposureKey = "cloud.exposure";
            float exposureA = Rig.PassExposure;
            double softnessA = Rig.Setting(AtmospheresSettings.Keys.CloudSoftness);
            float exposureB = exposureA + (float)ExposureStep;
            double softnessB = softnessA >= 0.75 ? 0.5 : 1.0;
            Rig.StoreA();
            Rig.SetKey(exposureKey, exposureB);
            Rig.SetViewerSetting(AtmospheresSettings.Keys.CloudSoftness, softnessB);
            Rig.WatchKey(exposureKey);
            yield return ProbeWait.Frames(10);
            Verdict("ab_b_pass", "B: the pass's frame reads the new exposure, the setting holds the new softness", Math.Abs(Rig.PassExposure - exposureB) <= ExposureEps && Rig.Setting(AtmospheresSettings.Keys.CloudSoftness) == softnessB && Rig.Showing == 'B',
                    "exposure " + F(exposureB) + ", softness " + F(softnessB) + ", side B", "exposure " + F(Rig.PassExposure) + ", softness " + F(Rig.Setting(AtmospheresSettings.Keys.CloudSoftness)) + ", side " + Rig.Showing);
            Verdict("ab_b_read_check", "B: the read check sees the pass read cloud.exposure in the frame just run", ReadThisFrame(), "read in frame " + Rig.TuningFrame + " or the one before",
                    Rig.WatchedReadFrame == int.MinValue ? "never read" : "last read in frame " + Rig.WatchedReadFrame);
            Rig.FlipAB();
            yield return ProbeWait.Frames(10);
            Verdict("ab_flip_a_pass", "Flip: the stored A's values stand again (exposure, softness, no override)", Math.Abs(Rig.PassExposure - exposureA) <= ExposureEps && Rig.Setting(AtmospheresSettings.Keys.CloudSoftness) == softnessA
                    && !Rig.HasOverride(exposureKey) && Rig.Showing == 'A', "exposure " + F(exposureA) + ", softness " + F(softnessA) + ", no override, side A",
                    "exposure " + F(Rig.PassExposure) + ", softness " + F(Rig.Setting(AtmospheresSettings.Keys.CloudSoftness)) + ", override " + Rig.HasOverride(exposureKey) + ", side " + Rig.Showing);
            Verdict("ab_flip_a_read_check", "A: the read check sees the pass read cloud.exposure with no override set", ReadThisFrame(), "read in frame " + Rig.TuningFrame + " or the one before",
                    Rig.WatchedReadFrame == int.MinValue ? "never read" : "last read in frame " + Rig.WatchedReadFrame);
            Rig.FlipAB();
            yield return ProbeWait.Frames(10);
            Verdict("ab_flip_b_pass", "a second Flip brings B back", Math.Abs(Rig.PassExposure - exposureB) <= ExposureEps && Rig.Setting(AtmospheresSettings.Keys.CloudSoftness) == softnessB && Rig.Showing == 'B',
                    "exposure " + F(exposureB) + ", softness " + F(softnessB) + ", side B", "exposure " + F(Rig.PassExposure) + ", softness " + F(Rig.Setting(AtmospheresSettings.Keys.CloudSoftness)) + ", side " + Rig.Showing);
            Rig.FlipAB();
            yield return ProbeWait.Frames(5);

            // --- d. The cloud speed ---
            Rig.SetCloudSpeed(1f);
            yield return ProbeWait.Seconds(1);
            foreach (ProbeWait w in Measure(MeasureS)) yield return w;
            double flow1 = flowRate, drift1 = driftRate;
            Info("flow_rate_x1", flow1);
            Info("drift_m_per_s_x1", drift1);
            Verdict("speed_x1_flows", "at speed 1 the noise clock runs with the real time", flow1 >= FlowRateMin && flow1 <= FlowRateMax, F(FlowRateMin) + " to " + F(FlowRateMax) + " flow s per real s", F(flow1));
            Rig.SetCloudSpeed((float)SpeedScale);
            yield return ProbeWait.Seconds(1);
            foreach (ProbeWait w in Measure(MeasureS)) yield return w;
            double flow4 = flowRate, drift4 = driftRate;
            Info("flow_rate_x4", flow4);
            Info("drift_m_per_s_x4", drift4);
            Verdict("speed_x4_ratio", "at speed 4 the noise clock runs 4 times faster", flow1 > 0.0 && flow4 / flow1 >= RatioMin && flow4 / flow1 <= RatioMax,
                    "a ratio of " + F(RatioMin) + " to " + F(RatioMax), F(flow4) + " / " + F(flow1) + " = " + F(flow4 / flow1));
            Verdict("speed_ui_value", "the viewer's speed and the tuning table agree with the control", Math.Abs(Rig.CloudSpeed - SpeedScale) < 1e-4 && Math.Abs(Rig.TuningValue(CloudMotion.TuneScale, 1f) - SpeedScale) < 1e-4,
                    "speed " + F(SpeedScale) + " in both", "viewer " + F(Rig.CloudSpeed) + ", table " + F(Rig.TuningValue(CloudMotion.TuneScale, 1f)));
            bool driftMeasurable = drift1 > DriftMeasurableM;
            Info("drift_measurable", driftMeasurable ? 1.0 : 0.0);
            if (driftMeasurable)
                Verdict("speed_drift_ratio", "at speed 4 the layers' drift is 4 times faster", drift4 / drift1 >= DriftRatioMin && drift4 / drift1 <= DriftRatioMax,
                        "a ratio of " + F(DriftRatioMin) + " to " + F(DriftRatioMax), F(drift4) + " / " + F(drift1) + " = " + F(drift4 / drift1) + " m per s");
            else LogLine("viewer: the layers' drift is not measurable here (" + F(drift1) + " m per s at speed 1: no wind at the layers); the noise clock above stands for the motion");
            Rig.FreezeClouds(true);
            yield return ProbeWait.Seconds(1);
            foreach (ProbeWait w in Measure(MeasureS)) yield return w;
            Info("flow_rate_frozen", flowRate);
            Verdict("speed_frozen", "Freeze Clouds stops the noise clock and the drift", Rig.CloudsFrozen && flowRate <= FrozenFlowS && (!driftMeasurable || driftRate <= DriftFrozenShare * drift1)
                    && Math.Abs(Rig.TuningValue(CloudMotion.TuneScale, 1f)) < 1e-6, "flow rate at most " + F(FrozenFlowS) + ", scale 0",
                    "flow rate " + F(flowRate) + ", drift " + F(driftRate) + " m per s, scale " + F(Rig.TuningValue(CloudMotion.TuneScale, 1f)));
            Rig.FreezeClouds(false);
            yield return ProbeWait.Seconds(1);
            foreach (ProbeWait w in Measure(MeasureS)) yield return w;
            Verdict("speed_resumes", "letting the clouds go again brings the speed back", flow1 > 0.0 && flowRate / flow1 >= RatioMin && flowRate / flow1 <= RatioMax && !Rig.CloudsFrozen,
                    "a ratio of " + F(RatioMin) + " to " + F(RatioMax), F(flowRate) + " / " + F(flow1) + " = " + F(flowRate / flow1));
            Rig.SetCloudSpeed(1f);

            // --- f. The capture ---
            string lastBefore = Rig.LastCapture;
            Rig.RequestCapture();
            yield return ProbeWait.Frames(1);
            int hidden = 0, visible = 0, guard = 0;
            while (Rig.Capturing && guard++ < 120)
            {
                if (Rig.WindowVisible) visible++;
                else hidden++;
                yield return ProbeWait.Frames(1);
            }
            string picture = null;
            foreach (ProbeWait w in Poll(() => Rig.LastCapture != lastBefore && Rig.LastCapture.Length > 0 && Rig.FileSize(Rig.CapturePicturePath(Rig.LastCapture)) > MinPictureBytes, 15.0)) yield return w;
            bool firstWritten = polled;
            if (firstWritten) picture = Rig.LastCapture;
            Verdict("capture_hidden", "the window is hidden while the picture is taken and back afterwards", visible == 0 && hidden >= 2 && Rig.WindowVisible && Rig.CaptureReleasedFrame > Rig.CaptureFrame,
                    "hidden in every frame from the request to the frame after the picture was asked for, then shown", hidden + " hidden and " + visible + " visible frames, picture asked in frame " + Rig.CaptureFrame
                    + ", window back in frame " + Rig.CaptureReleasedFrame + ", visible now " + Rig.WindowVisible);
            string sidecarPath = picture == null ? null : System.IO.Path.ChangeExtension(Rig.CapturePicturePath(picture), ".txt");
            string sidecar = sidecarPath == null ? null : Rig.ReadText(sidecarPath);
            Verdict("capture_files", "the picture and its sidecar are written", firstWritten && sidecar != null, "a picture of more than " + MinPictureBytes + " bytes and a sidecar",
                    picture == null ? "no picture" : picture + (sidecar == null ? ", no sidecar" : ", with its sidecar"));
            Dictionary<string, string> side = ParseSidecar(sidecar);
            ViewerPoseReading pose = Rig.Pose;
            string sideUt, sideAgl;
            side.TryGetValue("ut", out sideUt);
            side.TryGetValue("altitudeAgl", out sideAgl);
            double ut, agl;
            bool utOk = double.TryParse(sideUt, NumberStyles.Float, CultureInfo.InvariantCulture, out ut) && Math.Abs(ut - Rig.GameUt) <= SidecarUtS;
            bool aglOk = double.TryParse(sideAgl, NumberStyles.Float, CultureInfo.InvariantCulture, out agl) && Math.Abs(agl - pose.CameraAglM) <= SidecarAglM;
            string pictureName, poseName, weatherName, speedText, sideText, frozenText;
            side.TryGetValue("picture", out pictureName);
            side.TryGetValue("pose", out poseName);
            side.TryGetValue("weather", out weatherName);
            side.TryGetValue("cloudSpeed", out speedText);
            side.TryGetValue("abSide", out sideText);
            side.TryGetValue("timeFrozen", out frozenText);
            bool sidecarOk = pictureName == picture && poseName == "ground" && weatherName == WeatherPresets.Title(ViewerWeather.Field) && speedText == "1" && sideText == "A" && frozenText == "False" && utOk && aglOk;
            Verdict("capture_sidecar", "the sidecar names what was set", sidecarOk, "picture " + picture + ", pose ground, weather " + WeatherPresets.Title(ViewerWeather.Field) + ", cloudSpeed 1, abSide A, not frozen, the game's UT and camera height",
                    "picture " + pictureName + ", pose " + poseName + ", weather " + weatherName + ", cloudSpeed " + speedText + ", abSide " + sideText + ", timeFrozen " + frozenText + ", ut " + sideUt + ", altitudeAgl " + sideAgl);
            Rig.RequestCapture();
            yield return ProbeWait.Frames(1);
            foreach (ProbeWait w in Poll(() => !Rig.Capturing && Rig.LastCapture != picture && Rig.LastCapture.Length > 0 && Rig.FileSize(Rig.CapturePicturePath(Rig.LastCapture)) > MinPictureBytes, 15.0)) yield return w;
            Verdict("capture_second", "a second picture has its own file and the first stays", polled && Rig.FileSize(Rig.CapturePicturePath(picture ?? "")) > MinPictureBytes, "two different files", picture + " and " + Rig.LastCapture);

            // --- The workbench (VIEW3): groups, modes, Rebuild Weather and the seed, readout, presets, A/B, cost ---
            foreach (ProbeWait w in WorkbenchSection()) yield return w;

            // --- The window closed: everything back ---
            // Closed from a dirty state, so that the close has something to undo: a forced weather with forced rain, the other side of the A/B pair, a time offset and the time held.
            // (Before this the options were back already except the cloud speed and the clock was at its natural time, so a close that restored nothing passed.)
            Rig.SetWeather(ViewerWeather.RainLowStratiform);
            Rig.FlipAB();
            Rig.SetTimeOffset(OffsetSeconds);
            yield return ProbeWait.Seconds(1.5);
            Rig.FreezeTime(true);
            yield return ProbeWait.Seconds(1);
            double naturalAtClose = Rig.ClockNatural, realClose = Rig.RealS;
            Rig.ToggleWindow();
            foreach (ProbeWait w in Poll(() => !Rig.WindowOpen && !Rig.SessionActive, PollShortS)) yield return w;
            Verdict("close_ends_session", "closing the window ends the session", polled, "no window, no session", "window " + Rig.WindowOpen + ", session " + Rig.SessionActive);
            yield return ProbeWait.Frames(10);
            bool overridesGone = !Rig.HasOverride(CloudMotion.TuneScale) && !Rig.HasOverride(exposureKey) && !Rig.HasOverride(CloudLookTuning.TuneCoverCombine);
            foreach (ViewerLayer l in layers) overridesGone &= !Rig.HasOverride(CloudLookTuning.TuneMapOverride + l.Name);
            bool settingsBack = Rig.Setting(AtmospheresSettings.Keys.CloudSoftness) == softness0 && Math.Abs(Rig.Setting(AtmospheresSettings.Keys.ForceLevel) - forceLevel0) < 1e-6
                                && (int)Rig.Setting(AtmospheresSettings.Keys.ForceType) == (int)forceType0 && Math.Abs(Rig.CloudSpeed - 1f) < 1e-6;
            Verdict("restore_options", "the options the viewer set are back (overrides gone, settings, cloud speed)", overridesGone && settingsBack, "no override left, softness " + F(softness0) + ", forceLevel " + F(forceLevel0) + ", speed 1",
                    "overrides gone " + overridesGone + ", softness " + F(Rig.Setting(AtmospheresSettings.Keys.CloudSoftness)) + ", forceLevel " + F(Rig.Setting(AtmospheresSettings.Keys.ForceLevel)) + ", speed " + F(Rig.CloudSpeed));
            double utBack = Rig.GameUt;
            // The reference is the UT before the viewer opened and the real seconds since, not the viewer's own natural time (which a wrong accumulation would share).
            double physicsSince = Rig.PhysicsTimeS - physicsStart;
            double utExpected = utStart + physicsSince;
            Verdict("restore_time", "the game's clock is back on the natural time", Math.Abs(utBack - utExpected) <= RestoreTimeS,
                    F(utStart) + " (UT before the viewer opened) + the physics time since (" + F(physicsSince) + " s; the real time since was " + F(Rig.RealS - realStart) + " s) +-" + F(RestoreTimeS),
                    F(utBack) + " (the viewer's natural time at the close: " + F(naturalAtClose) + ", " + F(Rig.RealS - realClose) + " s ago)");
            yield return ProbeWait.Seconds(4);
            double asl = Rig.Pose.VesselAslM;
            Near("restore_vessel", "the vessel is back at its start", startAsl, asl, RestoreVesselM);
            Verdict("pass_kept_drawing", "the own cloud pass kept drawing through the whole run", Rig.PassDrawing && Rig.PassFramesDrawn > drawnAtStart, "drawing, more frames than at the start", "drawing " + Rig.PassDrawing
                    + ", " + (Rig.PassFramesDrawn - drawnAtStart) + " frames since the start");
            yield return ProbeWait.Seconds(AfterCloseWaitS);
            JudgeVesselIntact(Prefix);
            Info("elapsed_s", Elapsed);
        }

        // The pass read the watched key in the frame just run or the one before (the viewer's own looks at it are not counted: Tuning.Peek).
        private bool ReadThisFrame()
        {
            int read = Rig.WatchedReadFrame;
            return read != int.MinValue && Rig.TuningFrame - read <= 1;
        }
    }
}
