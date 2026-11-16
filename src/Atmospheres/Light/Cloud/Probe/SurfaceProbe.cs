using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Atmospheres.Viewer;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // What the scene's fog and light read in one frame of the surface series.
    internal struct FogReading
    {
        internal bool On;
        internal int Mode;
        internal float Density;
        internal Color Colour;
        // What the fog writer holds, and what the weather view asks of it and sees.
        internal float Held, Asked, Level, VisibilityM;
        // The scene's ambient light (RenderSettings.ambientLight) and the sun light's colour times intensity, as the game holds them in that frame.
        internal Color Ambient;
        internal float SunIntensity;
        internal int AmbientMode;
        internal Color SunColour;
    }

    // The probe of series K-S2 (start "surface", rows tests/series/rows/SURFACEPROBE.json): where the white of the far terrain in the solo pictures comes from. The same cb_far pose as
    // K-S1 (a cell 60 km ahead, the vessel held, the time and the clouds frozen), taken at the two headings 90 (K-S1's: the camera looks at the sun, SUSPECTED from K-S1's log line 'view 0
    // deg from it') and 270, each frame measured for the angle between the camera and the sun, and the SAME frame several times with ONE thing changed:
    //   old_fog     the fog rule before this package (driver.forcedLevelOwnsFog 0: the field's visibility is the fog's even under a forced level), the frame of the K-S1 pictures;
    //   baseline    the rule now: a forced level owns the fog (every layer empty);
    //   drawn       the cell drawn alone (the K-S1 picture);
    //   no_weather  weatherEffects off (the fog writer, the weather field and so the cloud pass, the weather view, precipitation, wet surfaces);
    //   no_pass     ownClouds off (only the cloud pass off, the weather effects on);
    //   no_cover    groundCover off (the snow and wet repaint of the terrain off; its restore is a budget of a few ms a frame, so the frame waits for it);
    //   no_atmo     Atmospheres' master switch off (all of the package: the main mod's own settings, Scatterer, TUFX and the game's air stay).
    // Each frame logs 'surface: <frame>: fog ...' (RenderSettings.fog, mode, density, colour, ambient, the sun's light, what the fog writer holds, the weather view's level and visibility, the
    // field's level at the vessel, the camera, the sun, the UT) and takes the viewer's picture; the pictures are compared offline (tools/surface_frames.py). The probe judges that the
    // frames are one pose, sun and scene, that the rule of the fog took effect (the baseline's frames ask no fog) and that each picture is written; it does not judge the look.
    [AtmospheresModule(993)]
    internal sealed class SurfaceProbe : SeriesProbe
    {
        internal const string Series = "K-S2";
        internal const string Prefix = "SURF.";
        // K-S1's cb_far pose and cell, and the universal time of its pictures (the field's weather at the vessel is the one K-S1 saw).
        internal const string TypeName = "Cumulonimbus incus";
        internal const double AglM = 300.0, ElevationDeg = 5.0, HeadingToward = 90.0, HeadingAway = 270.0, CameraDistM = 100.0, CellAheadKm = 60.0, K1Ut = 1470283.6;
        // A frame's camera stands within this of the first frame's aim of its pose, the sun within this of its first reading; the two headings differ by at least this angle to the sun.
        internal const double AimToleranceDeg = 0.5, SunDriftDeg = 1.0, PosesApartDeg = 60.0;
        // How long the scenario waits for the fog to settle after a switch (the writer slews at 12 % per 3 Hz tick: a few seconds), the longest wait, and the extra wait for the ground cover
        // to repaint back (a budget of a few ms a frame).
        internal const double FogSettleS = 4.0, FogWaitS = 25.0, CoverWaitS = 12.0, PollPlaceS = 60.0, PollMapS = 20.0, PollPictureS = 15.0, SettleS = 3.0;
        internal const int SettleFrames = 60, AimStillMaxFrames = 80, MinPictureBytes = 1000;

        // The frames of each pose (the names the rows use are p1_<frame> and p2_<frame>).
        internal static readonly string[] FramesP1 = { "old_fog", "baseline", "drawn", "no_weather", "no_pass", "no_cover", "no_atmo" };
        internal static readonly string[] FramesP2 = { "baseline", "drawn", "no_atmo" };

        private ISoloRig rig;
        private bool reached;
        private string taken;
        private bool polled;

        internal ISoloRig Rig
        {
            get { return rig ?? (rig = new GameSoloRig()); }
            set { rig = value; }
        }

        // The scene's fog and light as the game holds them, and the angle between the camera and the sun in degrees; a test replaces them.
        internal Func<FogReading> FogReader = ReadGameFog;
        internal Func<double> SunAngleReader = ReadSunAngle;

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

        private static FogReading ReadGameFog()
        {
            WeatherView view = WeatherHooks.View;
            Light sun = Sun.Instance != null ? Sun.Instance.sunLight : null;
            return new FogReading
            {
                On = RenderSettings.fog, Mode = (int)RenderSettings.fogMode, Density = RenderSettings.fogDensity, Colour = RenderSettings.fogColor,
                Held = FogWriter.WrittenDensity, Asked = view.FogDensity, Level = view.Level, VisibilityM = view.VisibilityM,
                Ambient = RenderSettings.ambientLight, SunIntensity = sun != null ? sun.intensity : float.NaN, AmbientMode = (int)RenderSettings.ambientMode,
                SunColour = sun != null ? sun.color : Color.black
            };
        }

        // The angle between the flight camera's forward and the direction to the sun, in degrees (NaN where either cannot be read).
        private static double ReadSunAngle()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || FlightGlobals.fetch == null) return double.NaN;
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            Vessel v = FlightGlobals.ActiveVessel;
            CelestialBody sun = GameGlobals.Sun;
            if (cam == null || v == null || sun == null) return double.NaN;
            Vector3d toSun = (sun.position - v.GetWorldPos3D()).normalized;
            Vector3d forward = cam.transform.forward;
            return Math.Acos(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(toSun, forward)))) * 180.0 / Math.PI;
        }

        private static string F(double v)
        {
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private bool Verdict(string name, string what, bool ok, string expected, string read)
        {
            LogLine("surface: " + what + ": expected " + expected + ", read " + read + (ok ? " - ok" : " - FAILED"));
            Emit(Prefix + name + "_ok", ok);
            Check(Prefix + name, ok, ok ? null : "expected " + expected + ", read " + read);
            return ok;
        }

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

        private IEnumerable<ProbeWait> StillAim()
        {
            ViewerPoseReading last = Rig.Pose;
            for (int i = 0; i < AimStillMaxFrames; i++)
            {
                yield return ProbeWait.Frames(2);
                ViewerPoseReading now = Rig.Pose;
                bool still = Circular(last.AzimuthDeg, now.AzimuthDeg) <= SoloProbe.AimStillDeg && Math.Abs(last.ElevationDeg - now.ElevationDeg) <= SoloProbe.AimStillDeg;
                last = now;
                if (still) yield break;
            }
        }

        // One picture by the viewer's capture; taken holds its file name (null where none was written).
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
            yield return ProbeWait.Frames(5);
        }

        // What the frame is: the fog and light the scene holds, the camera, the sun, the time.
        private FogReading Describe(string frame)
        {
            FogReading f = FogReader();
            ViewerPoseReading p = Rig.Pose;
            LogLine("surface: " + frame + ": fog " + (f.On ? "on" : "off") + ", mode " + f.Mode + ", density " + F(f.Density) + ", colour " + F(f.Colour.r) + " " + F(f.Colour.g) + " " + F(f.Colour.b)
                    + ", the fog writer holds " + F(f.Held) + ", the view asks " + F(f.Asked) + ", level " + F(f.Level) + ", visibility " + F(f.VisibilityM) + " m, the field's level at the vessel "
                    + F(Rig.PrecipitationLevel) + "; ambient (mode " + f.AmbientMode + ") " + F(f.Ambient.r) + " " + F(f.Ambient.g) + " " + F(f.Ambient.b) + ", sun light " + F(f.SunIntensity) + " colour " + F(f.SunColour.r) + " " + F(f.SunColour.g) + " " + F(f.SunColour.b) + "; camera azimuth " + F(p.AzimuthDeg)
                    + " elevation " + F(p.ElevationDeg) + " at " + F(p.CameraAglM) + " m, " + F(SunAngleReader()) + " deg from the sun; sun elevation " + F(Rig.SunElevationDeg) + " deg; UT " + F(Rig.GameUt));
            Emit(Prefix + frame + "_fog_density", f.On ? f.Density : 0.0);
            Emit(Prefix + frame + "_fog_asked", f.Asked);
            Emit(Prefix + frame + "_level", f.Level);
            return f;
        }

        // Waits until the scene's fog has settled to what the frame asks of it: unchanged for FogSettleS seconds (the writer slews at 12 % per tick, so a ramp is not stable), at most FogWaitS.
        private IEnumerable<ProbeWait> FogSettles()
        {
            double end = Rig.RealS + FogWaitS, stableSince = Rig.RealS;
            FogReading first = FogReader();
            float last = first.On ? first.Density : 0f;
            while (Rig.RealS < end)
            {
                yield return ProbeWait.Frames(2);
                FogReading f = FogReader();
                float now = f.On ? f.Density : 0f;
                if (Math.Abs(now - last) > 1e-7f + 0.002f * Math.Max(now, last)) stableSince = Rig.RealS;
                last = now;
                if (Rig.RealS - stableSince >= FogSettleS) yield break;
            }
        }

        // The state a frame needs, and the way back. Settings go through the probe's SetSetting (in memory; the runner restores the files).
        private void Enter(string frame, SoloEntry entry)
        {
            switch (frame)
            {
                case "old_fog": ForceTuning(FogRuleKey, 0f); break;
                case "baseline": ForceTuning(FogRuleKey, 1f); Rig.SetSoloStrength(0f); break;
                case "drawn": ForceTuning(FogRuleKey, 1f); Rig.SetSoloStrength(1f); break;
                case "no_weather": ForceTuning(FogRuleKey, 1f); Rig.SetSoloStrength(0f); SetSetting(AtmospheresSettings.Keys.WeatherEffects, 0.0); break;
                case "no_pass": ForceTuning(FogRuleKey, 1f); Rig.SetSoloStrength(0f); SetSetting(AtmospheresSettings.Keys.OwnClouds, 0.0); break;
                case "no_cover": ForceTuning(FogRuleKey, 1f); Rig.SetSoloStrength(0f); SetSetting(AtmospheresSettings.Keys.GroundCover, 0.0); break;
                case "no_atmo": ForceTuning(FogRuleKey, 1f); Rig.SetSoloStrength(0f); SetSetting(AtmospheresSettings.Keys.Enabled, 0.0); break;
            }
        }

        private void Leave(string frame)
        {
            switch (frame)
            {
                case "no_weather": SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0); break;
                case "no_pass": SetSetting(AtmospheresSettings.Keys.OwnClouds, 1.0); break;
                case "no_cover": SetSetting(AtmospheresSettings.Keys.GroundCover, groundCover0 ? 1.0 : 0.0); break;
                case "no_atmo": SetSetting(AtmospheresSettings.Keys.Enabled, 1.0); break;
            }
        }

        internal const string FogRuleKey = "driver.forcedLevelOwnsFog";
        private bool groundCover0;

        // One frame: switch the one thing, wait for the fog (and for the ground cover to repaint where the frame has it), describe it, take the picture.
        private IEnumerable<ProbeWait> Frame(string pose, string frame, SoloEntry entry, int bit, Dictionary<string, string> pictures, Dictionary<string, FogReading> fogs)
        {
            string key = pose + "_" + frame;
            Enter(frame, entry);
            if (frame == "drawn")
            {
                foreach (ProbeWait w in Poll(() => Rig.PassDrawing && Math.Abs(Rig.PassMapOverride(entry.LayerName) - 1f) <= SoloProbe.MapReadTolerance && Rig.PassLayerMask == bit, PollMapS)) yield return w;
                Verdict(pose + "_drawn_alone", pose + ": the cell is drawn alone", polled, "the layer's forced map 1 and the pass's layer mask the layer's bit", "map " + F(Rig.PassMapOverride(entry.LayerName)) + ", mask " + Rig.PassLayerMask);
            }
            else if (frame == "baseline")
            {
                foreach (ProbeWait w in Poll(() => Rig.PassDrawing && Math.Abs(Rig.PassMapOverride(entry.LayerName)) <= SoloProbe.MapReadTolerance && Rig.PassLayerMask == bit, PollMapS)) yield return w;
            }
            else if (frame == "no_cover")
            {
                foreach (ProbeWait w in Poll(() => Rig.PassDrawing, PollMapS)) yield return w;
            }
            foreach (ProbeWait w in FogSettles()) yield return w;
            if (frame == "no_cover") yield return ProbeWait.Seconds(CoverWaitS);
            yield return ProbeWait.Frames(SettleFrames);
            yield return ProbeWait.Seconds(SettleS);
            fogs[key] = Describe(key);
            foreach (ProbeWait w in Take()) yield return w;
            string picture = taken;
            pictures[key] = picture;
            LogLine("surface: " + key + ": picture " + (picture ?? "none"));
            Leave(frame);
        }

        // One pose: place it, hold it, read the angle to the sun, set the cell and take its frames.
        private IEnumerable<ProbeWait> Pose(string pose, double heading, string[] frames, SoloEntry entry, int bit, Dictionary<string, string> pictures, Dictionary<string, FogReading> fogs, double sun)
        {
            Rig.PlacePose(new ViewerPose { Name = "cb_far", AglM = AglM, ElevationDeg = ElevationDeg, HeadingDeg = heading, CameraDistM = CameraDistM });
            yield return ProbeWait.Frames(2);
            foreach (ProbeWait w in Poll(() => !Rig.PlacementPending && Rig.HoldStable, PollPlaceS)) yield return w;
            yield return ProbeWait.Frames(SettleFrames);
            foreach (ProbeWait w in StillAim()) yield return w;
            ViewerPoseReading aim = Rig.Pose;
            double angle = SunAngleReader();
            Emit(Prefix + pose + "_aim_elevation_deg", aim.ElevationDeg);
            emitted[pose + "_sun_angle_deg"] = angle;
            if (!double.IsNaN(angle)) Emit(Prefix + pose + "_sun_angle_deg", angle);
            LogLine("surface: " + pose + ": heading " + F(heading) + ", the camera's azimuth " + F(aim.AzimuthDeg) + " and elevation " + F(aim.ElevationDeg) + ", " + F(angle) + " deg from the sun");
            Rig.SetSoloCellAhead(CellAheadKm);
            Rig.SetSolo(entry.Layer, entry.Type);
            Rig.SetSoloStrength(0f);
            // The pass and the terrain's cover come back after the switches of the frames before (the stack is rebuilt, the cover repaints within a budget of a few ms a frame).
            foreach (ProbeWait w in Poll(() => Rig.PassDrawing, 30.0)) yield return w;
            if (pose != "p1") yield return ProbeWait.Seconds(CoverWaitS);
            foreach (ProbeWait w in Poll(() => Rig.PassDrawing && Math.Abs(Rig.PassMapOverride(entry.LayerName)) <= SoloProbe.MapReadTolerance && Rig.PassLayerMask == bit, PollMapS)) yield return w;
            Verdict(pose + "_empty_frame", pose + ": the frame has every layer empty", polled, "the solo layer's forced map 0 and the pass's layer mask the layer's bit", "map " + F(Rig.PassMapOverride(entry.LayerName)) + ", mask " + Rig.PassLayerMask);
            foreach (string frame in frames)
                foreach (ProbeWait w in Frame(pose, frame, entry, bit, pictures, fogs)) yield return w;
            ViewerPoseReading end = Rig.Pose;
            Verdict(pose + "_same_aim", pose + ": the camera did not move over the frames", Circular(end.AzimuthDeg, aim.AzimuthDeg) <= AimToleranceDeg && Math.Abs(end.ElevationDeg - aim.ElevationDeg) <= AimToleranceDeg,
                    "within " + F(AimToleranceDeg) + " deg of the pose's first frame", "azimuth " + F(end.AzimuthDeg) + " (" + F(aim.AzimuthDeg) + "), elevation " + F(end.ElevationDeg) + " (" + F(aim.ElevationDeg) + ")");
            double sunEnd = Rig.SunElevationDeg;
            Verdict(pose + "_sun_fixed", pose + ": the sun's elevation did not move over the frames", !double.IsNaN(sunEnd) && Math.Abs(sunEnd - sun) <= SunDriftDeg, F(sun) + " +-" + F(SunDriftDeg) + " deg", F(sunEnd) + " deg");
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            SetSetting(AtmospheresSettings.Keys.CloudSource, (double)(int)CloudSourceKind.Ours);
            SetSetting(AtmospheresSettings.Keys.OwnClouds, 1.0);
            SetSetting(AtmospheresSettings.Keys.CloudBasePhysics, 0.0);
            SetSetting(AtmospheresSettings.Keys.CloudBaseVariation, 0.0);
            SetSetting(AtmospheresSettings.Keys.BaseRagged, 0.0);
            yield return ProbeWait.Until(() => Rig.FlightReady && Rig.PassDrawing, 300, "the flight scene and the own cloud pass drawing");
            reached = true;
            groundCover0 = Rig.Setting(AtmospheresSettings.Keys.GroundCover) > 0.5;
            // The vessel is counted once it is loaded and unpacked (the viewer places nothing before) and again at the end.
            foreach (ProbeWait w in WaitVesselReady()) yield return w;

            Rig.ToggleWindow();
            foreach (ProbeWait w in Poll(() => Rig.WindowOpen && Rig.SessionActive, 10.0)) yield return w;
            if (!Verdict("window_opens", "the viewer opens", polled, "window and session within 10 s", "window " + Rig.WindowOpen + ", session " + Rig.SessionActive)) yield break;
            Rig.FreezeClouds(true);
            Rig.PlacePose(new ViewerPose { Name = "cb_far", AglM = AglM, ElevationDeg = ElevationDeg, HeadingDeg = HeadingToward, CameraDistM = CameraDistM });
            yield return ProbeWait.Frames(2);
            foreach (ProbeWait w in Poll(() => !Rig.PlacementPending && Rig.HoldStable, PollPlaceS)) yield return w;
            // The time: K-S1's own universal time (its pictures' weather at the vessel, so that the old fog rule meets the field it met there) where the sun stands in K-S1's band; else the
            // first offset that does (K-S1's search).
            double sun = double.NaN;
            double natural = Rig.ClockNatural;
            if (!double.IsNaN(natural))
            {
                Rig.SetTimeOffset(K1Ut - natural);
                yield return ProbeWait.Seconds(1.5);
                double e = Rig.SunElevationDeg;
                if (!double.IsNaN(e) && Math.Abs(e - SoloProbe.SunTargetDeg) <= SoloProbe.SunBandDeg) sun = e;
            }
            double previous = double.NaN;
            for (int k = 0; k <= SoloProbe.SunSteps && double.IsNaN(sun); k++)
            {
                Rig.SetTimeOffset(k * SoloProbe.SunStepS);
                yield return ProbeWait.Seconds(1.5);
                double e = Rig.SunElevationDeg;
                if (!double.IsNaN(e) && !double.IsNaN(previous) && Math.Abs(e - SoloProbe.SunTargetDeg) <= SoloProbe.SunBandDeg && e < previous) sun = e;
                previous = e;
            }
            if (!Verdict("sun_found", "a time puts the sun at the stated elevation", !double.IsNaN(sun), F(SoloProbe.SunTargetDeg) + " deg", double.IsNaN(sun) ? "none" : F(sun) + " deg")) yield break;
            Rig.FreezeTime(true);
            yield return ProbeWait.Seconds(1);
            Emit(Prefix + "sun_elevation_deg", sun);
            Emit(Prefix + "ut_s", Rig.GameUt);

            // The type alone, as a cell 60 km ahead of the camera.
            SoloEntry? found = null;
            foreach (SoloEntry e in Rig.SoloEntries)
                if (string.Equals(e.TypeName, TypeName, StringComparison.OrdinalIgnoreCase)) found = e;
            if (!Verdict("type_found", "the cumulonimbus type is in the stack", found.HasValue, TypeName, found.HasValue ? found.Value.LayerName + " / " + found.Value.TypeName : "absent")) yield break;
            SoloEntry entry = found.Value;
            int bit = 1 << entry.Layer;

            var pictures = new Dictionary<string, string>();
            var fogs = new Dictionary<string, FogReading>();
            foreach (ProbeWait w in Pose("p1", HeadingToward, FramesP1, entry, bit, pictures, fogs, sun)) yield return w;
            foreach (ProbeWait w in Pose("p2", HeadingAway, FramesP2, entry, bit, pictures, fogs, sun)) yield return w;
            Tuning.ClearOverride(FogRuleKey);

            // The rule took effect: the frames with it ask for no fog; and whether the old rule asked any (information: a field without rain at the vessel gives none, the frames are then the same).
            bool rule = true;
            foreach (KeyValuePair<string, FogReading> pair in fogs)
                if (pair.Key.EndsWith("_baseline", StringComparison.Ordinal) || pair.Key.EndsWith("_drawn", StringComparison.Ordinal)) rule &= pair.Value.Asked <= 0f;
            Verdict("fog_rule", "the frames with the rule 'a forced level owns the fog' ask for no fog", rule, "the weather view's fog density 0 in every baseline and drawn frame", rule ? "0" : "a frame asks for fog");
            FogReading old;
            bool oldAsks = fogs.TryGetValue("p1_old_fog", out old) && old.Asked > 0f;
            Emit(Prefix + "old_fog_asks_fog", oldAsks);
            if (!oldAsks) LogLine("surface: warning: the old rule asked for no fog either (the field's level at the vessel at this time is 0): old_fog and baseline are the same frame, the fog is not the white of the mountains");

            int written = 0, expected = FramesP1.Length + FramesP2.Length;
            foreach (KeyValuePair<string, string> pair in pictures) if (pair.Value != null) written++;
            Verdict("pictures", "every frame's picture is written", written == expected, expected + " pictures", written + " pictures");
            double a1 = Emitted("p1_sun_angle_deg"), a2 = Emitted("p2_sun_angle_deg");
            Verdict("poses_apart", "the two headings look at the sun from different angles", !double.IsNaN(a1) && !double.IsNaN(a2) && Math.Abs(a1 - a2) >= PosesApartDeg,
                    "at least " + F(PosesApartDeg) + " deg apart", "p1 " + F(a1) + ", p2 " + F(a2));
            Rig.ToggleWindow();
            foreach (ProbeWait w in Poll(() => !Rig.WindowOpen && !Rig.SessionActive, 10.0)) yield return w;
            yield return ProbeWait.Seconds(AfterCloseWaitS);
            JudgeVesselIntact(Prefix);
            Emit(Prefix + "elapsed_s", Elapsed);
        }

        private readonly Dictionary<string, double> emitted = new Dictionary<string, double>();

        private double Emitted(string name)
        {
            double v;
            return emitted.TryGetValue(name, out v) ? v : double.NaN;
        }
    }
}
