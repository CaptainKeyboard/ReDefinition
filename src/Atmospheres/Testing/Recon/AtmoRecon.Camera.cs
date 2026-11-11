using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // P6: the camera chain and the depth texture. The list of the cameras with their masks, targets and command buffers (also from the IVA);
    // and whether _CameraDepthTexture holds the opaque scene's depth at the events around the transparent queue (BeforeForwardAlpha,
    // AfterForwardAlpha), compared with the copy taken straight after the depth texture is made (the plan's fallback: a command buffer
    // that copies the depth). The depth is copied by a Blit of BuiltinRenderTextureType.Depth into a small float target; a target that
    // still holds its start value after some frames means the event did not run for that camera.
    internal sealed partial class AtmoRecon
    {
        private sealed class DepthTap
        {
            internal string Camera;
            internal CameraEvent Event;
            // The source: the global _CameraDepthTexture as bound when the event runs (a consumer of the global), or the camera's depth
            // buffer itself (BuiltinRenderTextureType.Depth).
            internal bool Global;
            internal RenderTexture Target;
            internal CommandBuffer Buffer;
            internal Camera Owner;
            // A tap that samples another tap's target instead of the depth (the consumer of the copy at a later event).
            internal DepthTap Source;
            // The pixels read back (null where the read did not happen).
            internal Color[] Pixels;
        }

        private static readonly CameraEvent[] DepthEvents =
        {
            CameraEvent.AfterDepthTexture, CameraEvent.AfterGBuffer, CameraEvent.AfterForwardOpaque,
            CameraEvent.BeforeForwardAlpha, CameraEvent.AfterForwardAlpha, CameraEvent.BeforeImageEffects
        };

        // The events that can hold the fallback copy, in the order of preference.
        // Run1 showed the first three hold nothing on the flight camera (never executed, or zero); the copy at the start of the transparent
        // queue is the one that has content, so it is a candidate whose consumer runs after the transparent queue.
        private static readonly CameraEvent[] CopyEvents =
            { CameraEvent.AfterForwardOpaque, CameraEvent.AfterDepthTexture, CameraEvent.AfterGBuffer, CameraEvent.BeforeForwardAlpha };

        // Where a copy made at an event is read back by a later command buffer: at the start of the transparent queue, or, for a copy made
        // there, after it.
        private static CameraEvent ConsumerEvent(CameraEvent copy)
        {
            return copy == CameraEvent.BeforeForwardAlpha ? CameraEvent.AfterForwardAlpha : CameraEvent.BeforeForwardAlpha;
        }

        private const int TapWidth = 256, TapHeight = 144;

        private bool uiHidden;

        // Whether the frame the probe reads follows the scene. Run2 read one frame (luma 0.4232, peak 1.49) in every state of P7, P8 and
        // P10, with the disc on and off: the target of the flight camera did not change. A flat coloured wall is put in front of the camera
        // and the centre of the frame is read with and without it from the camera's target and from the screen; the source that sees the
        // wall is used from then on (the screen with the interface hidden).
        private IEnumerable<ProbeWait> FrameLiveness()
        {
            Vessel pod = FlightGlobals.ActiveVessel;
            Shader flat = Shader.Find("Hidden/Internal-Colored");
            if (pod == null || flat == null)
            {
                EmitText("Env.frame_source", "unknown: no vessel or no flat shader");
                yield break;
            }
            PlaceCamera(60f, 8f, 90f);
            yield return ProbeWait.Seconds(3);
            Camera main = FlightCamera.fetch.mainCamera;
            var without = new double[2];
            var with = new double[2];
            for (int mode = 0; mode < 2; mode++)
            {
                host.UseScreen = mode == 1;
                foreach (ProbeWait w in Capture()) yield return w;
                without[mode] = lastFrame != null ? Mean(lastFrame.Lumas(0.5, 0.5, 0.04, 0.04)) : double.NaN;
            }
            // An opaque white wall at the geometry queue (the material of the disc fixtures): the raw material of this shader blends and
            // sits in the transparent queue by default, and a red wall could have the luma of the scene it stands in.
            Material material = ReconMesh.Flat(Color.white, 2000);
            Vector3d cameraAt = main.transform.position, forward = main.transform.forward;
            var wall = new ReconBall(pod, cameraAt + forward * 4.0 - pod.CoMD, 3f, material, 0);
            Action follow = wall.Update;
            host.PerFrame.Add(follow);
            yield return ProbeWait.Frames(SettleFrames);
            for (int mode = 0; mode < 2; mode++)
            {
                host.UseScreen = mode == 1;
                foreach (ProbeWait w in Capture()) yield return w;
                with[mode] = lastFrame != null ? Mean(lastFrame.Lumas(0.5, 0.5, 0.04, 0.04)) : double.NaN;
            }
            host.PerFrame.Remove(follow);
            wall.Destroy();
            UnityEngine.Object.Destroy(material);
            bool liveTarget = Math.Abs(with[0] - without[0]) > 0.03, liveScreen = Math.Abs(with[1] - without[1]) > 0.03;
            Emit("Env.frame_luma_target_without", without[0]);
            Emit("Env.frame_luma_target_with", with[0]);
            Emit("Env.frame_luma_screen_without", without[1]);
            Emit("Env.frame_luma_screen_with", with[1]);
            Emit("Env.frame_live_target", liveTarget);
            Emit("Env.frame_live_screen", liveScreen);
            frameLive = liveTarget || liveScreen;
            Emit("Env.frame_live_any", frameLive);
            host.UseScreen = !liveTarget && liveScreen;
            if (host.UseScreen)
            {
                try
                {
                    GameEvents.onHideUI.Fire();
                    uiHidden = true;
                }
                catch (Exception e)
                {
                    Say("the interface could not be hidden (" + e.GetType().Name + " " + e.Message + ")");
                }
            }
            EmitText("Env.frame_source", host.UseScreen ? "screen" : liveTarget ? "target" : "none (neither source saw the wall)");
        }

        // Whether a frame source saw the wall of FrameLiveness. The steps that read the frame (P6 balls through the screen are not among
        // them: they read command-buffer targets) run only while this holds; a dead frame would make their rows say nothing.
        private bool frameLive;

        // Starts a step that reads the frame; false (with its error row) where no frame source followed the scene.
        private bool FrameStepAllowed(string id)
        {
            if (frameLive) return true;
            Say(id + " left out: no frame source followed the scene (Env.frame_live_any 0)");
            EmitText(id + ".error", "left out: no frame source followed the scene (Env.frame_live_any 0)");
            return false;
        }

        private static double Mean(List<float> values)
        {
            if (values == null || values.Count == 0) return double.NaN;
            double sum = 0;
            foreach (float v in values) sum += v;
            return sum / values.Count;
        }

        private IEnumerable<ProbeWait> CameraChain()
        {
            PlaceCamera(60f, 8f, 90f);
            yield return ProbeWait.Seconds(3);

            Camera main = FlightCamera.fetch.mainCamera;
            EmitText("P6.main_camera", main.name + " depth " + Num(main.depth) + " target " + TargetText(main));
            ListCameras("flight");
            Texture bound = Shader.GetGlobalTexture("_CameraDepthTexture");
            Emit("P6.depth_global_bound", bound != null);

            // Two opaque balls at known distances in front of the camera and a point of empty view: run1 judged a copy valid only when it
            // showed both sky and surface, which a frame without sky pixels can never do. The test is now that the depth holds the
            // balls: the value under the near ball differs from the empty view, the far ball lies between, and the order follows the
            // distance (whatever the depth encoding is).
            Vessel pod = FlightGlobals.ActiveVessel;
            Shader ballShader = FirstShader("Legacy Shaders/Diffuse", "Diffuse", "Standard", "KSP/Diffuse");
            var balls = new List<ReconBall>();
            Material ballMaterial = null;
            Action followBalls = null;
            BallLayout layout = null;
            if (ballShader != null && pod != null)
            {
                ballMaterial = new Material(ballShader) { color = new Color(0.5f, 0.5f, 0.5f, 1f) };
                // Five balls of the same apparent size at eye depths from 2.5 to 40 m along a row across the frame, so that the stored value
                // can be set against the value the camera's projection predicts (a pair of balls only ever said "ordered").
                layout = new BallLayout(main);
                foreach (double depth in BallLayout.EyeDepths)
                {
                    int index = balls.Count;
                    Vector3 at = layout.WorldPosition(index);
                    balls.Add(new ReconBall(pod, (Vector3d)at - pod.CoMD, (float)(0.06 * depth), ballMaterial, 0));
                }
                followBalls = () =>
                {
                    foreach (ReconBall ball in balls) ball.Update();
                };
                host.PerFrame.Add(followBalls);
                bool inView = true;
                for (int i = 0; i < balls.Count; i++)
                {
                    Vector3 view = ViewportOf(main, balls[i].Position);
                    if (!(view.z > 0 && view.x > 0.05f && view.x < 0.95f)) inView = false;
                }
                Emit("P6.balls_in_view", inView);
                Emit("P6.uses_reversed_z", SystemInfo.usesReversedZBuffer);
                Emit("P6.camera_near", main.nearClipPlane);
                Emit("P6.camera_far", main.farClipPlane);
            }
            else
            {
                EmitText("P6.error", "no lit shader or no vessel for the calibration balls");
            }

            // The taps on the scene cameras.
            var taps = new List<DepthTap>();
            try
            {
                foreach (Camera camera in Camera.allCameras)
                {
                    if (!camera.enabled || !(camera.name == "Camera 00" || camera.name == "Camera 01" || camera == main)) continue;
                    foreach (CameraEvent evt in DepthEvents)
                    for (int kind = 0; kind < 2; kind++)
                    {
                        var tap = new DepthTap { Camera = camera.name, Event = evt, Owner = camera, Global = kind == 1 };
                        tap.Target = new RenderTexture(TapWidth, TapHeight, 0, RenderTextureFormat.RFloat);
                        tap.Target.Create();
                        RenderTexture active = RenderTexture.active;
                        RenderTexture.active = tap.Target;
                        GL.Clear(false, true, new Color(-1f, -1f, -1f, -1f));
                        RenderTexture.active = active;
                        tap.Buffer = new CommandBuffer { name = "recon depth " + evt + (tap.Global ? " global" : " builtin") };
                        tap.Buffer.Blit(tap.Global ? new RenderTargetIdentifier("_CameraDepthTexture") : new RenderTargetIdentifier(BuiltinRenderTextureType.Depth),
                                        tap.Target);
                        camera.AddCommandBuffer(evt, tap.Buffer);
                        taps.Add(tap);
                    }
                }
                // The consumers of the copy: at BeforeForwardAlpha, where the transparent queue (3000 and up) begins, a blit reads the copy
                // made earlier (as a material at that queue would sample it) into a target of its own.
                foreach (CameraEvent evt in CopyEvents)
                {
                    DepthTap source = taps.Find(t => t.Owner == main && t.Event == evt && !t.Global);
                    if (source == null) continue;
                    var tap = new DepthTap { Camera = main.name, Event = ConsumerEvent(evt), Owner = main, Source = source };
                    tap.Target = new RenderTexture(TapWidth, TapHeight, 0, RenderTextureFormat.RFloat);
                    tap.Target.Create();
                    RenderTexture active = RenderTexture.active;
                    RenderTexture.active = tap.Target;
                    GL.Clear(false, true, new Color(-1f, -1f, -1f, -1f));
                    RenderTexture.active = active;
                    tap.Buffer = new CommandBuffer { name = "recon depth consumer of " + evt };
                    tap.Buffer.Blit(new RenderTargetIdentifier(source.Target), tap.Target);
                    main.AddCommandBuffer(ConsumerEvent(evt), tap.Buffer);
                    taps.Add(tap);
                }
            }
            catch (Exception e)
            {
                Say("P6: the depth taps could not all be set (" + e.GetType().Name + " " + e.Message + ")");
            }
            Say("P6: " + taps.Count + " depth taps set");
            yield return ProbeWait.Frames(30);

            var results = new Dictionary<string, double[]>();
            var json = new ReconJson().BeginObject().BeginArray("taps");
            foreach (DepthTap tap in taps)
            {
                double[] stats = ReadTap(tap);
                string sourceName = tap.Source != null ? "copy of " + tap.Source.Event : tap.Global ? "global" : "builtin";
                results[tap.Camera + "|" + tap.Event + "|" + (tap.Source != null ? "C" + tap.Source.Event : tap.Global ? "G" : "B")] = stats;
                json.BeginObject().Text("camera", tap.Camera).Text("event", tap.Event.ToString()).Text("source", sourceName).Number("executed", stats[0]).Number("mean", stats[1])
                    .Number("nonFar", stats[2]).Number("min", stats[3]).Number("max", stats[4]).EndObject();
                EmitText("P6.depth." + tap.Camera.Replace(' ', '_') + "." + tap.Event + "." + sourceName.Replace(' ', '_'), "executed=" + Num(stats[0]) + " mean=" + stats[1].ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)
                         + " nonFar=" + Num(stats[2]));
            }
            json.EndArray().EndObject();
            json.Save(DumpPath("depth-taps.json"));

            // The verdicts per camera.
            bool readable = false, fallback = false, fallbackMain = false, readableMain = false, consumerOk = false;
            var names = new HashSet<string>();
            foreach (DepthTap tap in taps) names.Add(tap.Camera);
            foreach (string camera in names)
            {
                double[] alpha = Get(results, camera, CameraEvent.BeforeForwardAlpha, true);
                double[] after = Get(results, camera, CameraEvent.AfterForwardAlpha, true);
                double[] alphaBuiltin = Get(results, camera, CameraEvent.BeforeForwardAlpha, false);
                double[] reference = null;
                CameraEvent referenceEvent = CameraEvent.BeforeForwardAlpha;
                // The first event whose copy is valid: it ran, it varies over the frame, and it holds the calibration balls in the right
                // order (DepthHoldsBalls). Every event's validity goes to a row.
                bool chosenValid = false;
                bool isMain = camera == main.name;
                foreach (CameraEvent evt in CopyEvents)
                {
                    double[] candidate = Get(results, camera, evt, false);
                    DepthTap candidateTap = taps.Find(t => t.Camera == camera && t.Event == evt && !t.Global && t.Source == null);
                    bool valid = isMain && candidate != null && candidate[0] > 0 && candidate[4] - candidate[3] > 0.05 && DepthHoldsBalls(candidateTap, layout, main, "copy_" + evt);
                    if (isMain) Emit("P6.copy_valid_" + evt, valid);
                    if (valid && !chosenValid)
                    {
                        reference = candidate;
                        referenceEvent = evt;
                        chosenValid = true;
                    }
                }
                bool referenceValid = chosenValid;
                // Readable: the global texture at the start of the transparent queue varies over the frame, holds the balls in order and is
                // the same at its end (nothing rebound or overwrote it during the queue).
                DepthTap alphaTap = taps.Find(t => t.Camera == camera && t.Event == CameraEvent.BeforeForwardAlpha && t.Global && t.Source == null);
                bool alphaSame = isMain && alpha != null && alpha[0] > 0 && alpha[4] - alpha[3] > 0.05 && DepthHoldsBalls(alphaTap, layout, main, "global_alpha");
                bool afterSame = after != null && after[0] > 0 && alpha != null && Math.Abs(after[1] - alpha[1]) <= 0.01 * Math.Abs(alpha[1]) + 1e-6;
                if (!afterSame) alphaSame = false;
                Emit("P6." + camera.Replace(' ', '_') + ".reference_valid", referenceValid);
                Emit("P6." + camera.Replace(' ', '_') + ".readable_at_alpha", alphaSame);
                Emit("P6." + camera.Replace(' ', '_') + ".builtin_depth_executes_at_alpha", alphaBuiltin != null && alphaBuiltin[0] > 0);
                Emit("P6." + camera.Replace(' ', '_') + ".unchanged_after_alpha", afterSame);
                if (alphaSame) readable = true;
                if (referenceValid) fallback = true;
                // The verdict counts the camera that draws the pod and the ocean. The copy is used by a consumer at the start of the
                // transparent queue: the blit at BeforeForwardAlpha that reads it must see the mean and the sky/surface mix of the reference.
                if (camera == main.name)
                {
                    if (alphaSame) readableMain = true;
                    if (referenceValid) fallbackMain = true;
                    double[] consumed;
                    results.TryGetValue(camera + "|" + ConsumerEvent(referenceEvent) + "|C" + referenceEvent, out consumed);
                    DepthTap consumerTap = taps.Find(t => t.Source != null && t.Source.Event == referenceEvent && t.Camera == camera);
                    consumerOk = referenceValid && consumed != null && consumed[0] > 0 && reference != null
                                 && Math.Abs(consumed[1] - reference[1]) <= 0.01 * Math.Abs(reference[1]) + 1e-6
                                 && DepthHoldsBalls(consumerTap, layout, main, "consumer_of_" + referenceEvent);
                    EmitText("P6.fallback_event", referenceEvent.ToString());
                    Emit("P6.copy_consumed_at_alpha", consumerOk);
                }
            }
            Emit("P6.depth_readable_3000", readable);
            Emit("P6.depth_readable_main_camera", readableMain);
            Emit("P6.depth_fallback_ok", fallback);
            Emit("P6.depth_fallback_main_camera", fallbackMain);
            // Direct use on the main camera, or the copy valid and still usable at the start of the transparent queue; RECON.md reads the
            // two rows readable and fallback and decides which D32, D37 and D38 take.
            Emit("P6.depth_ok", readableMain || (fallbackMain && consumerOk));

            foreach (DepthTap tap in taps)
            {
                try
                {
                    tap.Owner.RemoveCommandBuffer(tap.Event, tap.Buffer);
                    tap.Buffer.Release();
                    tap.Target.Release();
                }
                catch (Exception)
                {
                }
            }

            if (followBalls != null) host.PerFrame.Remove(followBalls);
            foreach (ReconBall ball in balls) ball.Destroy();
            if (ballMaterial != null) UnityEngine.Object.Destroy(ballMaterial);

            // The IVA: the cameras there, and the order the interior mods leave them in.
            bool iva = false;
            try
            {
                iva = CameraManager.Instance != null && CameraManager.Instance.SetCameraIVA();
            }
            catch (Exception e)
            {
                Say("P6: the IVA could not be entered (" + e.GetType().Name + " " + e.Message + ")");
            }
            Emit("P6.iva_entered", iva);
            if (iva)
            {
                yield return ProbeWait.Frames(60);
                ListCameras("iva");
                CameraManager.Instance.SetCameraFlight();
                yield return ProbeWait.Seconds(2);
            }
        }

        // The calibration balls of P6: equal in apparent size, at eye depths from 2.5 to 40 m, in a row across the middle of the frame,
        // plus a point of empty view above them. The camera's projection predicts the stored depth of each (ReconMath.DepthExpected).
        private sealed class BallLayout
        {
            internal static readonly double[] EyeDepths = { 2.5, 5.0, 10.0, 20.0, 40.0 };
            internal static readonly Vector3 Empty = new Vector3(0.5f, 0.85f, 1f);
            private readonly Camera camera;
            private readonly Vector3[] viewports = new Vector3[EyeDepths.Length];

            internal BallLayout(Camera camera)
            {
                this.camera = camera;
                for (int i = 0; i < viewports.Length; i++) viewports[i] = new Vector3(0.12f + 0.19f * i, 0.5f, 1f);
            }

            internal Vector3 Viewport(int index)
            {
                return viewports[index];
            }

            // The eye depth of the surface the centre pixel of a ball sees: the near side of the sphere, radius 0.06 times the eye depth of its
            // centre, along the ray through the ball's viewport point (the expected value is for that surface, not for the ball's centre).
            internal double SurfaceDepth(int index)
            {
                Ray ray = camera.ViewportPointToRay(viewports[index]);
                double along = Vector3.Dot(ray.direction, camera.transform.forward);
                return EyeDepths[index] - 0.06 * EyeDepths[index] * along;
            }

            // The world position at that eye depth (the distance along the camera's forward axis) on the ray through the ball's viewport point.
            internal Vector3 WorldPosition(int index)
            {
                Ray ray = camera.ViewportPointToRay(viewports[index]);
                double along = Vector3.Dot(ray.direction, camera.transform.forward);
                return ray.origin + ray.direction * (float)(EyeDepths[index] / along);
            }
        }

        // Whether a tap's pixels hold the calibration balls: the value under each ball is read (the render texture can come back upside
        // down, so the flipped reading counts too and says so in a row), the values must be ordered with the eye depth, and they must
        // agree within 10 % with the value the camera's projection predicts for one of the encodings a depth texture can hold. The measured
        // and the predicted values go to rows, so that a verdict that fails says which encoding came closest.
        private bool DepthHoldsBalls(DepthTap tap, BallLayout layout, Camera camera, string label)
        {
            if (tap == null || tap.Pixels == null || layout == null) return false;
            double near = camera.nearClipPlane, far = camera.farClipPlane;
            bool reversed = SystemInfo.usesReversedZBuffer;
            for (int flip = 0; flip < 2; flip++)
            {
                var measured = new double[BallLayout.EyeDepths.Length];
                for (int i = 0; i < measured.Length; i++) measured[i] = Texel(tap, layout.Viewport(i), flip == 1);
                double empty = Texel(tap, BallLayout.Empty, flip == 1);
                int best;
                double bestError;
                var surfaces = new double[measured.Length];
                for (int i = 0; i < surfaces.Length; i++) surfaces[i] = layout.SurfaceDepth(i);
                bool ok = ReconMath.DepthAgrees(measured, surfaces, near, far, reversed, 0.10, 5e-4, 5e-4, out best, out bestError);
                if (flip == 0 || ok)
                {
                    string flipText = flip == 1 ? "_flipped" : "";
                    for (int i = 0; i < measured.Length; i++)
                    {
                        double[] expected = ReconMath.DepthExpected(surfaces[i], near, far, reversed);
                        string depthKey = Num(BallLayout.EyeDepths[i]).Replace('.', '_');
                        Emit("P6.balls_" + label + flipText + "_d" + depthKey + "_measured", measured[i]);
                        if (flip == 0) Emit("P6.balls_" + label + "_d" + depthKey + "_expected_raw", expected[0]);
                    }
                    Emit("P6.balls_" + label + flipText + "_empty", empty);
                    if (best >= 0)
                    {
                        EmitText("P6.balls_" + label + flipText + "_closest_encoding", ReconMath.DepthEncodings[best]);
                        Emit("P6.balls_" + label + flipText + "_max_rel_error", bestError);
                    }
                }
                if (ok)
                {
                    Emit("P6.balls_" + label + "_flipped", flip == 1);
                    return true;
                }
            }
            return false;
        }

        // The mean of the written pixels in a 3 x 3 block around a viewport point; NaN where none was written.
        private static double Texel(DepthTap tap, Vector3 view, bool flip)
        {
            int cx = Mathf.Clamp((int)(view.x * TapWidth), 1, TapWidth - 2);
            int cy = Mathf.Clamp((int)((flip ? 1f - view.y : view.y) * TapHeight), 1, TapHeight - 2);
            double sum = 0;
            int n = 0;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    float v = tap.Pixels[(cy + dy) * TapWidth + cx + dx].r;
                    if (v < -0.5f) continue;
                    sum += v;
                    n++;
                }
            }
            return n == 0 ? double.NaN : sum / n;
        }

        private static double[] Get(Dictionary<string, double[]> results, string camera, CameraEvent evt, bool global)
        {
            double[] stats;
            return results.TryGetValue(camera + "|" + evt + "|" + (global ? "G" : "B"), out stats) ? stats : null;
        }

        // executed (1/0), mean of the written values, share of pixels that are neither the near nor the far value, min, max.
        private static double[] ReadTap(DepthTap tap)
        {
            RenderTexture active = RenderTexture.active;
            RenderTexture.active = tap.Target;
            var read = new Texture2D(TapWidth, TapHeight, TextureFormat.RFloat, false);
            read.ReadPixels(new Rect(0, 0, TapWidth, TapHeight), 0, 0, false);
            RenderTexture.active = active;
            Color[] pixels = read.GetPixels();
            tap.Pixels = pixels;
            UnityEngine.Object.Destroy(read);
            int written = 0, notZero = 0, notOne = 0;
            double sum = 0, min = double.MaxValue, max = double.MinValue;
            foreach (Color c in pixels)
            {
                if (c.r < -0.5f) continue;
                written++;
                sum += c.r;
                if (c.r > 1e-5f) notZero++;
                if (c.r < 1f - 1e-5f) notOne++;
                if (c.r < min) min = c.r;
                if (c.r > max) max = c.r;
            }
            if (written == 0) return new double[] { 0, 0, 0, 0, 0 };
            double nonFar = Math.Min(notZero, notOne) / (double)written;
            return new double[] { 1, sum / written, nonFar, min, max };
        }

        private static string TargetText(Camera camera)
        {
            return camera.targetTexture != null ? camera.targetTexture.width + "x" + camera.targetTexture.height : "screen";
        }

        // Every camera with its settings and the number of command buffers per event, to the log, a row and a dump.
        private void ListCameras(string where)
        {
            var json = new ReconJson().BeginObject().Text("where", where).BeginArray("cameras");
            var summary = new StringBuilder();
            int count = 0;
            foreach (Camera camera in Camera.allCameras)
            {
                count++;
                var buffers = new StringBuilder();
                foreach (CameraEvent evt in Enum.GetValues(typeof(CameraEvent)))
                {
                    int n = camera.GetCommandBuffers(evt).Length;
                    if (n > 0) buffers.Append(evt).Append('=').Append(n).Append(' ');
                }
                json.BeginObject().Text("name", camera.name).Number("depth", camera.depth).Flag("enabled", camera.enabled)
                    .Text("cullingMask", camera.cullingMask.ToString("x")).Text("target", TargetText(camera)).Text("renderingPath", camera.actualRenderingPath.ToString())
                    .Text("clearFlags", camera.clearFlags.ToString()).Text("depthTextureMode", camera.depthTextureMode.ToString())
                    .Number("near", camera.nearClipPlane).Number("far", camera.farClipPlane).Flag("hdr", camera.allowHDR)
                    .Text("buffers", buffers.ToString().Trim()).EndObject();
                summary.Append(camera.name).Append("[d").Append(Num(camera.depth)).Append(camera.enabled ? "" : " off").Append(' ').Append(TargetText(camera)).Append("] ");
            }
            json.EndArray().EndObject();
            json.Save(DumpPath("cameras-" + where + ".json"));
            Emit("P6.cameras_" + where, count);
            EmitText("P6.order_" + where, summary.ToString().Trim());
            Say("P6: cameras (" + where + "): " + summary);
        }
    }
}
