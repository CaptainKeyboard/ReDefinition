using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Series K-WB (audit A2, owner 2026-11-19: the scene's white balance must hold on every body with clouds, not only on Kerbin): the main mod's probe sets the
    // command pod down on the body the start names (-redefinitionProbeLand <body>). This probe opens the weather field's and the own pass's switches (as
    // T09Probe does), picks the time whose cloud cover at the pod is nearest one half among those with the sun 25 to 45 degrees up (a clear sky compares nothing, a closed cloud layer shows no mounds), sets the
    // pod down 40 m above the ground and holds it (the camera stands above grass and scatter), waits for the pass and saves the screen without the UI as the
    // player sees it from two camera poses, each at the slider's strengths 0, 0.25, 0.5 and 1.
    // Pictures: wb-<body>-p<pose>-s<strength in percent>.png in the series picture folder. Rows: WB.body, WB.cover, WB.sun_elev_deg, WB.tint_r/g/b, WB.pictures.
    [AtmospheresModule(996)]
    internal sealed class WbProbe : SeriesProbe
    {
        // Pitch (negative looks up past the pod), distance in m, heading: the sky above the horizon and the horizon itself.
        private static readonly double[][] Poses = { new[] { -20.0, 12.0, 0.0 }, new[] { -6.0, 12.0, 60.0 } };
        // The strengths of the slider sceneWhiteBalance each pose is shot at.
        private static readonly double[] Strengths = { 0.0, 0.25, 0.5, 1.0 };
        private const double SunLowDeg = 25.0, SunHighDeg = 45.0, SettleS = 4.0, PassWaitS = 120.0, MapSettleS = 20.0, FieldWaitS = 90.0, PodAboveGroundM = 40.0, TargetCover = 0.5;

        private readonly KspT09World game = new KspT09World();
        private int pictures;

        protected override string SeriesName
        {
            get { return "K-WB"; }
        }

        protected override double MaxSeconds
        {
            get { return 15 * 60; }
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            yield return ProbeWait.Until(InFlightReady, 600, "the flight with the pod");
            foreach (ProbeWait w in WaitVesselReady(120)) yield return w;
            yield return ProbeWait.Seconds(5);
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || FlightGlobals.ActiveVessel == null)
            {
                Check("the probe runs in the flight scene", false, HighLogic.LoadedScene.ToString());
                yield break;
            }
            // The switches the weather field and the cloud renderers depend on (CloudGates), as T09Probe.ApplyProductSwitches and UseOwn set them.
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            SetSetting(AtmospheresSettings.Keys.GenEnabled, 1.0);
            SetSetting(AtmospheresSettings.Keys.OwnClouds, 1.0);
            SetSetting(AtmospheresSettings.Keys.CloudSource, (double)(int)CloudSourceKind.ReDefinition);
            SetSetting(AtmospheresSettings.Keys.SceneWhiteBalance, 0.25);
            EmitText("WB.gates", CloudGates.Describe(AtmospheresSettings.Number));
            Check("the weather field's gate is open (enabled, weatherEffects, genEnabled)", CloudGates.GeneratorInstalled(AtmospheresSettings.Number),
                  CloudGates.Describe(AtmospheresSettings.Number));
            Check("the own pass's switches are on (ownClouds, source Ours)", CloudGates.OwnPassSwitches(AtmospheresSettings.Number, EveRenderCompat.Blocked),
                  CloudGates.Describe(AtmospheresSettings.Number));
            CelestialBody body = FlightGlobals.ActiveVessel.mainBody;
            int bodyIndex = body.flightGlobalsIndex;
            EmitText("WB.body", body.bodyName);

            // The time: over two of the body's days in steps of 1/96 of a day, among the candidates with the sun 25 to 45 degrees up at the pod, the one whose cloud
            // cover at 3 km there is nearest TargetCover (the weather field's answer, WeatherHub.Sample, as T09Probe.ChooseUt reads it). The field may need some seconds after
            // the start to answer; up to FieldWaitS are given to it. The sun's height of a candidate is computed from the body's turn, not by setting the clock.
            Vessel pod = FlightGlobals.ActiveVessel;
            double podLat = pod.latitude, podLon = pod.longitude;
            CheatOptions.NoCrashDamage = true;
            CheatOptions.IgnoreMaxTemperature = true;
            // The step from the body's own turn, which SunElevationAt turns the pod by (a tidally locked moon's solar day can be infinite).
            double turn = body.rotationPeriod;
            if (double.IsNaN(turn) || double.IsInfinity(turn) || turn < 600.0) turn = 21600.0;
            double step = Math.Max(turn / 96.0, 60.0);
            double now = game.UniversalTime, bestUt = now, bestCover = -1.0;
            int answered = 0;
            double fieldEnd = Elapsed + FieldWaitS;
            while (true)
            {
                answered = 0;
                for (int k = 0; k < 192; k++)
                {
                    double t = now + k * step;
                    double el = SunElevationAt(body, podLat, podLon, t, turn);
                    if (el < SunLowDeg || el > SunHighDeg) continue;
                    WeatherSample ws;
                    if (!WeatherHub.Sample(bodyIndex, podLat, podLon, 3000.0, t, out ws)) continue;
                    answered++;
                    // The cover nearest TargetCover: single mounds with sky between them (the owner, 2026-11-19: a closed cloud layer shows no puffs and no detail).
                    if (bestCover < 0.0 || Math.Abs(ws.CloudCover01 - TargetCover) < Math.Abs(bestCover - TargetCover))
                    {
                        bestCover = ws.CloudCover01;
                        bestUt = t;
                    }
                }
                if (answered > 0 || Elapsed >= fieldEnd) break;
                yield return ProbeWait.Seconds(1);
            }
            Emit("WB.cover_candidates", answered);
            Emit("WB.cover", bestCover);
            Check("the weather field answered for a time with the sun 25 to 45 degrees up", answered > 0, answered.ToString(CultureInfo.InvariantCulture));
            if (answered == 0) yield break;
            game.UniversalTime = bestUt;
            yield return ProbeWait.Frames(6);
            // The closed-form sun height is checked against the game's own, and the sun must not stand behind another body (a moon's parent: Laythe behind Jool
            // drew a night sky with the sun 27 degrees up on 2026-11-19): otherwise the clock is stepped until both hold.
            for (int i = 0; i < 192; i++)
            {
                double el = game.SunElevationDeg();
                string blocker = SunBlockedBy(FlightGlobals.ActiveVessel);
                if (el >= SunLowDeg && el <= SunHighDeg && blocker == null) break;
                if (i == 0)
                    LogLine("Probe: at the chosen time the sun stands " + el.ToString("F1", CultureInfo.InvariantCulture) + " degrees up"
                            + (blocker != null ? " behind " + blocker : "") + "; stepping the clock");
                game.UniversalTime = game.UniversalTime + step;
                yield return ProbeWait.Frames(3);
            }
            string hiddenBy = SunBlockedBy(FlightGlobals.ActiveVessel);
            Check("the sun is not hidden behind another body", hiddenBy == null, hiddenBy ?? "");
            if (FlightGlobals.ActiveVessel == null)
            {
                Check("the pod survives the clock", false, "no active vessel");
                yield break;
            }
            // Set down above the ground (the camera then stands above grass and scatter) and held there, as T09Probe holds its views.
            game.PlaceVessel(podLat, podLon, game.TerrainAltitude(podLat, podLon) + PodAboveGroundM);
            game.StartHold();
            double holdEnd = Elapsed + 30.0;
            while (Elapsed < holdEnd && !game.HoldStable) yield return ProbeWait.Frames(1);
            Check("the pod is held", game.HoldStable, "");
            double sun = game.SunElevationDeg();
            Emit("WB.sun_elev_deg", sun);
            Check("the sun stands 25 to 45 degrees up", sun >= SunLowDeg - 2.0 && sun <= SunHighDeg + 2.0, sun.ToString("F1", CultureInfo.InvariantCulture));

            // Wait for the pass without failing the start on the timeout; its reason is logged every 10 s and named by the check below.
            double passEnd = Elapsed + PassWaitS, nextNote = Elapsed + 10.0;
            while (Elapsed < passEnd && !(OwnCloudModule.Current != null && OwnCloudModule.Current.Stats.Mode != "off"))
            {
                if (Elapsed >= nextNote)
                {
                    LogLine("Probe: the own pass is off: " + (OwnCloudModule.Current != null ? OwnCloudModule.Current.Stats.Why : "no module"));
                    nextNote = Elapsed + 10.0;
                }
                yield return ProbeWait.Seconds(1);
            }
            OwnCloudModule own = OwnCloudModule.Current;
            bool drawing = own != null && own.Stats.Mode != "off";
            Check("the own cloud pass draws on " + body.bodyName, drawing, own != null ? own.Stats.Why : "no module");
            if (!drawing) yield break;
            yield return ProbeWait.Seconds(MapSettleS);

            game.UseFreeCamera();
            bool hid = HideUi();
            for (int p = 0; p < Poses.Length; p++)
            {
                game.CameraHeading = (float)(Poses[p][2] * Math.PI / 180.0);
                game.CameraPitch = (float)(Poses[p][0] * Math.PI / 180.0);
                game.SetCameraDistance(Poses[p][1]);
                // One picture per strength of the slider (owner 2026-11-19: the full balance is too strong on every body; the default is chosen from these).
                foreach (double strength in Strengths)
                {
                    SetSetting(AtmospheresSettings.Keys.SceneWhiteBalance, strength);
                    bool wanted = strength > 0.001;
                    double end = Elapsed + 10.0;
                    yield return ProbeWait.Seconds(SettleS);
                    while (Elapsed < end && own.Stats.SceneWhiteBalance != wanted) yield return ProbeWait.Frames(1);
                    Check("pose " + p + " strength " + strength.ToString("F2", CultureInfo.InvariantCulture) + ": the scene's balance is " + (wanted ? "on" : "off"),
                          own.Stats.SceneWhiteBalance == wanted, "Stats.SceneWhiteBalance");
                    if (p == 0 && wanted && strength > 0.99)
                    {
                        Color t = own.Stats.SceneTint;
                        Emit("WB.tint_r", t.r);
                        Emit("WB.tint_g", t.g);
                        Emit("WB.tint_b", t.b);
                    }
                    foreach (ProbeWait w in Shoot(body.bodyName, p, "s" + ((int)Math.Round(strength * 100)).ToString("D3", CultureInfo.InvariantCulture))) yield return w;
                }
            }
            if (hid) ShowUi();
            SetSetting(AtmospheresSettings.Keys.SceneWhiteBalance, 0.25);
            game.StopHold();
            Emit("WB.pictures", pictures);
            Check("every pose has a picture per strength", pictures == Strengths.Length * Poses.Length, pictures.ToString(CultureInfo.InvariantCulture));
        }

        // The sun's height in degrees at a point of the body's surface at the time t: the body's turn carries the point round its axis; the sun's direction is
        // taken as it is now (the body's orbit moves it by about a degree over two days at most).
        private static double SunElevationAt(CelestialBody body, double lat, double lon, double t, double turnS)
        {
            CelestialBody sun = GameGlobals.Sun;
            if (sun == null) return 90.0;
            double turnDeg = (t - Planetarium.GetUniversalTime()) / turnS * 360.0;
            Vector3d up = body.GetSurfaceNVector(lat, lon);
            Vector3d axis = body.angularVelocity.normalized;
            if (axis.sqrMagnitude < 0.5) axis = Vector3d.up;
            up = QuaternionD.AngleAxis(turnDeg, axis) * up;
            Vector3d toSun = (sun.position - body.position).normalized;
            return Math.Asin(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(toSun, up)))) * 180.0 / Math.PI;
        }

        // The body whose sphere stands between the vessel and the sun, or null: every body but the sun and the vessel's own.
        private static string SunBlockedBy(Vessel v)
        {
            CelestialBody sun = GameGlobals.Sun;
            if (sun == null || v == null) return null;
            Vector3d from = v.GetWorldPos3D(), to = sun.position;
            Vector3d dir = (to - from).normalized;
            double length = (to - from).magnitude;
            foreach (CelestialBody b in FlightGlobals.Bodies)
            {
                if (b == null || b == sun || b == v.mainBody) continue;
                Vector3d rel = b.position - from;
                double along = Vector3d.Dot(rel, dir);
                if (along <= 0.0 || along >= length) continue;
                if ((rel - dir * along).magnitude < b.Radius) return b.bodyName;
            }
            return null;
        }

        private static bool HideUi()
        {
            KSP.UI.UIMasterController ui = KSP.UI.UIMasterController.Instance;
            if (ui == null || !ui.IsUIShowing) return false;
            ui.HideUI();
            return true;
        }

        private static void ShowUi()
        {
            if (KSP.UI.UIMasterController.Instance != null) KSP.UI.UIMasterController.Instance.ShowUI();
        }

        private IEnumerable<ProbeWait> Shoot(string body, int pose, string which)
        {
            string name = "wb-" + body + "-p" + pose + "-" + which + ".png";
            try
            {
                Directory.CreateDirectory(SeriesPictures.Folder);
                ScreenCapture.CaptureScreenshot(Path.Combine(SeriesPictures.Folder, name));
                pictures++;
            }
            catch (Exception e)
            {
                Check("picture " + name, false, e.GetType().Name + " " + e.Message);
            }
            // CaptureScreenshot writes at the end of the frame; two frames let it land.
            yield return ProbeWait.Frames(2);
        }
    }
}
