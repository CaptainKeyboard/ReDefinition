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
    //   1. the frame time with rtOwnClouds off (300 frames, the rays tracing without the own layers);
    //   2. the switch on: the wall time and the frame time until the first pack stands, the bake's main-thread time and its worst tick, the upload;
    //   3. the frame time with the pack standing and in the rays (300 frames): the cost of the own layers in the rays is 3 against 1;
    //   4. the pack's facts: the layers in it against the stack's, the texture;
    //   5. the rays: they ask, take the pack into their frames, and the proxy reports no problem with it (a texture it cannot share drops every frame);
    //   6. the five places round the vessel (RtPackAgreement.Pick, sun 45 degrees, 6 degrees at the terminator): the shadow factor by the pack's law
    //      against the cloud shadow field at 64 steps a span, and against the field at the shadow pass's 8 steps; and the scan of the whole lattice;
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
        private static readonly string[] Views = { "pad", "horizon-h0", "horizon-h1", "horizon-h2", "horizon-h3" };

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

            // 1. Off.
            SetSetting(AtmospheresSettings.Keys.RtOwnCloudsLayerCap, 0);
            yield return ProbeWait.Frames(30);
            Emit("T18.off_no_pack", RtOwnClouds.Pack == null && RtOwnClouds.Texture == null);
            float t0 = Time.realtimeSinceStartup;
            int f0 = Time.frameCount;
            yield return ProbeWait.Frames(300);
            double offMs = FrameMs(t0, f0);
            Emit("T18.frame_ms_off", offMs);

            // 2. On.
            int bakesBefore = RtOwnClouds.Stats.BakesDone;
            float onAt = Time.realtimeSinceStartup;
            int onFrame = Time.frameCount;
            SetSetting(AtmospheresSettings.Keys.RtOwnClouds, 1);
            yield return ProbeWait.Until(() => RtOwnClouds.Pack != null && RtOwnClouds.Texture != null, 240, "the first ray cloud pack");
            double bakeMs = FrameMs(onAt, onFrame);
            Emit("T18.first_pack_wall_s", Time.realtimeSinceStartup - onAt);
            Emit("T18.frame_ms_bake", bakeMs);
            Emit("T18.frame_ms_delta_bake", bakeMs - offMs);
            RtCloudPackDriver.Statistics stats = RtOwnClouds.Stats;
            Emit("T18.bake_total_ms", stats.LastBakeMs);
            Emit("T18.bake_tick_worst_ms", stats.WorstTickMs);
            Emit("T18.bakes_done", stats.BakesDone - bakesBefore);
            Emit("T18.upload_ms", RtOwnClouds.LastUploadMs);
            Emit("T18.upload_worst_ms", RtOwnClouds.WorstUploadMs);

            // 3. Steady, the layers in the rays: only once the rays take the pack, or the window would time the rays without it.
            yield return ProbeWait.Until(() => Time.frameCount - RayCloudSeam.TakenFrame <= 2, 120, "the rays taking the own pack before the steady window");
            yield return ProbeWait.Frames(30);
            t0 = Time.realtimeSinceStartup;
            f0 = Time.frameCount;
            yield return ProbeWait.Frames(300);
            double steadyMs = FrameMs(t0, f0);
            Emit("T18.frame_ms_steady", steadyMs);
            Emit("T18.frame_ms_delta_steady", steadyMs - offMs);
            Emit("T18.rt_frame_ms_delta", steadyMs - offMs);
            Emit("T18.rt_ms_off", offMs);
            Emit("T18.rt_ms_taps8", steadyMs);

            // 3b. The cost of the lookups a ray takes: the most taps a span takes (rt.rayTaps), the same view, the pack standing.
            foreach (int taps in new[] { 4, 16 })
            {
                ForceTuning("rt.rayTaps", taps);
                yield return ProbeWait.Frames(30);
                t0 = Time.realtimeSinceStartup;
                f0 = Time.frameCount;
                yield return ProbeWait.Frames(300);
                double ms = FrameMs(t0, f0);
                Emit("T18.rt_ms_taps" + taps, ms);
                Emit("T18.rt_ms_delta_taps" + taps, ms - offMs);
            }
            ClearForced();
            yield return ProbeWait.Frames(30);

            // 4. Facts.
            RtCloudPack pack = RtOwnClouds.Pack;
            Check("a pack stands", pack != null);
            if (pack == null) yield break;
            int expected = Math.Min(stack.LayerCount, RtCloudPackLayout.MaxLayers);
            Emit("T18.layers_in_pack", pack.LayerCount);
            Emit("T18.layers_in_pack_all", pack.LayerCount == expected);
            Emit("T18.pack_width", pack.Width);
            Emit("T18.pack_mb", RtCloudPackLayout.Bytes(pack.Width, pack.Height, pack.LayerCount) * 2 / (1024.0 * 1024.0));
            Texture texture = RtOwnClouds.Texture;
            Emit("T18.texture_ok", texture != null && texture.width == pack.Width && texture.height == pack.Height * pack.LayerCount + RayCloudLayout.HeaderRows);
            double covered = 0.0;
            for (int i = 0; i < pack.Width * pack.Height * 2; i += 2)
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

            // 6. The five places and the scan, against the stack as it stands now.
            LayerTypeTable[] tables = stack.Tables();
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
            foreach (double elevation in new[] { 45.0, 6.0 })
            {
                RtAgreementScan scan = RtPackAgreement.Scan(stack, tables, pack, vessel.latitude, vessel.longitude, 4.0, 0.2, elevation);
                string key = "T18.scan" + (int)elevation;
                Emit(key + "_places", scan.Places);
                Emit(key + "_median", scan.Median);
                Emit(key + "_p90", scan.P90);
                Emit(key + "_worst", scan.Worst);
            }

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
            foreach (string view in Views)
            {
                double diff = PictureDifference(view);
                Emit("T18.picture_diff_" + view.Replace('-', '_'), diff);
                if (diff > best) best = diff;
            }
            Emit("T18.picture_diff_max", best);
        }
    }
}
