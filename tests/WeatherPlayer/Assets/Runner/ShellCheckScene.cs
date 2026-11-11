using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // The L2 spike of the vehicle cover (package D34, brief item 4 and the player acceptance line), with the mod's own classes: a dark box
    // part (8 m x 1 m x 4 m) with a cover shell (ShellMesh, the shell shader, an amount of 1), a tilted wing and a vertical tank, floating
    // in front of the terrain and the sky. Reference expectation, written before the first run:
    //   * the shell shows: at 10, 30 and 100 m at least 90 % of the roof's pixels are cover (luma rose by more than 0.25 over the bare
    //     part), and the vertical tank carries none (at most 3 % of its pixels);
    //   * depth z-fight: of the roof's pixels, those that show the dark part through the cover are at most 1 % at 10, 30 and 100 m (the
    //     shell sits 2 cm over the part with the depth offset -1; the count at 300 m is reported, the mod draws shells near cameras only);
    //   * motion vectors: the part and its shell move together (15 m/s along x, both with motionVectorGenerationMode Object); over the
    //     pixels the shell covers, at least 98 % of the motion vectors with the shell equal the part's own without it (within 2 % or a
    //     quarter of a pixel), at 30 and 100 m; the vectors in that region are not zero (the control: at least a pixel of motion);
    //   * Deferred on and off: with Unity's deferred path the cover's luma over the roof is within 0.8 .. 1.25 of the forward path's and
    //     the cover still shows (the installed Deferred mod's own lighting shaders are not in the player [VERIFY in the game]);
    //   * variant (f), the screen-space pass: at 30 m it covers at least 80 % of the roof and at most 3 % of the vertical tank, its roof
    //     share is within 15 points of the shell's, and it changes no motion vector (identical to the frame without it);
    //   * the contribution of nothing else: with the shells off no pixel differs from the bare frame by more than 0.001 (the shell
    //     renderers are the only change).
    // What the motion-vector row cannot see: a shell that wrote NO vector would let the part's own show through underneath and match;
    // it catches a shell that writes different vectors (the pixels where the cover is visible are those of the shell pass). The player
    // draws no vessel motion-vector rig: the game's rig draws every opaque mesh renderer of a part, the shell included
    // (VesselMotionVectors) [VERIFY in the game].
    //
    //   -mode scene -scene shell-check
    [WeatherScene("shell-check")]
    internal sealed class ShellCheckScene : ShellSceneBase
    {
        private static readonly Vector3 RoofSize = new Vector3(8f, 1f, 4f);
        private static readonly Vector3 TankSize = new Vector3(1.2f, 3f, 1.2f);
        private static readonly Vector3 TankAt = Stand + new Vector3(0f, -2.4f, 0f);
        private GameObject root;

        protected override void BuildContent()
        {
            MakeMaterials();
            // The root sits at the origin; the parts are placed in world space and the root is moved to move the whole stand-in.
            root = new GameObject("Stand-in");
            Mesh roof = BoxMesh(RoofSize);
            AddPart(roof, Stand, Quaternion.identity, root.transform, true);
            // The vertical tank hangs under the roof; only its sides face the camera (no up-facing area but its cap, hidden under the roof).
            Mesh tank = BoxMesh(TankSize);
            AddPart(tank, TankAt, Quaternion.identity, root.transform, true);
            // A wing: the roof's shape tilted by 25 degrees about z, to the side.
            AddPart(BoxMesh(new Vector3(6f, 0.3f, 2f)), Stand + new Vector3(0f, 0.6f, 4.5f), Quaternion.Euler(0f, 0f, 25f), root.transform, true);
            ShellsOn(false);
        }

        private void PlaceCamera(float distance)
        {
            Cam.transform.position = Stand + new Vector3(0f, distance * 0.4f, -distance);
            Cam.transform.rotation = Quaternion.LookRotation(Stand - Cam.transform.position, Vector3.up);
        }

        // The pixels of the top face of the roof, shrunk a little, as a rectangle.
        private ImageStats.Rect2 RoofRect(float shrink)
        {
            return TopRect(Stand + new Vector3(0f, RoofSize.y * 0.5f, 0f), new Vector3(RoofSize.x * 0.5f, 0f, RoofSize.z * 0.5f), shrink);
        }

        private ImageStats.Rect2 TopRect(Vector3 centre, Vector3 halfSize, float shrink)
        {
            return ScreenRect(centre, halfSize, shrink);
        }

        // The tank's side as seen from the camera: the vertical rectangle of its front face.
        private ImageStats.Rect2 TankRect(float shrink)
        {
            return ScreenRect(TankAt + new Vector3(0f, 0f, -TankSize.z * 0.5f), new Vector3(TankSize.x * 0.5f, TankSize.y * 0.5f, 0f), shrink);
        }

        private IEnumerator Still(bool shells, Action<RgbFrame> done)
        {
            ShellsOn(shells);
            yield return Frames(3);
            yield return GrabRgb(done);
        }

        // The share of the pixels of a rectangle that are cover in frame `on` against `off` (luma rose by more than the threshold).
        private double CoverShare(RgbFrame on, RgbFrame off, ImageStats.Rect2 r)
        {
            int n = 0, cover = 0;
            for (int y = r.Y0; y < r.Y1; y++)
                for (int x = r.X0; x < r.X1; x++)
                {
                    int i = y * on.Width + x;
                    n++;
                    if (Luma(on, i) - Luma(off, i) > CoverThreshold) cover++;
                }
            return n == 0 ? double.NaN : cover / (double)n;
        }

        // The pixels of a rectangle of the frame with the shell on that show the dark part where the shell should cover: those under the
        // middle of the part's own luma (frame `off`, the rectangle's mean) and the cover's mean luma.
        private int DarkPixels(RgbFrame on, RgbFrame off, ImageStats.Rect2 r)
        {
            double limit = 0.5 * (off.Mean(r, 3) + CoverLuma(on, off, r));
            int dark = 0;
            for (int y = r.Y0; y < r.Y1; y++)
                for (int x = r.X0; x < r.X1; x++) if (Luma(on, y * on.Width + x) < limit) dark++;
            return dark;
        }

        // The vessel moves along x at 0.25 m a frame for `frames` frames, the frame and the vectors of the last one are returned. The
        // pose is a function of the frame, so two runs (shells on and off) have the same history.
        private IEnumerator Motion(bool shells, int frames, Action<RgbFrame, float[], float[]> done)
        {
            ShellsOn(shells);
            for (int i = 0; i < frames; i++)
            {
                root.transform.position = new Vector3(0.25f * (i - frames), 0f, 0f);
                yield return Frames(1);
            }
            // The last pose is set in this frame's Update phase and the tap reads the frame that renders it, at its end: the vectors are
            // the motion from the previous pose to this one.
            root.transform.position = Vector3.zero;
            RgbFrame frame = null;
            float[] mx = null, my = null;
            yield return GrabFrameAndMotion((f, x, y) => { frame = f; mx = x; my = y; });
            done(frame, mx, my);
        }

        public override IEnumerator Run()
        {
            yield return Frames(6);
            Sink.Info("scene.reversed_z", SystemInfo.usesReversedZBuffer ? 1 : 0);
            // The luma thresholds are those of KSP's colour space (gamma): in linear the cover's coded luma would be far lower.
            Sink.Flag("scene.gamma_colour_space", QualitySettings.activeColorSpace == ColorSpace.Gamma);

            // --- Cover on the roof and none on the tank; z-fight; the shells off equal the bare frame.
            foreach (float distance in new[] { 10f, 30f, 100f, 300f })
            {
                PlaceCamera(distance);
                RgbFrame off = null, on = null, offAgain = null;
                yield return Still(false, f => off = f);
                yield return Still(true, f => on = f);
                yield return Still(false, f => offAgain = f);
                string d = distance.ToString("0", CultureInfo.InvariantCulture);
                ImageStats.Rect2 roof = RoofRect(distance >= 300f ? 0.1f : 0.2f);
                double share = CoverShare(on, off, roof);
                int dark = DarkPixels(on, off, roof);
                Sink.Info("roof.pixels_d" + d, roof.Area);
                Sink.Info("roof.cover_share_d" + d, share);
                Sink.Info("roof.zfight_pixels_d" + d, dark);
                if (distance <= 100f)
                {
                    Sink.AtLeast("roof.cover_share_d" + d + "_min", share, 0.90);
                    Sink.AtMost("roof.zfight_share_d" + d, roof.Area == 0 ? 1.0 : dark / (double)roof.Area, 0.01);
                    Sink.AtMost("tank.cover_share_d" + d, CoverShare(on, off, TankRect(0.25f)), 0.03);
                    // The largest difference of any pixel between the frame before the shells and the frame after them: nothing is left behind.
                    Sink.AtMost("shells_off.max_pixel_difference_d" + d, MaxAbsDifference(off, offAgain), 1e-3);
                }
            }

            // --- Motion vectors: the shell moves with the part.
            foreach (float distance in new[] { 30f, 100f })
            {
                PlaceCamera(distance);
                RgbFrame offFrame = null, onFrame = null;
                float[] offX = null, offY = null, onX = null, onY = null;
                yield return Motion(false, 8, (f, x, y) => { offFrame = f; offX = x; offY = y; });
                yield return Motion(true, 8, (f, x, y) => { onFrame = f; onX = x; onY = y; });
                string d = distance.ToString("0", CultureInfo.InvariantCulture);
                if (offX == null || onX == null)
                {
                    Sink.Flag("mv.vectors_present_d" + d, false);
                    continue;
                }
                int total = 0, match = 0;
                double moving = 0;
                ImageStats.Rect2 whole = new ImageStats.Rect2(0, 0, Width, Height);
                for (int y = whole.Y0; y < whole.Y1; y++)
                    for (int x = whole.X0; x < whole.X1; x++)
                    {
                        int i = y * Width + x;
                        if (Luma(onFrame, i) - Luma(offFrame, i) <= CoverThreshold) continue;
                        total++;
                        float ex = offX[i], ey = offY[i], dx = onX[i] - ex, dy = onY[i] - ey;
                        float magnitude = Mathf.Sqrt(ex * ex + ey * ey);
                        float tolerance = Mathf.Max(0.25f / Width, 0.02f * magnitude);
                        moving += magnitude;
                        if (Mathf.Sqrt(dx * dx + dy * dy) <= tolerance) match++;
                    }
                Sink.Info("mv.shell_pixels_d" + d, total);
                Sink.AtLeast("mv.shell_pixels_present_d" + d, total, 50);
                Sink.AtLeast("mv.moving_pixels_of_motion_d" + d, total == 0 ? 0 : moving / total * Width, 1.0);
                Sink.AtLeast("mv.match_d" + d, total == 0 ? 0 : match / (double)total, 0.98);
            }

            // --- Deferred on and off.
            PlaceCamera(30f);
            root.transform.position = Vector3.zero;
            RgbFrame fOff = null, fOn = null, dOff = null, dOn = null;
            yield return Still(false, f => fOff = f);
            yield return Still(true, f => fOn = f);
            Cam.renderingPath = RenderingPath.DeferredShading;
            yield return Frames(4);
            // A player that falls back to forward would make the ratio 1 by itself: the path really in use is checked.
            Sink.Flag("deferred.active", Cam.actualRenderingPath == RenderingPath.DeferredShading);
            yield return Still(false, f => dOff = f);
            yield return Still(true, f => dOn = f);
            Cam.renderingPath = RenderingPath.Forward;
            ImageStats.Rect2 r30 = RoofRect(0.2f);
            double forwardLuma = CoverLuma(fOn, fOff, r30), deferredLuma = CoverLuma(dOn, dOff, r30);
            double deferredShare = CoverShare(dOn, dOff, r30);
            Sink.Info("deferred.cover_luma_forward", forwardLuma);
            Sink.Info("deferred.cover_luma_deferred", deferredLuma);
            Sink.AtLeast("deferred.cover_share", deferredShare, 0.90);
            Sink.AtLeast("deferred.luma_ratio_low", deferredLuma / Math.Max(forwardLuma, 1e-6), 0.8);
            Sink.AtMost("deferred.luma_ratio_high", deferredLuma / Math.Max(forwardLuma, 1e-6), 1.25);
            yield return Frames(4);

            // --- Variant (f): the screen-space pass against the shell.
            ShellsOn(false);
            ScreenOn(false, Stand, RoofSize);
            RgbFrame bare = null, screen = null, shell = null;
            float[] bareX = null, bareY = null, screenX = null, screenY = null;
            yield return Motion(false, 8, (f, x, y) => { bare = f; bareX = x; bareY = y; });
            ScreenOn(true, Stand + new Vector3(0f, -1.2f, 0f), new Vector3(RoofSize.x * 0.5f + 1f, 3.2f, RoofSize.z * 0.5f + 1f));
            yield return Motion(false, 8, (f, x, y) => { screen = f; screenX = x; screenY = y; });
            ScreenOn(false, Stand, RoofSize);
            root.transform.position = Vector3.zero;
            yield return Still(true, f => shell = f);
            RgbFrame bareStill = null;
            yield return Still(false, f => bareStill = f);
            // The motion run ends with the stand-in at offset 0, its still position: the frames are compared with the stills directly.
            ImageStats.Rect2 roof30 = RoofRect(0.2f);
            double screenRoof = CoverShare(screen, bare, roof30), shellRoof = CoverShare(shell, bareStill, roof30);
            Sink.Info("screen.roof_cover_share", screenRoof);
            Sink.Info("shell.roof_cover_share", shellRoof);
            Sink.AtLeast("screen.roof_cover_share_min", screenRoof, 0.80);
            Sink.AtMost("screen.tank_cover_share", CoverShare(screen, bare, TankRect(0.25f)), 0.03);
            Sink.AtMost("screen.roof_share_difference_to_shell", Math.Abs(screenRoof - shellRoof), 0.15);
            if (bareX == null || screenX == null)
            {
                Sink.Flag("screen.vectors_present", false);
            }
            else
            {
                double maxDiff = 0;
                for (int i = 0; i < bareX.Length; i++) maxDiff = Math.Max(maxDiff, Math.Max(Math.Abs(bareX[i] - screenX[i]), Math.Abs(bareY[i] - screenY[i])));
                Sink.AtMost("screen.motion_vector_difference", maxDiff, 1e-6);
            }
            yield return Diagnose();
        }

        // The screen pass writes what it computes instead of the cover: the colour of the roof's middle pixel for four debug modes, with the
        // colour each should have (mode 1 magenta, mode 2 the world position about the stand-in /16 + 0.5, mode 3 the normal /2 + 0.5,
        // mode 4 amount and inside flag). Information for the diagnosis, not a verdict.
        private IEnumerator Diagnose()
        {
            PlaceCamera(30f);
            root.transform.position = Vector3.zero;
            ShellsOn(false);
            ImageStats.Rect2 rr = RoofRect(0.2f);
            int cx = (rr.X0 + rr.X1) / 2, cy = (rr.Y0 + rr.Y1) / 2;
            Ray ray = Cam.ScreenPointToRay(new Vector3(cx + 0.5f, cy + 0.5f, 0f));
            float t = (Stand.y + RoofSize.y * 0.5f - ray.origin.y) / ray.direction.y;
            Vector3 hit = ray.origin + ray.direction * t;
            Sink.Info("diag.roof_pixel_x", cx);
            Sink.Info("diag.roof_pixel_y", cy);
            Sink.Info("diag.expected_mode2_r", (hit.x - Stand.x) / 16f + 0.5f);
            Sink.Info("diag.expected_mode2_g", (hit.y - Stand.y) / 16f + 0.5f);
            Sink.Info("diag.expected_mode2_b", (hit.z - Stand.z) / 16f + 0.5f);
            Sink.Info("diag.camera_near", Cam.nearClipPlane);
            Sink.Info("diag.camera_far", Cam.farClipPlane);
            Sink.Info("diag.camera_depth_texture_mode", (int)Cam.depthTextureMode);
            for (int mode = 1; mode <= 6; mode++)
            {
                ScreenOn(true, Stand + new Vector3(0f, -1.2f, 0f), new Vector3(RoofSize.x * 0.5f + 1f, 3.2f, RoofSize.z * 0.5f + 1f), mode);
                RgbFrame f = null;
                yield return Frames(3);
                yield return GrabRgb(x => f = x);
                int i = cy * f.Width + cx;
                Sink.Info("diag.mode" + mode + "_r", f.R[i]);
                Sink.Info("diag.mode" + mode + "_g", f.G[i]);
                Sink.Info("diag.mode" + mode + "_b", f.B[i]);
            }
            ScreenOn(false, Stand, RoofSize);
        }

        // The mean luma of the cover pixels of a rectangle (the pixels the shell changed).
        private double CoverLuma(RgbFrame on, RgbFrame off, ImageStats.Rect2 r)
        {
            double sum = 0;
            int n = 0;
            for (int y = r.Y0; y < r.Y1; y++)
                for (int x = r.X0; x < r.X1; x++)
                {
                    int i = y * on.Width + x;
                    if (Luma(on, i) - Luma(off, i) <= CoverThreshold) continue;
                    sum += Luma(on, i);
                    n++;
                }
            return n == 0 ? 0.0 : sum / n;
        }
    }
}
