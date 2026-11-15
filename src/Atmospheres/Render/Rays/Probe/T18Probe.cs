using System;
using System.Collections.Generic;
using System.IO;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The probe of package T18 inside the integrated series K-T3 (rows tests/series/rows/T18.json): the own layers in the main mod's traced rays in the
    // running game. The start pins raytracing Full: the pack is kept only while the rays trace (RayCloudSeam.Asked). It runs one pad flight and measures,
    // in this order:
    //   0. the clock: an hour whose cover over the vessel is 0.4 to 0.7 (nearest 0.55) with the sun 25 to 60 degrees up, from the weather field;
    //   1. the switch off: no pack;
    //   2. the switch on: the wall time until the first pack stands, the bake's main-thread time and its worst tick, the upload;
    //   3. the cost of the own layers in the rays: interleaved windows of the same traced scene with the pack in the rays and hidden from them (the pack stays,
    //      RtOwnClouds.HideFromRays), at 4, 8 and 16 taps; the bake's main-thread time per frame over those windows; the age of the pack over a refresh cycle;
    //   4. the pack's facts: the layers in it against the stack's, the texture;
    //   5. the rays: they ask, take the pack into their frames, and the proxy reports no problem with it (a texture it cannot share drops every frame); the
    //      view's frame (the vessel's place in the map frame against its latitude and longitude), shell and size, and the header row's numbers;
    //   6. the five places round the vessel (RtPackAgreement.Pick, sun 45 degrees, 6 degrees at the terminator): |T_ray - T_field| of the sun's transmittance
    //      against the cloud shadow field at 64 steps a span, and against the field at the shadow pass's 8 steps; and the scan of the whole lattice; each for the
    //      pack that stands and for a pack baked in the same call (no age);
    //   7. pictures with the layers in the rays, then the layer cap 2, then the switch off and the same pictures without them.
    // Frame times are the CPU's (Time.unscaledDeltaTime) and carry the GPU where it bounds the frame; the series pins no frame cap.
    [AtmospheresModule(991)]
    internal sealed class T18Probe : SeriesProbe
    {
        internal const string Series = "K-T3";

        // The clock's search: hours from the next full hour, the cover band and its target, the sun band, the clock tries.
        private const int ClockHours = 96, ClockTries = 12;
        private const double CoverMin = 0.4, CoverMax = 0.7, CoverTarget = 0.55, SunMinDeg = 25.0, SunMaxDeg = 60.0, FieldWaitS = 60.0;
        // The precipitation level (0 to 1) above which an hour counts as wet at the vessel.
        private const float DryLevelMax = 0.02f;

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
            return null;
        }

        private static double FrameMs(float fromTime, int fromFrame)
        {
            int frames = Time.frameCount - fromFrame;
            return frames <= 0 ? 0.0 : (Time.realtimeSinceStartup - fromTime) * 1000.0 / frames;
        }

        // One window of the cost measurement: `settle` frames unmeasured (the first frames after a switch carry the switch's own work), then `frames` frames each
        // timed, ms. The window's figure is the MEDIAN frame time (windowMs): a frame that another process's GPU work or a garbage collection stretched does not
        // move it, as it moves a mean. windowMax is the longest frame, for the log.
        private double windowMs, windowMax;
        private readonly List<double> frameTimes = new List<double>();

        private IEnumerable<ProbeWait> Window(int settle, int frames)
        {
            yield return ProbeWait.Frames(settle);
            frameTimes.Clear();
            for (int i = 0; i < frames; i++)
            {
                yield return ProbeWait.Frames(1);
                frameTimes.Add(Time.unscaledDeltaTime * 1000.0);
            }
            windowMax = 0.0;
            foreach (double t in frameTimes) windowMax = Math.Max(windowMax, t);
            windowMs = Median(frameTimes);
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return 0.0;
            var sorted = new List<double>(values);
            sorted.Sort();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : 0.5 * (sorted[mid - 1] + sorted[mid]);
        }

        private void EmitScan(string key, RtAgreementScan scan)
        {
            Emit(key + "_places", scan.Places);
            Emit(key + "_median", scan.Median);
            Emit(key + "_p90", scan.P90);
            Emit(key + "_worst", scan.Worst);
            Emit(key + "_tau_ratio", scan.TauRatioMedian);
        }

        private static double SunElevationDeg(Vessel v)
        {
            CelestialBody sun = GameGlobals.Sun;
            if (sun == null || v == null || v.mainBody == null) return 90.0;
            Vector3d toSun = (sun.position - v.GetWorldPos3D()).normalized;
            Vector3d up = (v.GetWorldPos3D() - v.mainBody.position).normalized;
            return Math.Asin(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(toSun, up)))) * 180.0 / Math.PI;
        }

        private bool clockOk;
        private double clockCover, clockSun;

        // Step 0: the field asked at the vessel, 3 km up, for every hour; the hours with a cover in the band nearest to the target first, the clock set to
        // each until the sun stands in its band.
        private IEnumerable<ProbeWait> ChooseClock(Vessel vessel)
        {
            clockOk = false;
            int body = vessel.mainBody.flightGlobalsIndex;
            double baseUt = Math.Ceiling(Planetarium.GetUniversalTime() / 3600.0) * 3600.0;
            var answered = new List<KeyValuePair<double, double>>();
            var wet = new HashSet<double>();
            double giveUp = Elapsed + FieldWaitS;
            while (true)
            {
                answered.Clear();
                wet.Clear();
                for (int k = 0; k < ClockHours; k++)
                {
                    WeatherSample s;
                    double t = baseUt + k * 3600.0;
                    if (!WeatherHub.Sample(body, vessel.latitude, vessel.longitude, 3000.0, t, out s)) continue;
                    answered.Add(new KeyValuePair<double, double>(t, s.CloudCover01));
                    if (s.Level01 > DryLevelMax) wet.Add(t);
                }
                if (answered.Count == ClockHours || Elapsed >= giveUp) break;
                yield return ProbeWait.Seconds(1);
            }
            Emit("T18.clock_answered", answered.Count);
            Emit("T18.clock_wet_hours", wet.Count);
            // An hour with precipitation over the vessel has a rain deck under the layers: the pictures could not tell the own clouds from it.
            var order = answered.FindAll(c => c.Value >= CoverMin && c.Value <= CoverMax && !wet.Contains(c.Key));
            order.Sort((a, b) =>
            {
                int byCover = Math.Abs(a.Value - CoverTarget).CompareTo(Math.Abs(b.Value - CoverTarget));
                return byCover != 0 ? byCover : a.Key.CompareTo(b.Key);
            });
            Emit("T18.clock_in_band", order.Count);
            int tried = 0;
            foreach (KeyValuePair<double, double> c in order)
            {
                if (tried++ >= ClockTries) break;
                ForceUniversalTime(c.Key);
                yield return ProbeWait.Frames(6);
                double sun = SunElevationDeg(vessel);
                if (sun < SunMinDeg || sun > SunMaxDeg) continue;
                clockOk = true;
                clockCover = c.Value;
                clockSun = sun;
                break;
            }
        }

        // A picture of the screen as the player sees it, into the series' pictures (collected to .local/work/series-pictures/K-T3).
        private IEnumerable<ProbeWait> Picture(string view, string state)
        {
            string name = "t18-" + view + "-" + state + "-" + Screen.width + "x" + Screen.height + "-game.png";
            try
            {
                Directory.CreateDirectory(SeriesPictures.Folder);
                ScreenCapture.CaptureScreenshot(Path.Combine(SeriesPictures.Folder, name));
            }
            catch (Exception e)
            {
                EmitText("T18.picture_fault", name + ": " + e.GetType().Name + " " + e.Message);
            }
            yield return ProbeWait.Frames(4);
        }

        // The two views: the flight camera as the start has it (the vessel's glossy parts, the ground under the clouds), and the camera turned level
        // to the horizon (the sky, the far ground and the sea seen through the rays).
        // The horizon is taken at four headings a quarter turn apart, since which way the sea and the far ground lie from the pad is not known to the probe.
        // The sea views: the camera 400 m out and 0.45 rad up, looking down on the water at a glancing angle (the water's mirror image of the sky is where the rays'
        // clouds show: the pad's camera at 23 m sees a few pixels of sea). Again four headings, since which way the sea lies is not known to the probe.
        private static readonly string[] Views = { "pad", "horizon-h0", "horizon-h1", "horizon-h2", "horizon-h3", "sea-h0", "sea-h1", "sea-h2", "sea-h3" };

        private IEnumerable<ProbeWait> Pictures(string state, float hdg, float pitch)
        {
            FlightCamera camera = FlightCamera.fetch;
            if (camera == null) yield break;
            camera.camHdg = hdg;
            camera.camPitch = pitch;
            yield return ProbeWait.Frames(30);
            foreach (ProbeWait w in Picture(Views[0], state)) yield return w;
            camera.camPitch = 0.02f;
            for (int k = 0; k < 4; k++)
            {
                camera.camHdg = hdg + k * (float)(Math.PI / 2.0);
                yield return ProbeWait.Frames(30);
                foreach (ProbeWait w in Picture(Views[1 + k], state)) yield return w;
            }
            float distance = camera.Distance;
            camera.SetDistance(400f);
            camera.camPitch = 0.45f;
            for (int k = 0; k < 4; k++)
            {
                camera.camHdg = hdg + k * (float)(Math.PI / 2.0);
                yield return ProbeWait.Frames(30);
                foreach (ProbeWait w in Picture(Views[5 + k], state)) yield return w;
            }
            camera.SetDistance(distance);
            camera.camHdg = hdg;
            camera.camPitch = pitch;
        }

        // The mean absolute difference of the colour of two pictures, 0 to 1, over every fifth texel; -1 where one cannot be read. The pictures are what
        // the player would see: the metric says whether the rays' clouds changed the screen at all (an on/off pair that does not differ proves nothing).
        private double PictureDifference(string view)
        {
            string on = Path.Combine(SeriesPictures.Folder, "t18-" + view + "-on-" + Screen.width + "x" + Screen.height + "-game.png");
            string off = Path.Combine(SeriesPictures.Folder, "t18-" + view + "-off-" + Screen.width + "x" + Screen.height + "-game.png");
            var a = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var b = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!File.Exists(on) || !File.Exists(off)) return -1.0;
                if (!ImageConversion.LoadImage(a, File.ReadAllBytes(on)) || !ImageConversion.LoadImage(b, File.ReadAllBytes(off))) return -1.0;
                if (a.width != b.width || a.height != b.height) return -1.0;
                Color32[] pa = a.GetPixels32(), pb = b.GetPixels32();
                double sum = 0.0;
                int n = 0;
                for (int i = 0; i < pa.Length; i += 5)
                {
                    sum += (Math.Abs(pa[i].r - pb[i].r) + Math.Abs(pa[i].g - pb[i].g) + Math.Abs(pa[i].b - pb[i].b)) / (3.0 * 255.0);
                    n++;
                }
                return n == 0 ? -1.0 : sum / n;
            }
            catch (Exception e)
            {
                EmitText("T18.picture_fault", view + ": " + e.GetType().Name + " " + e.Message);
                return -1.0;
            }
            finally
            {
                UnityEngine.Object.Destroy(a);
                UnityEngine.Object.Destroy(b);
            }
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            OwnCloudModule module = null;
            // The switches the series declares for this start (productSwitches): the series pins none of them in the settings file.
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            SetSetting(AtmospheresSettings.Keys.CloudSource, (double)(int)CloudSourceKind.Ours);
            SetSetting(AtmospheresSettings.Keys.OwnClouds, 1.0);
            SetSetting(AtmospheresSettings.Keys.RtOwnClouds, 0);
            yield return ProbeWait.Until(() => { module = OwnCloudModule.Current; return HighLogic.LoadedScene == GameScenes.FLIGHT && InFlightReady() && module != null && module.Stack != null && module.Stack.LayerCount > 0; },
                                         300, "the flight scene and the own cloud stack");
            Vessel vessel = FlightGlobals.ActiveVessel;

            // 0. The clock.
            foreach (ProbeWait w in ChooseClock(vessel)) yield return w;
            Check("an hour with a broken sky and the sun up", clockOk, clockOk ? null : "no hour of " + ClockHours + " has a cover " + CoverMin + " to " + CoverMax + " with the sun " + SunMinDeg + " to " + SunMaxDeg + " degrees up");
            if (!clockOk) yield break;
            Emit("T18.cover_chosen", clockCover);
            Emit("T18.sun_chosen_deg", clockSun);
            // The maps of the new hour reach the stack within a few seconds (OwnCloudPlan.MapIntervalS).
            yield return ProbeWait.Seconds(5);
            yield return ProbeWait.Until(() => module.Stack != null && module.Stack.LayerCount > 0, 120, "the own cloud stack at the chosen hour");
            var stack = (LayerStackSource)module.Stack;
            Emit("T18.stack_layers", stack.LayerCount);
            Emit("T18.rays_asked_at_start", RayCloudSeam.Asked(Time.frameCount));
            Check("the rays trace (raytracing on)", RayCloudSeam.Asked(Time.frameCount), "RayCloudSeam: no ask from the main mod's raytracing; the start must pin raytracing Full");

            // 1. Off: no pack with the switch off. The scene settles for 120 frames first (the new hour's maps, the weather, the sky's own work).
            SetSetting(AtmospheresSettings.Keys.RtOwnCloudsLayerCap, 0);
            yield return ProbeWait.Frames(120);
            Emit("T18.off_no_pack", RtOwnClouds.Pack == null && RtOwnClouds.Texture == null);

            // 2. On: the wall time to the first pack, the frames while it bakes, the bake and the upload.
            int bakesBefore = RtOwnClouds.Stats.BakesDone;
            float onAt = Time.realtimeSinceStartup;
            int onFrame = Time.frameCount;
            SetSetting(AtmospheresSettings.Keys.RtOwnClouds, 1);
            yield return ProbeWait.Until(() => RtOwnClouds.Pack != null && RtOwnClouds.Texture != null, 240, "the first ray cloud pack");
            Emit("T18.first_pack_wall_s", Time.realtimeSinceStartup - onAt);
            Emit("T18.frame_ms_bake", FrameMs(onAt, onFrame));
            RtCloudPackDriver.Statistics stats = RtOwnClouds.Stats;
            Emit("T18.bake_total_ms", stats.LastBakeMs);
            Emit("T18.bake_wall_s", stats.LastBakeWallS);
            Emit("T18.bake_tick_worst_ms", stats.WorstFirstTickMs);
            Emit("T18.bakes_done", stats.BakesDone - bakesBefore);
            Emit("T18.upload_ms", RtOwnClouds.FirstUploadMs);
            yield return ProbeWait.Until(() => Time.frameCount - RayCloudSeam.TakenFrame <= 2, 120, "the rays taking the own pack");

            // 3. The cost of the own layers in the rays: the same traced scene with the pack in the rays and with it hidden from them (the pack stays and is kept
            // current, so nothing but what the rays read differs). Five rounds; a round holds one window of each kind, the order turning each round so that a drift of
            // the scene (the sky, other work on the machine) does not fall on one kind. A window is 30 frames to settle (not measured: the first frames after a
            // switch carry its work) and 60 timed frames, and its figure is the median frame time. A kind's figure is the median of its windows' figures; the
            // cost is the median over the rounds of (taps8 - hidden) of the same round, so that a slow drift cancels in each pair. The spread is the largest
            // distance of a round's figure of a kind from that kind's median: it says whether the figure can be trusted. A VALID measurement needs: every window did what
            // its kind says (the rays took the pack, or did not); no round of a kind more than max(2 ms, 5 %) from its median; and no other process using the GPU
            // (the series is run on a quiet machine: a Unity job or a second game on the same GPU moves the windows by several ms).
            double bakeMsAtStart = RtOwnClouds.Stats.TotalBakeMs;
            int framesAtStart = Time.frameCount, uploadsAtStart = RtOwnClouds.Uploads;
            RtOwnClouds.Stats.WorstTickMs = 0.0;
            RtOwnClouds.WorstSliceMs = 0.0;
            string[] kinds = { "hidden", "taps8", "taps4", "taps16" };
            const int Rounds = 5;
            var windows = new Dictionary<string, List<double>>();
            foreach (string k in kinds) windows[k] = new List<double>();
            int invalid = 0;
            double longestFrame = 0.0;
            for (int round = 0; round < Rounds; round++)
                for (int n = 0; n < kinds.Length; n++)
                {
                    string kind = kinds[(n + round) % kinds.Length];
                    RtOwnClouds.HideFromRays = kind == "hidden";
                    if (kind == "taps4") ForceTuning("rt.rayTaps", 4);
                    else if (kind == "taps16") ForceTuning("rt.rayTaps", 16);
                    else ClearForced();
                    foreach (ProbeWait w in Window(30, 60)) yield return w;
                    windows[kind].Add(windowMs);
                    longestFrame = Math.Max(longestFrame, windowMax);
                    // A window counts only where the rays did what the kind says: hidden, nothing taken at its end; the others, the pack taken in the last frames.
                    bool taken = Time.frameCount - RayCloudSeam.TakenFrame <= 2;
                    if (taken == (kind == "hidden")) invalid++;
                }
            RtOwnClouds.HideFromRays = false;
            ClearForced();
            double hidden = Median(windows["hidden"]);
            double spread = 0.0;
            foreach (string k in kinds)
            {
                double m = Median(windows[k]);
                foreach (double v in windows[k]) spread = Math.Max(spread, Math.Abs(v - m));
            }
            var deltas8 = new List<double>();
            var deltas4 = new List<double>();
            var deltas16 = new List<double>();
            for (int r = 0; r < Rounds; r++)
            {
                deltas8.Add(windows["taps8"][r] - windows["hidden"][r]);
                deltas4.Add(windows["taps4"][r] - windows["hidden"][r]);
                deltas16.Add(windows["taps16"][r] - windows["hidden"][r]);
            }
            Emit("T18.rt_ms_off", hidden);
            Emit("T18.rt_ms_taps8", Median(windows["taps8"]));
            Emit("T18.rt_ms_taps4", Median(windows["taps4"]));
            Emit("T18.rt_ms_taps16", Median(windows["taps16"]));
            Emit("T18.rt_frame_ms_delta", Median(deltas8));
            Emit("T18.rt_ms_delta_taps4", Median(deltas4));
            Emit("T18.rt_ms_delta_taps16", Median(deltas16));
            Emit("T18.rt_ms_spread", spread);
            Emit("T18.rt_longest_frame_ms", longestFrame);
            Emit("T18.rt_windows_wrong", invalid);
            // A figure to trust: every window did what its kind says and no round of a kind lies more than 2 ms (or 5 %) from the kind's median.
            Emit("T18.rt_cost_valid", invalid == 0 && spread <= Math.Max(2.0, 0.05 * hidden));
            // What keeping the pack current cost in those frames (all windows, the bake's own stopwatch): the main-thread ms of baking per traced frame, and the
            // worst refresh tick.
            int windowFrames = Math.Max(1, Time.frameCount - framesAtStart);
            Emit("T18.refresh_bake_ms_per_frame", (RtOwnClouds.Stats.TotalBakeMs - bakeMsAtStart) / windowFrames);
            Emit("T18.refresh_tick_worst_ms", RtOwnClouds.Stats.WorstTickMs);
            Emit("T18.refresh_uploads", RtOwnClouds.Uploads - uploadsAtStart);
            // The age of the sky the pack shows: after a whole refresh cycle (a second pack standing), the largest age since the first pack stood.
            int bakesAfterFirst = RtOwnClouds.Stats.BakesDone;
            RtOwnClouds.Stats.MaxAgeS = RtOwnClouds.Stats.AgeS;
            yield return ProbeWait.Until(() => RtOwnClouds.Stats.BakesDone > bakesAfterFirst, 240, "a refreshed ray cloud pack");
            Emit("T18.pack_age_max_s", RtOwnClouds.Stats.MaxAgeS);
            Emit("T18.refresh_bake_wall_s", RtOwnClouds.Stats.LastBakeWallS);
            Emit("T18.refresh_bake_total_ms", RtOwnClouds.Stats.LastBakeMs);
            // The refresh uploads a layer slice a frame (RtOwnClouds): the worst main-thread step of them, since the windows began.
            yield return ProbeWait.Frames(10);
            Emit("T18.upload_slice_worst_ms", RtOwnClouds.WorstSliceMs);
            yield return ProbeWait.Frames(30);

            // 4. Facts.
            RtCloudPack pack = RtOwnClouds.Pack;
            Check("a pack stands", pack != null);
            if (pack == null) yield break;
            int expected = Math.Min(stack.LayerCount, RtCloudPackLayout.MaxLayers);
            Emit("T18.layers_in_pack", pack.LayerCount);
            Emit("T18.layers_in_pack_all", pack.LayerCount == expected);
            Emit("T18.pack_width", pack.Width);
            Emit("T18.pack_mb", RtCloudPackLayout.Bytes(pack.Width, pack.Height, pack.LayerCount) / (1024.0 * 1024.0));
            Texture texture = RtOwnClouds.Texture;
            Emit("T18.texture_ok", texture != null && texture.width == pack.Width && texture.height == pack.Height * pack.LayerCount + RayCloudLayout.HeaderRows);
            double covered = 0.0;
            for (int i = 0; i < pack.Width * pack.Height * RtCloudPackLayout.Channels; i += RtCloudPackLayout.Channels)
                if (LayerDensity.Shape(pack.Data[i], pack.Entries[0].Coverage, pack.Entries[0].EdgeSoftness) > 0.0) covered += 1.0;
            Emit("T18.layer0_cloud_share", covered / (pack.Width * pack.Height));

            // 5. The rays take the pack.
            yield return ProbeWait.Until(() => Time.frameCount - RayCloudSeam.TakenFrame <= 2, 120, "the rays taking the own pack");
            bool fresh = RayCloudSeam.Fresh(Time.frameCount, vessel.mainBody.flightGlobalsIndex);
            bool took = Time.frameCount - RayCloudSeam.TakenFrame <= 2;
            string status = RayCloudSeam.HostStatus() ?? "";
            bool problem = status.IndexOf("cannot be shared", StringComparison.Ordinal) >= 0 || status.IndexOf("input 7", StringComparison.Ordinal) >= 0;
            // The proxy reports the cloud block's mode of its latest traced frame: a dxgi.dll older than this build has no such field (or reads the block as
            // EVE's layer, mode 1), and every check above would pass for it, since "took" is the managed side's own flag.
            bool proxyOwn = status.IndexOf("cloudsMode=2", StringComparison.Ordinal) >= 0;
            Emit("T18.view_fresh", fresh);
            Emit("T18.rays_took_pack", took);
            Emit("T18.rays_problem", problem);
            Emit("T18.proxy_reads_own_mode", proxyOwn);
            EmitText("T18.rays_status", status.Length > 200 ? status.Substring(status.Length - 200) : status);
            Check("the rays take the own pack and the proxy shares it", fresh && took && !problem, "fresh " + fresh + ", taken " + took + ", status: " + status);
            Check("the proxy traced its latest frame with the own cloud block (cloudsMode=2 in its status; a dxgi.dll from before this build lacks it)", proxyOwn, "status: " + status);

            // 5b. What the shaders read, as the game gives it: the view block's frame and shell against the body and the pack, and the header row's numbers.
            // The vessel's place in the map frame must come out at its latitude and longitude (a wrong frame, a mirrored axis or a rotation of the body that is
            // not in the matrix moves it by degrees), and the shell and the texture's size must be the pack's.
            RayCloudView view = RayCloudSeam.View;
            CelestialBody viewBody = vessel.mainBody;
            Vector3 centre = (Vector3)viewBody.position;
            Vector3 inMap = view.WorldToMap.MultiplyVector((Vector3)vessel.GetWorldPos3D() - centre);
            double mapLat = Math.Asin(Math.Max(-1.0, Math.Min(1.0, inMap.z / inMap.magnitude))) * 180.0 / Math.PI, mapLon = Math.Atan2(inMap.y, inMap.x) * 180.0 / Math.PI;
            double lonErr = Math.Abs(mapLon - vessel.longitude);
            lonErr = Math.Min(lonErr, 360.0 - lonErr);
            Emit("T18.view_frame_err_deg", Math.Max(Math.Abs(mapLat - vessel.latitude), lonErr));
            double minInner = double.MaxValue, maxOuter = 0.0;
            for (int i = 0; i < pack.LayerCount; i++)
            {
                minInner = Math.Min(minInner, pack.Entries[i].InnerRadiusM);
                maxOuter = Math.Max(maxOuter, pack.Entries[i].OuterRadiusM);
            }
            Emit("T18.view_shell_ok", Math.Abs(view.InnerRadiusM - minInner) < 1.0 && Math.Abs(view.OuterRadiusM - maxOuter) < 1.0);
            Emit("T18.view_size_ok", view.Width == pack.Width && view.SliceHeight == pack.Height && view.LayerCount == pack.LayerCount && view.BodyIndex == pack.BodyIndex);
            Emit("T18.view_body_radius_err_m", Math.Abs(pack.BodyRadiusM - viewBody.Radius));
            for (int i = 0; i < pack.LayerCount; i++)
            {
                RtCloudLayerEntry e = pack.Entries[i];
                EmitText("T18.header_layer" + i, e.Name + " shell " + (e.InnerRadiusM - pack.BodyRadiusM).ToString("0") + " to " + (e.OuterRadiusM - pack.BodyRadiusM).ToString("0")
                                                 + " m, strength " + e.ShadowStrength.ToString("0.00") + ", coverage " + e.Coverage.ToString("0.000") + ", softness " + e.EdgeSoftness.ToString("0.000")
                                                 + ", clearK " + e.ClearSkyK.ToString("0.000") + ", power " + e.WaterPower.ToString("0.00") + ", shift " + e.MapShiftDeg.ToString("0.00"));
            }

            // 6. The five places and the scan, against the stack as it stands now, for the pack that stands (its age is in it) and for a pack baked in this very
            // call (the stack and the pack are then the same instant, so what is left is the model: if the two differ, the standing pack is stale; if both are far
            // from the field, the ray side's reading of the field is wrong). The figures are |T_ray - T_reference| (RtAgreementScan).
            LayerTypeTable[] tables = stack.Tables();
            long bakeStart = System.Diagnostics.Stopwatch.GetTimestamp();
            RtCloudPack freshPack = RtCloudPackDriver.BakeNow(stack, tables, RtOwnClouds.LiveSettings());
            Emit("T18.fresh_bake_ms", (System.Diagnostics.Stopwatch.GetTimestamp() - bakeStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            Emit("T18.pack_age_at_scan_s", RtOwnClouds.Stats.AgeS);
            string missing;
            List<RtAgreementPlace> places = RtPackAgreement.Pick(stack, tables, pack, vessel.latitude, vessel.longitude, 4.0, 0.2, 45.0, out missing);
            foreach (RtAgreementPlace p in places)
            {
                string key = p.Name.Substring(0, 2);
                Emit("T18.shadow_ref_" + key, p.Reference);
                Emit("T18.shadow_ray_" + key, p.Ray);
                Emit("T18.shadow_diff_" + key, p.Diff);
                Emit("T18.shadow_quick_diff_" + key, p.QuickDiff);
            }
            Emit("T18.places_missing", missing.Length == 0 ? 0 : missing.Split(',').Length);
            if (missing.Length > 0) EmitText("T18.places_missing_kinds", missing);
            string missingFresh;
            foreach (RtAgreementPlace p in RtPackAgreement.Pick(stack, tables, freshPack, vessel.latitude, vessel.longitude, 4.0, 0.2, 45.0, out missingFresh))
                Emit("T18.shadow_fresh_diff_" + p.Name.Substring(0, 2), p.Diff);
            foreach (double elevation in new[] { 45.0, 6.0 })
            {
                EmitScan("T18.scan" + (int)elevation, RtPackAgreement.Scan(stack, tables, pack, vessel.latitude, vessel.longitude, 4.0, 0.2, elevation));
                EmitScan("T18.scan" + (int)elevation + "_fresh", RtPackAgreement.Scan(stack, tables, freshPack, vessel.latitude, vessel.longitude, 4.0, 0.2, elevation));
            }
            // What both sides read at the three places of the 45 degree lattice where they differ most (RtPackAgreement.WorstPlaces), one line per place and layer.
            int worstLine = 0;
            foreach (string line in RtPackAgreement.WorstPlaces(stack, tables, pack, vessel.latitude, vessel.longitude, 4.0, 0.2, 45.0, 3))
                EmitText("T18.scan45_worst_place_" + worstLine++, line);

            // 7. Pictures with the layers in the rays; the layer cap; off and the same pictures without.
            FlightCamera camera = FlightCamera.fetch;
            float hdg = camera != null ? camera.camHdg : 0f, pitch = camera != null ? camera.camPitch : 0f;
            foreach (ProbeWait w in Pictures("on", hdg, pitch)) yield return w;
            SetSetting(AtmospheresSettings.Keys.RtOwnCloudsLayerCap, 2);
            int capExpected = Math.Min(2, stack.LayerCount);
            yield return ProbeWait.Until(() => RtOwnClouds.Pack != null && RtOwnClouds.Pack.LayerCount == capExpected && !RtOwnClouds.Baking, 240, "the pack with the layer cap");
            Emit("T18.cap2_layers", RtOwnClouds.Pack != null ? RtOwnClouds.Pack.LayerCount : -1);
            SetSetting(AtmospheresSettings.Keys.RtOwnCloudsLayerCap, 0);
            SetSetting(AtmospheresSettings.Keys.RtOwnClouds, 0);
            yield return ProbeWait.Frames(10);
            bool released = RtOwnClouds.Pack == null && RtOwnClouds.Texture == null && !RayCloudSeam.Fresh(Time.frameCount, vessel.mainBody.flightGlobalsIndex);
            Emit("T18.off_releases_pack", released);
            Check("the switch off releases the pack, the texture and the rays' view", released);
            foreach (ProbeWait w in Pictures("off", hdg, pitch)) yield return w;
            SetSetting(AtmospheresSettings.Keys.RtOwnClouds, 1);
            // The pairs against each other: a view whose pair does not differ shows no own cloud in the rays (or none in view).
            double best = -1.0;
            foreach (string pictureView in Views)
            {
                double diff = PictureDifference(pictureView);
                Emit("T18.picture_diff_" + pictureView.Replace('-', '_'), diff);
                if (diff > best) best = diff;
            }
            Emit("T18.picture_diff_max", best);
        }
    }
}
