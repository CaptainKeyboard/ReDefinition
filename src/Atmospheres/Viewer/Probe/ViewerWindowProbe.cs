using System;
using System.Collections.Generic;
using System.Globalization;

namespace ReDefinition.Atmospheres.Viewer
{
    // What one look at the open window read: its box on the screen against the screen's size.
    internal struct WindowFitReading
    {
        internal bool Measured, Inside;
        internal ScreenBox Box;
        internal int ScreenW, ScreenH;
        internal float UiScale;
        internal string Expected, Read;
    }

    // The check both window starts of K-V1 make: the viewer's window, measured on the screen as the game draws it, lies wholly inside the screen. A window that cannot be measured fails
    // (the check cannot pass on a window it did not see).
    internal static class ViewerWindowCheck
    {
        private static string F(double v)
        {
            return v.ToString("0.#", CultureInfo.InvariantCulture);
        }

        internal static WindowFitReading Read(IViewerRig rig)
        {
            var r = new WindowFitReading { ScreenW = rig.ScreenWidth, ScreenH = rig.ScreenHeight, UiScale = rig.UiScale };
            r.Measured = rig.WindowBox(out r.Box);
            r.Inside = r.Measured && WindowPlacement.Inside(r.Box, r.ScreenW, r.ScreenH);
            r.Expected = "x from 0 to " + r.ScreenW + " and y from 0 to " + r.ScreenH + " px (screen " + r.ScreenW + "x" + r.ScreenH + ", UI scale " + F(r.UiScale) + ")";
            r.Read = r.Measured ? "x " + F(r.Box.XMin) + " to " + F(r.Box.XMax) + ", y " + F(r.Box.YMin) + " to " + F(r.Box.YMax) + " px" : "the window's box could not be measured";
            return r;
        }
    }

    // The second start of series K-V1 ("wide", rows tests/series/rows/VPROBE.json): the owner's own screen, 2560x1440 at KSP's UI scale 1.5. It opens the viewer's window, lets it settle,
    // places the ground pose (the window is built anew when the layers' list changes) and reads where the window stands against the screen. The first start's full scenario
    // (ViewerProbe) makes the same check at 1707x960.
    [AtmospheresModule(990)]
    internal sealed class ViewerWindowProbe : SeriesProbe
    {
        internal const string Series = "K-V1";
        internal const string Prefix = "VIEWERWIN.";
        internal const int SettleFrames = 60;

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
            get { return 3 * 60; }
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
            LogLine("viewerwin: " + what + ": expected " + expected + ", read " + read + (ok ? " - ok" : " - FAILED"));
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

        private void Read(string tag)
        {
            WindowFitReading win = ViewerWindowCheck.Read(Rig);
            Verdict(tag + "_inside_screen", tag + ": the window lies wholly inside the screen", win.Inside, win.Expected, win.Read);
            Info(tag + "_x_min_px", win.Box.XMin);
            Info(tag + "_x_max_px", win.Box.XMax);
            Info(tag + "_y_min_px", win.Box.YMin);
            Info(tag + "_y_max_px", win.Box.YMax);
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            SetSetting(AtmospheresSettings.Keys.CloudSource, (double)(int)CloudSourceKind.Ours);
            SetSetting(AtmospheresSettings.Keys.OwnClouds, 1.0);
            yield return ProbeWait.Until(() => Rig.FlightReady && Rig.PassDrawing, 300, "the flight scene and the own cloud pass drawing");
            reached = true;
            // The vessel is counted once it is loaded and unpacked (the viewer places nothing before) and again at the end.
            foreach (ProbeWait w in WaitVesselReady()) yield return w;
            Info("screen_width_px", Rig.ScreenWidth);
            Info("screen_height_px", Rig.ScreenHeight);
            Info("ui_scale", Rig.UiScale);

            Rig.ToggleWindow();
            foreach (ProbeWait w in Poll(() => Rig.WindowOpen && Rig.SessionActive && Rig.WindowVisible, 10.0)) yield return w;
            if (!Verdict("window_opens", "Toggle opens the window and starts the session", polled, "window open, visible, session active",
                         "window " + Rig.WindowOpen + ", visible " + Rig.WindowVisible + ", session " + Rig.SessionActive)) yield break;
            yield return ProbeWait.Frames(SettleFrames);
            Read("opened");

            // The ground pose: the vessel is placed, the camera aimed, and the window built again where the layers' list changed.
            IList<ViewerLayer> layers = Rig.Layers;
            ViewerPose ground = default(ViewerPose);
            foreach (ViewerPose p in PosePresets.For(layers)) if (p.Name == "ground") ground = p;
            Rig.PlacePose(ground);
            yield return ProbeWait.Frames(2);
            foreach (ProbeWait w in Poll(() => !Rig.PlacementPending && Rig.HoldStable, 60.0)) yield return w;
            yield return ProbeWait.Frames(SettleFrames);
            Read("placed");
            string shot = Rig.Screenshot("viewer-window-wide.png");
            yield return ProbeWait.Frames(20);
            Verdict("window_screenshot", "the screenshot of the open window is written", Rig.FileSize(shot) > 1000, "a file of more than 1000 bytes", Rig.FileSize(shot) + " bytes");

            Rig.ToggleWindow();
            foreach (ProbeWait w in Poll(() => !Rig.WindowOpen && !Rig.SessionActive, 10.0)) yield return w;
            Verdict("window_closes", "a second Toggle closes the window and ends the session", polled, "no window, no session", "window " + Rig.WindowOpen + ", session " + Rig.SessionActive);
            yield return ProbeWait.Seconds(AfterCloseWaitS);
            JudgeVesselIntact(Prefix);
            Info("elapsed_s", Elapsed);
        }
    }
}
