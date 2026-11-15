using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Atmospheres.Viewer;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // What the scene's fog reads in one frame of the surface series.
    internal struct FogReading
    {
        internal bool On;
        internal int Mode;
        internal float Density;
        internal Color Colour;
        // What the fog writer holds, and what the weather view asks of it and sees.
        internal float Held, Asked, Level, VisibilityM;
    }

    // The probe of series K-S2 (start "surface", rows tests/series/rows/SURFACEPROBE.json): where the white of the far terrain in the solo pictures comes from. The same pose and sun
    // as the cb_far view of K-S1, one cell 60 km ahead, the vessel held and the time frozen, and the SAME frame four times (every layer empty unless the frame says it is drawn):
    //   old_fog     the fog rule before this package (driver.forcedLevelOwnsFog 0: the field's visibility is the fog's even under a forced level), the baseline of the K-S1 pictures;
    //   baseline    the rule now: a forced level owns the fog;
    //   drawn       the cell drawn alone (the K-S1 picture, the rule now);
    //   no_weather  weatherEffects off (the fog writer and every weather effect off);
    //   no_mod      the whole mod off (Scatterer, TUFX and the game's own air only).
    // Each frame logs 'surface: <frame>: fog ...' (RenderSettings.fog, mode, density, colour, what the fog writer holds, the view's level and visibility, the sun) and takes the viewer's
    // picture; the pictures are compared offline (the far terrain's luminance, chroma and local contrast across the frames, tools/surface_sheet.py). The probe judges that the frames
    // are the same pose, sun and scene (the camera did not move, the sun did not move, the layer is alone) and that each picture is written; it does not judge the look.
    [AtmospheresModule(993)]
    internal sealed class SurfaceProbe : SeriesProbe
    {
        internal const string Series = "K-S2";
        internal const string Prefix = "SURF.";
        // The pose of K-S1's cb_far, and its cell.
        internal const string TypeName = "Cumulonimbus incus";
        internal const double AglM = 300.0, ElevationDeg = 5.0, HeadingDeg = 90.0, CameraDistM = 100.0, CellAheadKm = 60.0;
        // A frame's camera stands within this of the first frame's aim, and the sun within this of its first reading.
        internal const double AimToleranceDeg = 0.5, SunDriftDeg = 1.0;
        // How long the scenario waits for the fog to settle after a switch (the writer slews at 12 % per 3 Hz tick: a few seconds), and the longest wait.
        internal const double FogSettleS = 4.0, FogWaitS = 25.0, PollPlaceS = 60.0, PollMapS = 20.0, PollPictureS = 15.0, SettleS = 3.0;
        internal const int SettleFrames = 60, AimStillMaxFrames = 80, MinPictureBytes = 1000;

        internal static readonly string[] Frames = { "old_fog", "baseline", "drawn", "no_weather", "no_mod" };

        private ISoloRig rig;
        private bool reached;
        private string taken;
        private bool polled;

        internal ISoloRig Rig
        {
            get { return rig ?? (rig = new GameSoloRig()); }
            set { rig = value; }
        }

        // The scene's fog as the game holds it; a test replaces it.
        internal Func<FogReading> FogReader = ReadGameFog;

        protected override string SeriesName
        {
            get { return Series; }
        }

        protected override double MaxSeconds
        {
            get { return 12 * 60; }
        }

        protected override string LostGround()
        {
            if (!reached) return null;
            return Rig.FlightReady ? null : "the flight scene was lost during the scenario";
        }

        private static FogReading ReadGameFog()
        {
            WeatherView view = WeatherHooks.View;
            return new FogReading
            {
                On = RenderSettings.fog, Mode = (int)RenderSettings.fogMode, Density = RenderSettings.fogDensity, Colour = RenderSettings.fogColor,
                Held = FogWriter.WrittenDensity, Asked = view.FogDensity, Level = view.Level, VisibilityM = view.VisibilityM
            };
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

        // What the frame is: the fog the scene holds, the camera, the sun.
        private void Describe(string frame)
        {
            FogReading f = FogReader();
            ViewerPoseReading p = Rig.Pose;
            LogLine("surface: " + frame + ": fog " + (f.On ? "on" : "off") + ", mode " + f.Mode + ", density " + F(f.Density) + ", colour " + F(f.Colour.r) + " " + F(f.Colour.g) + " " + F(f.Colour.b)
                    + ", the fog writer holds " + F(f.Held) + ", the view asks " + F(f.Asked) + ", level " + F(f.Level) + ", visibility " + F(f.VisibilityM) + " m, the field's level at the vessel "
                    + F(Rig.PrecipitationLevel) + "; camera azimuth " + F(p.AzimuthDeg) + " elevation " + F(p.ElevationDeg) + " at " + F(p.CameraAglM) + " m; sun " + F(Rig.SunElevationDeg) + " deg");
            Emit(Prefix + frame + "_fog_density", f.On ? f.Density : 0.0);
            Emit(Prefix + frame + "_fog_asked", f.Asked);
            Emit(Prefix + frame + "_level", f.Level);
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

        private static string Pictured(string picture)
        {
            return picture ?? "none";
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
            yield return ProbeWait.Seconds(3);
            IList<ViewerLayer> layers = Rig.Layers;

            Rig.ToggleWindow();
            foreach (ProbeWait w in Poll(() => Rig.WindowOpen && Rig.SessionActive, 10.0)) yield return w;
            if (!Verdict("window_opens", "the viewer opens", polled, "window and session within 10 s", "window " + Rig.WindowOpen + ", session " + Rig.SessionActive)) yield break;
            Rig.FreezeClouds(true);
            var pose = new ViewerPose { Name = "cb_far", AglM = AglM, ElevationDeg = ElevationDeg, HeadingDeg = HeadingDeg, CameraDistM = CameraDistM };
            Rig.PlacePose(pose);
            yield return ProbeWait.Frames(2);
            foreach (ProbeWait w in Poll(() => !Rig.PlacementPending && Rig.HoldStable, PollPlaceS)) yield return w;
            // The sun: the first offset at which it stands in K-S1's band and goes down (the same search, so that the frames match K-S1's).
            double sun = double.NaN, previous = double.NaN;
            for (int k = 0; k <= SoloProbe.SunSteps && double.IsNaN(sun); k++)
            {
                Rig.SetTimeOffset(k * SoloProbe.SunStepS);
                yield return ProbeWait.Seconds(1.5);
                double e = Rig.SunElevationDeg;
                if (!double.IsNaN(e) && !double.IsNaN(previous) && Math.Abs(e - SoloProbe.SunTargetDeg) <= SoloProbe.SunBandDeg && e < previous) sun = e;
                previous = e;
            }
            if (!Verdict("sun_found", "an offset of the time puts the sun at the stated elevation", !double.IsNaN(sun), F(SoloProbe.SunTargetDeg) + " deg, going down", double.IsNaN(sun) ? "none" : F(sun) + " deg")) yield break;
            Rig.FreezeTime(true);
            yield return ProbeWait.Seconds(1);
            Emit(Prefix + "sun_elevation_deg", sun);

            // The type alone, as a cell 60 km ahead of the camera, at strength 0 (the empty frame) until the frame says it is drawn.
            IList<SoloEntry> entries = Rig.SoloEntries;
            SoloEntry? found = null;
            foreach (SoloEntry e in entries)
                if (string.Equals(e.TypeName, TypeName, StringComparison.OrdinalIgnoreCase)) found = e;
            if (!Verdict("type_found", "the cumulonimbus type is in the stack", found.HasValue, TypeName, found.HasValue ? found.Value.LayerName + " / " + found.Value.TypeName : "absent")) yield break;
            SoloEntry entry = found.Value;
            int bit = 1 << entry.Layer;
            yield return ProbeWait.Frames(SettleFrames);
            foreach (ProbeWait w in StillAim()) yield return w;
            ViewerPoseReading aim = Rig.Pose;
            Emit(Prefix + "aim_elevation_deg", aim.ElevationDeg);
            Rig.SetSoloCellAhead(CellAheadKm);
            Rig.SetSolo(entry.Layer, entry.Type);
            Rig.SetSoloStrength(0f);
            foreach (ProbeWait w in Poll(() => Math.Abs(Rig.PassMapOverride(entry.LayerName)) <= SoloProbe.MapReadTolerance && Rig.PassLayerMask == bit, PollMapS)) yield return w;
            if (!Verdict("empty_frame", "the frame has every layer empty", polled, "the solo layer's forced map 0 and the pass's layer mask the layer's bit", "map " + F(Rig.PassMapOverride(entry.LayerName)) + ", mask " + Rig.PassLayerMask)) yield break;

            var pictures = new Dictionary<string, string>();

            // 1. old_fog: the rule before this package.
            ForceTuning("driver.forcedLevelOwnsFog", 0f);
            foreach (ProbeWait w in Capture("old_fog", pictures)) yield return w;
            // 2. baseline: the rule now.
            ForceTuning("driver.forcedLevelOwnsFog", 1f);
            foreach (ProbeWait w in Capture("baseline", pictures)) yield return w;
            // 3. drawn: the cell alone.
            Rig.SetSoloStrength(1f);
            foreach (ProbeWait w in Poll(() => Math.Abs(Rig.PassMapOverride(entry.LayerName) - 1f) <= SoloProbe.MapReadTolerance && Rig.PassLayerMask == bit, PollMapS)) yield return w;
            Verdict("drawn_alone", "the cell is drawn alone", polled, "the layer's forced map 1 and the pass's layer mask the layer's bit", "map " + F(Rig.PassMapOverride(entry.LayerName)) + ", mask " + Rig.PassLayerMask);
            foreach (ProbeWait w in Capture("drawn", pictures)) yield return w;
            Rig.SetSoloStrength(0f);
            foreach (ProbeWait w in Poll(() => Math.Abs(Rig.PassMapOverride(entry.LayerName)) <= SoloProbe.MapReadTolerance, PollMapS)) yield return w;
            // 4. no_weather: weatherEffects off.
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 0.0);
            foreach (ProbeWait w in Capture("no_weather", pictures)) yield return w;
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            // 5. no_mod: the whole mod off. The viewer's capture may not run then; a screen picture stands in.
            SetSetting(AtmospheresSettings.Keys.Enabled, 0.0);
            foreach (ProbeWait w in Capture("no_mod", pictures)) yield return w;
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);
            yield return ProbeWait.Seconds(2);

            int written = 0;
            foreach (string frame in Frames) if (pictures.ContainsKey(frame) && pictures[frame] != null) written++;
            Verdict("pictures", "every frame's picture is written", written == Frames.Length, Frames.Length + " pictures", written + " pictures");
            Tuning.ClearOverride("driver.forcedLevelOwnsFog");
            ViewerPoseReading end = Rig.Pose;
            Verdict("same_aim", "the camera did not move over the frames", Circular(end.AzimuthDeg, aim.AzimuthDeg) <= AimToleranceDeg && Math.Abs(end.ElevationDeg - aim.ElevationDeg) <= AimToleranceDeg,
                    "within " + F(AimToleranceDeg) + " deg of the first frame", "azimuth " + F(end.AzimuthDeg) + " (" + F(aim.AzimuthDeg) + "), elevation " + F(end.ElevationDeg) + " (" + F(aim.ElevationDeg) + ")");
            double sunEnd = Rig.SunElevationDeg;
            Verdict("sun_fixed", "the sun's elevation did not move over the frames", !double.IsNaN(sunEnd) && Math.Abs(sunEnd - sun) <= SunDriftDeg, F(sun) + " +-" + F(SunDriftDeg) + " deg", F(sunEnd) + " deg");
            Rig.ToggleWindow();
            foreach (ProbeWait w in Poll(() => !Rig.WindowOpen && !Rig.SessionActive, 10.0)) yield return w;
            Emit(Prefix + "elapsed_s", Elapsed);
        }

        // One frame: wait for the scene's fog to settle (gone when the frame has none), describe it and take the picture.
        private IEnumerable<ProbeWait> Capture(string frame, Dictionary<string, string> pictures)
        {
            foreach (ProbeWait w in FogSettles()) yield return w;
            yield return ProbeWait.Frames(SettleFrames);
            yield return ProbeWait.Seconds(SettleS);
            Describe(frame);
            foreach (ProbeWait w in Take()) yield return w;
            string picture = taken;
            if (picture == null && frame == "no_mod")
            {
                string shot = Rig.Screenshot("surface-no-mod.png");
                yield return ProbeWait.Seconds(2);
                picture = shot;
                LogLine("surface: no_mod: the viewer wrote no picture with the mod off; a screen picture stands in: " + shot);
            }
            pictures[frame] = picture;
            LogLine("surface: " + frame + ": picture " + Pictured(picture));
        }
    }
}
