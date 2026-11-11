using System;
using System.Collections;
using System.Collections.Generic;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // Checks the aurora shell's own draw (AuroraPass and its shader, the classes the mod uses) against the artefacts of the earlier EVE
    // layer (package D38). Each pose renders the world with and without the shell (identical camera, state and clock); the contribution
    // mask is the positive part of the difference. From the mask:
    //   lid    the largest step between adjacent row means of the mask, over the peak row mean (a hard lid is one big step);
    //   drip   the share of columns whose lowest lit row hangs more than 6 % of the picture below the median of its neighbours
    //          (vertical drips below the base line);
    //   band   the mean mask in the rows below 3 degrees of elevation over the peak row mean (the horizon band).
    // The grid: sun -6, -12, -25 x activity 0.2, 0.6, 1.0 x latitude 60, 70, 78 at 25 degrees of view elevation, and the latitudes at
    // 10 and 40 degrees at sun -12, activity 0.6. The camera looks at the oval (north when the oval is poleward of the camera, south
    // when it is equatorward). Also: the terrain occludes the shell (a cube in front of the centre), the repeat of a state is
    // bit-identical (no jitter), and the shell is absent at sun +10.
    //
    //   -mode scene -scene aurora-check
    [WeatherScene("aurora-check")]
    public sealed class AuroraCheckScene : AuroraSceneBase
    {
        private const float LidLimit = 0.12f, DripLimit = 0.03f, BandLimit = 0.02f, PeakMin = AuroraMetrics.PeakMin;

        public override IEnumerator Run()
        {
            AuroraLook look = AuroraLook.Defaults();
            float worstLid = 0f, worstDrip = 0f, worstBand = 0f;
            int measured = 0, dim = 0;
            var visibleAt = new Dictionary<int, bool>();
            var poses = new List<float[]>();
            foreach (float sun in new[] { -6f, -12f, -25f })
                foreach (float activity in new[] { 0.2f, 0.6f, 1.0f })
                    foreach (float lat in new[] { 60f, 70f, 78f })
                        poses.Add(new[] { sun, activity, lat, 25f, 0f });
            foreach (float lat in new[] { 60f, 70f, 78f })
                foreach (float elevation in new[] { 10f, 40f })
                    poses.Add(new[] { -12f, 0.6f, lat, elevation, 0f });
            // The dearest poses: the strong event pushes the oval to latitude 60, over a camera at 60, with the longest paths at 10 degrees.
            poses.Add(new[] { -12f, 1.0f, 60f, 10f, 1f });
            poses.Add(new[] { -12f, 1.0f, 60f, 25f, 1f });
            Sink.Info("poses.total", poses.Count);

            foreach (float[] pose in poses)
            {
                float sun = pose[0], activity = pose[1], lat = pose[2], elevation = pose[3], evt = pose[4];
                AuroraFrame frame = Forced(activity, sun, evt);
                float yaw = YawFor(frame, lat);
                SetSun(sun);
                Look(yaw, elevation);
                Shell(false, frame, lat, true, look);
                yield return Frames(2);
                LumaFrame off = null, on = null;
                yield return Grab(r => off = r);
                Shell(true, frame, lat, true, look);
                yield return Frames(2);
                yield return Grab(r => on = r);
                float[] mask = Mask(on, off);
                if (sun == -25f && activity >= 1f && elevation == 25f || (sun == -12f && activity == 0.6f && lat == 60f))
                    SavePicture("pose-s" + (int)(-sun) + "-a" + (int)(activity * 10f + 0.5f) + "-l" + (int)lat + "-e" + (int)elevation, 6f);
                string key = "s" + (int)(-sun) + "_a" + (int)(activity * 10f + 0.5f) + "_l" + (int)lat + "_e" + (int)elevation + (evt > 0f ? "_ev" : "");
                AuroraMetrics.Result result = AuroraMetrics.MeasureMask(mask, on.Width, on.Height, elevation, Cam.fieldOfView);
                double peak = result.Peak;
                Sink.Info("peak." + key, peak);
                int latKey = (int)lat;
                if (!result.Judged)
                {
                    dim++;
                    Sink.Info("dim." + key, 1);
                    continue;
                }
                visibleAt[latKey] = true;
                measured++;
                float lid = result.Lid;
                float drip = result.Drip;
                float band = result.Band;
                worstLid = Mathf.Max(worstLid, lid);
                worstDrip = Mathf.Max(worstDrip, drip);
                worstBand = Mathf.Max(worstBand, band);
                Sink.Info("lid." + key, lid);
                Sink.Info("drip." + key, drip);
                Sink.Info("band." + key, band);
            }
            Sink.AtMost("lid.worst", worstLid, LidLimit);
            Sink.AtMost("drip.worst", worstDrip, DripLimit);
            Sink.AtMost("band.worst", worstBand, BandLimit);
            Sink.AtLeast("poses.measured", measured, 18);
            Sink.Info("poses.dim", dim);
            foreach (int latKey in new[] { 60, 70, 78 })
            {
                bool seen;
                visibleAt.TryGetValue(latKey, out seen);
                Sink.Flag("visible.lat" + latKey, seen);
            }

            // --- The terrain occludes: a cube in front of the centre of the view.
            {
                float sun = -25f, activity = 1.0f, lat = 70f, elevation = 40f;
                AuroraFrame frame = Forced(activity, sun);
                SetSun(sun);
                Look(YawFor(frame, lat), elevation);
                // A dark sphere of 130 m radius, 400 m ahead and off the centre (left and up, so that a flipped depth copy would put it
                // elsewhere and the shell would show inside): its screen rectangle is a square, the circle inside it hides the shell.
                var cube = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                UnityEngine.Object.Destroy(cube.GetComponent<Collider>());
                cube.GetComponent<Renderer>().sharedMaterial = new Material(Resources.Load<Material>("WeatherStandard")) { color = new Color(0.05f, 0.05f, 0.05f) };
                cube.transform.position = Cam.transform.position + Cam.transform.forward * 400f - Cam.transform.right * 90f + Cam.transform.up * 70f;
                cube.transform.localScale = new Vector3(260f, 260f, 260f);
                Shell(false, frame, lat, true, look);
                yield return Frames(2);
                LumaFrame off = null, on = null;
                yield return Grab(r => off = r);
                Shell(true, frame, lat, true, look);
                yield return Frames(2);
                yield return Grab(r => on = r);
                float[] mask = Mask(on, off);
                ImageStats.Rect2 rect = ImageStats.ScreenRect(Cam, cube.transform.position, 130f, on.Width, on.Height);
                var inner = new ImageStats.Rect2(rect.X0 + (rect.X1 - rect.X0) / 4, rect.Y0 + (rect.Y1 - rect.Y0) / 4,
                                                 rect.X1 - (rect.X1 - rect.X0) / 4, rect.Y1 - (rect.Y1 - rect.Y0) / 4);
                double inside = MeanMask(mask, on.Width, on.Height, inner, true);
                double outside = MeanMask(mask, on.Width, on.Height, rect, false);
                Sink.Info("occluder.inside_mean", inside);
                Sink.Info("occluder.outside_mean", outside);
                Sink.Flag("occluder.hides_shell", inside <= 1e-4 && outside >= 1e-3);

                // The control: the same pose without the sphere is lit inside the same rectangle (so the zero above is the occlusion).
                cube.SetActive(false);
                Shell(false, frame, lat, true, look);
                yield return Frames(2);
                LumaFrame clearOff = null, clearOn = null;
                yield return Grab(r => clearOff = r);
                Shell(true, frame, lat, true, look);
                yield return Frames(2);
                yield return Grab(r => clearOn = r);
                double control = MeanMask(Mask(clearOn, clearOff), on.Width, on.Height, inner, true);
                Sink.Info("occluder.control_inside_mean", control);
                Sink.AtLeast("occluder.control_is_lit", control, 1e-3);
                UnityEngine.Object.Destroy(cube);
            }

            // --- The same state twice gives the same picture (nothing jitters), and the shell is not drawn by day.
            {
                float sun = -25f, activity = 1.0f, lat = 60f;
                AuroraFrame frame = Forced(activity, sun);
                SetSun(sun);
                Look(YawFor(frame, lat), 25f);
                Shell(false, frame, lat, true, look);
                yield return Frames(2);
                LumaFrame none = null, a = null, b = null;
                yield return Grab(r => none = r);
                Shell(true, frame, lat, true, look);
                yield return Frames(2);
                yield return Grab(r => a = r);
                yield return Frames(3);
                yield return Grab(r => b = r);
                Sink.Flag("repeat.pose_is_lit", PeakRowMean(Mask(a, none), a.Width, a.Height) >= PeakMin);
                Sink.AtMost("repeat.max_diff", MaxDiff(a, b), 1e-5);

                // By day the model draws nothing; and the shell recorded with the day's intensity (zero) changes no pixel.
                AuroraFrame day = Forced(activity, 10f);
                Sink.Flag("day.no_draw", !day.Draw);
                SetSun(10f);
                Shell(false, day, lat, true, look);
                yield return Frames(2);
                LumaFrame off1 = null, off2 = null;
                yield return Grab(r => off1 = r);
                day.Draw = true;
                Shell(true, day, lat, true, look);
                yield return Frames(2);
                yield return Grab(r => off2 = r);
                Sink.AtMost("day.shell_diff", MaxDiff(off1, off2), 1e-6);
            }
        }

        // The positive part of the difference, row 0 at the bottom.
        private static float[] Mask(LumaFrame on, LumaFrame off)
        {
            var m = new float[on.Y.Length];
            for (int i = 0; i < m.Length; i++) m[i] = Mathf.Max(0f, on.Y[i] - off.Y[i]);
            return m;
        }

        private static double MaxDiff(LumaFrame a, LumaFrame b)
        {
            double worst = 0;
            for (int i = 0; i < a.Y.Length; i++) worst = Math.Max(worst, Math.Abs(a.Y[i] - b.Y[i]));
            return worst;
        }

        // The metrics are the mod's own (AuroraMetrics, linked), the ones the series probe applies to the game's frames.
        private static double PeakRowMean(float[] mask, int width, int height)
        {
            return AuroraMetrics.PeakRowMean(mask, width, height);
        }

        // The mean mask inside a rectangle, or outside it within the rows the rectangle spans.
        private static double MeanMask(float[] mask, int width, int height, ImageStats.Rect2 r, bool inside)
        {
            return AuroraMetrics.MeanMask(mask, width, height, r.X0, r.Y0, r.X1, r.Y1, inside);
        }
    }
}
