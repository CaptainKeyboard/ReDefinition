using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using KSP.UI.Screens;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres.Forecast
{
    // The probe of package D30 inside the integrated series K-W3a (rows in tests/series/rows/D30.json; the series owns the start, this has
    // none). Active only with -atmoSeries K-W3a on the command line; D36's probe shares the start. Scenes, in this order, each step with its
    // own rows and its own log-error count:
    //   Space Centre   the window opens (D30.window_ksc_*); a storm is forced at the pad (a precipitation disc 8 to 14 h ahead), the clock
    //                  jumps across its ETA, and exactly one message is delivered (D30.alert_*); the same UT again delivers nothing.
    //   Tracking Station  the window with the body selector (D30.window_tracking_*), the overlay's rings against the expected projection.
    //   Flight         a new launch of the template craft (or the flight the start is already in): the window (D30.window_flight_*), the
    //                  HUD line through the main mod's WeatherLine where it is installed (D30.hud_line_*), the stock message app and the
    //                  warp stop (D30.message_app_api, D30.warp_stop_api: the APIs marked [VERIFY] in KspAlertSink), the overlay in the map.
    // "Log errors" are the Error and Exception lines of the log that name ReDefinition between the opening and the closing of the window.
    // Whatever the probe changes (settings, the pinned site, the forced storm) is put back at the end or by the runner's settings snapshot.
    [AtmospheresModule(985)]
    internal sealed class D30Probe : SeriesProbe
    {
        internal const string SeriesId = "K-W3a";
        private const double StormFromHours = 8.0, StormToHours = 14.0, StormRadiusKm = 80.0;
        private const float StormRateMmPerH = 60f;

        private int errors;
        private int shots;
        private bool checklistDone;
        private readonly Application.LogCallback counter;
        private double maxRingErrorPct;
        private int maxRings;

        public D30Probe()
        {
            counter = (condition, stack, type) =>
            {
                if (type != LogType.Error && type != LogType.Exception) return;
                if ((condition != null && condition.Contains("ReDefinition")) || (stack != null && stack.Contains("ReDefinition"))) errors++;
            };
        }

        protected override string SeriesName
        {
            get { return SeriesId; }
        }

        protected override double MaxSeconds
        {
            get { return 14 * 60; }
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            Application.logMessageReceived += counter;
            try
            {
                yield return ProbeWait.Until(() => HighLogic.LoadedScene == GameScenes.SPACECENTER || HighLogic.LoadedSceneIsFlight, 300, "the space centre or the flight");
                // The master switch weatherEffects is off at the start of the series (D36 reads that state in its first seconds in the Space
                // Centre); this probe turns it on once D36 has read it, and the forecast comes up from there.
                yield return ProbeWait.Seconds(40);
                Emit("D30.master_was_off", !AtmospheresSettings.WeatherEffects);
                SetSetting("weatherEffects", 1);
                SetSetting("forecastEnabled", 1);
                yield return ProbeWait.Until(() => ForecastModule.Service != null && WeatherHooks.Forecast != null, 240, "the forecast seam");
                Emit("D30.seam_ready", true);

                bool inFlight = HighLogic.LoadedSceneIsFlight;
                if (!inFlight)
                {
                    foreach (ProbeWait w in WindowPhase("ksc", SiteChoice.Ksc)) yield return w;
                    foreach (ProbeWait w in AlertPhase(SiteChoice.Ksc, "ksc")) yield return w;
                    HighLogic.LoadScene(GameScenes.TRACKSTATION);
                    yield return ProbeWait.Until(() => HighLogic.LoadedScene == GameScenes.TRACKSTATION && PlanetariumCamera.fetch != null, 180, "the tracking station");
                    yield return ProbeWait.Seconds(4);
                    foreach (ProbeWait w in WindowPhase("tracking", SiteChoice.Ksc)) yield return w;
                    foreach (ProbeWait w in OverlayPhase("tracking")) yield return w;
                    HighLogic.LoadScene(GameScenes.SPACECENTER);
                    yield return ProbeWait.Until(() => HighLogic.LoadedScene == GameScenes.SPACECENTER && SpaceCenter.Instance != null, 180, "the space centre again");
                    yield return ProbeWait.Seconds(4);
                    string craft = CraftPath();
                    if (craft == null)
                    {
                        Fail("no craft Kerbal X in the save's Ships/VAB: the flight steps of D30 cannot run");
                        yield break;
                    }
                    ConfigNode node = ConfigNode.Load(craft);
                    FlightDriver.StartWithNewLaunch(craft, "Squad/Flags/default", "LaunchPad", VesselCrewManifest.FromConfigNode(node));
                    yield return ProbeWait.Until(() => HighLogic.LoadedSceneIsFlight && GameGlobals.ActiveVessel != null && GameGlobals.ActiveVessel.loaded, 240, "the flight");
                    yield return ProbeWait.Seconds(8);
                }
                foreach (ProbeWait w in WindowPhase("flight", SiteChoice.Vessel)) yield return w;
                foreach (ProbeWait w in HudPhase()) yield return w;
                foreach (ProbeWait w in StockApiPhase()) yield return w;
                foreach (ProbeWait w in OverlayPhase("map")) yield return w;
                Emit("D30.overlay_rings", maxRings);
                Emit("D30.overlay_ring_error_pct", maxRingErrorPct);
            }
            finally
            {
                Application.logMessageReceived -= counter;
                SetSetting("forecastEnabled", 1);
            }
        }

        // The window in the scene: opens, shows data, closes; the log errors in between.
        private IEnumerable<ProbeWait> WindowPhase(string tag, SiteChoice choice)
        {
            ForecastWindow.CurrentChoice = choice;
            int before = errors;
            string settingsBefore = AtmospheresSettings.Snapshot().ToString();
            ForecastWindow window = ForecastWindow.Instance;
            window.Show();
            yield return ProbeWait.Until(() => window.IsOpen && window.Root != null, 10, "the forecast window in " + tag);
            Emit("D30.window_" + tag + "_opens", true);
            yield return ProbeWait.Until(() => window.View.State == ForecastState.Ready, 120, "a ready forecast in " + tag);
            yield return ProbeWait.Frames(30);
            window.Refresh();
            Emit("D30.window_" + tag + "_ready", window.View.State == ForecastState.Ready);
            Emit("D30.window_" + tag + "_strip_cells", window.View.Strip.Length);
            Emit("D30.window_" + tag + "_rows", window.View.Rows.Length);
            EmitText("D30.window_" + tag + "_next_storm", window.View.NextStormLine);
            if (!checklistDone)
            {
                // The checklist reads the structure of the dialog, which is the same in every scene: once, with the screenshots.
                checklistDone = true;
                List<WindowCheck> checks = ForecastWindowChecks.Run(window.Root, AtmospheresSettings.Snapshot().ToString() == settingsBefore, Shoot);
                yield return ProbeWait.Frames(5);
                foreach (WindowCheck check in checks)
                {
                    Check("D30.dialog." + check.Name, check.Ok, check.Detail);
                    Emit("D30.dialog." + check.Name, check.Ok);
                    Emit("D30.dialog." + check.Name + ".shot", check.Shot);
                }
            }
            window.Close();
            yield return ProbeWait.Frames(5);
            Emit("D30.window_" + tag + "_log_errors", errors - before);
        }

        // A storm is forced at the site; the clock jumps across its ETA; exactly one message comes, and the same UT again brings none.
        private IEnumerable<ProbeWait> AlertPhase(SiteChoice choice, string tag)
        {
            ForecastSite site;
            if (!ForecastSites.TryLocate(choice, out site))
            {
                Fail("the site " + tag + " is unknown");
                yield break;
            }
            SetSetting("alertsEnabled", 1);
            SetSetting("alertLeadHours", 3.0);
            SetSetting("alertMinLevel", (double)(int)LevelChoice.Heavy);
            SetSetting("alertMessageApp", 1);
            SetSetting("alertWarpStop", 0);
            SetSetting("alertSites", choice == SiteChoice.Ksc ? "ksc" : "vessel");
            double now = Planetarium.GetUniversalTime();
            bool forced = ForcePrecipitation(site.BodyIndex, site.LatDeg, site.LonDeg, StormRadiusKm, StormRateMmPerH, PrecipType.Snow,
                now + StormFromHours * 3600.0, now + StormToHours * 3600.0);
            Check("D30.storm_forced", forced);
            Emit("D30.storm_forced", forced);
            if (!forced) yield break;
            WeatherHooks.BumpForecastGeneration();
            ForecastService service = ForecastModule.Service;
            double eta = double.NaN;
            yield return ProbeWait.Until(() =>
            {
                ForecastResult detail = service.DetailOf(site);
                if (detail == null || double.IsNaN(detail.ThresholdEtaUt)) return false;
                eta = detail.ThresholdEtaUt;
                return true;
            }, 90, "the engine to find the forced storm at " + tag);
            Emit("D30.alert_eta_found", true);
            Emit("D30.alert_eta_lead_hours", (eta - now) / 3600.0);
            // The evaluator takes its first tick before the jump, so that the jump is a jump and not a first tick.
            yield return ProbeWait.Seconds(6);
            AlertDelivery delivery = ForecastUiModule.Delivery;
            int log0 = delivery.Log.Count;
            int app0 = AppMessages();
            double target = eta + 1800.0;
            ForceUniversalTime(target);
            yield return ProbeWait.Until(() => WeatherHub.Field.Readiness == FieldReadiness.Ready, 150, "the field to catch up after the jump");
            yield return ProbeWait.Until(() => delivery.Log.Count > log0, 60, "the message of the jump");
            yield return ProbeWait.Seconds(6);
            Emit("D30.alert_messages_after_jump", delivery.Log.Count - log0);
            Emit("D30.alert_summary", delivery.Log.Count > log0 && delivery.Log[delivery.Log.Count - 1].Summary);
            Emit("D30.alert_message_app_count", AppMessages() - app0);
            int log1 = delivery.Log.Count;
            ForceUniversalTime(Planetarium.GetUniversalTime());
            yield return ProbeWait.Seconds(8);
            Emit("D30.alert_repeat_messages", delivery.Log.Count - log1);
        }

        private static int AppMessages()
        {
            MessageSystem system = MessageSystem.Instance;
            if (system == null) return 0;
            return system.FindMessages(m => m.messageTitle == ForecastStrings.MessageTitle).Count;
        }

        // The HUD line of the main mod's flight panel, read through its text function, and its absence with the forecast switched off.
        private IEnumerable<ProbeWait> HudPhase()
        {
            Type line = TypeLookup.Find("ReDefinition.Hud.WeatherLine");
            MethodInfo text = line != null ? line.GetMethod("Text", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static) : null;
            ForecastSite site;
            bool located = ForecastSites.TryLocate(SiteChoice.Vessel, out site);
            double now = Planetarium.GetUniversalTime();
            if (text == null || !located)
            {
                Emit("D30.hud_main_mod", false);
                yield break;
            }
            Emit("D30.hud_main_mod", true);
            string shown = null;
            yield return ProbeWait.Until(() =>
            {
                Func<ForecastSite, ForecastReport> source = WeatherHooks.Forecast;
                shown = source != null ? (string)text.Invoke(null, new object[] { source(site), Planetarium.GetUniversalTime() }) : null;
                return shown != null;
            }, 60, "the weather line");
            EmitText("D30.hud_line_text", shown);
            Emit("D30.hud_line_present", shown != null && shown.StartsWith("Weather: ", StringComparison.Ordinal));
            SetSetting("forecastEnabled", 0);
            yield return ProbeWait.Until(() => WeatherHooks.Forecast == null, 20, "the forecast seam to be taken back");
            Emit("D30.hud_line_absent_when_off", WeatherHooks.Forecast == null && text.Invoke(null, new object[] { null, now }) == null);
            SetSetting("forecastEnabled", 1);
            yield return ProbeWait.Until(() => WeatherHooks.Forecast != null, 60, "the forecast seam to come back");
        }

        // The two stock APIs the alert delivery uses and the build marks [VERIFY]: the message app and the warp stop.
        private IEnumerable<ProbeWait> StockApiPhase()
        {
            var sink = new KspAlertSink();
            int before = AppMessages();
            sink.MessageApp(ForecastStrings.MessageTitle, "Probe check of the message app", AlertKind.Approaching);
            yield return ProbeWait.Frames(3);
            Emit("D30.message_app_api", AppMessages() - before == 1);
            TimeWarp.SetRate(1, true);
            yield return ProbeWait.Frames(3);
            bool running = sink.WarpRunning;
            sink.StopWarp();
            yield return ProbeWait.Frames(3);
            Emit("D30.warp_stop_api", running && !sink.WarpRunning);
        }

        // The overlay in the map view: its rings at four times of a warp against the projection of the tracks' own positions.
        private IEnumerable<ProbeWait> OverlayPhase(string tag)
        {
            if (HighLogic.LoadedSceneIsFlight && !MapView.MapIsEnabled) MapView.EnterMapView();
            CelestialBody home = GameGlobals.HomeBody;
            if (home != null && PlanetariumCamera.fetch != null) PlanetariumCamera.fetch.SetTarget(home);
            yield return ProbeWait.Until(() => MapOverlay.Instance != null && MapOverlay.Instance.Drawing, 30, "the overlay in " + tag);
            Emit("D30.overlay_drawing_" + tag, true);
            var projector = new KspMapProjector();
            double start = Planetarium.GetUniversalTime();
            for (int k = 0; k < 4; k++)
            {
                if (k > 0)
                {
                    ForceUniversalTime(start + k * 3600.0);
                    yield return ProbeWait.Until(() => WeatherHub.Field.Readiness == FieldReadiness.Ready, 150, "the field after the warp step");
                }
                yield return ProbeWait.Frames(4);
                // The scene is rebuilt every half second of real time: wait until it stands at the game's time, then compare against the
                // game's time (not the scene's own), so that a scene that lags a warp shows as an error.
                yield return ProbeWait.Until(() => Math.Abs(MapOverlay.Instance.Scene.Ut - Planetarium.GetUniversalTime()) < 2.0, 5, "the overlay at the game time");
                OverlayScene scene = MapOverlay.Instance.Scene;
                double gameUt = Planetarium.GetUniversalTime();
                ForecastTrack[] tracks = WeatherHooks.BodyTracks != null ? WeatherHooks.BodyTracks(scene.BodyIndex) : null;
                maxRings = Math.Max(maxRings, scene.Rings.Count);
                if (tracks == null) continue;
                foreach (OverlayRing ring in scene.Rings)
                {
                    ForecastTrack track = Array.Find(tracks, t => t.CellKey == ring.Key);
                    double lat, lon;
                    float radius, strength, x, y;
                    if (track == null || !SystemTracker.PositionAt(track, gameUt, out lat, out lon, out radius, out strength)) continue;
                    if (!projector.Project(scene.BodyIndex, lat, lon, out x, out y)) continue;
                    double dx = ring.Centre.X - x, dy = ring.Centre.Y - y;
                    maxRingErrorPct = Math.Max(maxRingErrorPct, Math.Sqrt(dx * dx + dy * dy) / Screen.height * 100.0);
                }
            }
            if (HighLogic.LoadedSceneIsFlight && MapView.MapIsEnabled) MapView.ExitMapView();
        }

        // A screenshot of the screen, numbered in order.
        private int Shoot(string item)
        {
            shots++;
            string folder = Path.Combine(KSPUtil.ApplicationRootPath, "Screenshots");
            Directory.CreateDirectory(folder);
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "atmo-D30-" + shots.ToString("00") + "-" + item + ".png"));
            return shots;
        }

        // The template craft in the save's Ships/VAB, as the launch of the main mod's probes finds it.
        private static string CraftPath()
        {
            string path = Path.Combine(KSPUtil.ApplicationRootPath, "saves", HighLogic.SaveFolder, "Ships", "VAB", "Kerbal X.craft");
            return File.Exists(path) ? path : null;
        }
    }
}
