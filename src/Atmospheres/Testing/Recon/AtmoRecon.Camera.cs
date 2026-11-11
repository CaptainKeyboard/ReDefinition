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
        }

        private static readonly CameraEvent[] DepthEvents =
        {
            CameraEvent.AfterDepthTexture, CameraEvent.AfterGBuffer, CameraEvent.AfterForwardOpaque,
            CameraEvent.BeforeForwardAlpha, CameraEvent.AfterForwardAlpha, CameraEvent.BeforeImageEffects
        };

        private const int TapWidth = 256, TapHeight = 144;

        private IEnumerable<ProbeWait> CameraChain()
        {
            PlaceCamera(60f, 8f, 90f);
            yield return ProbeWait.Seconds(3);

            Camera main = FlightCamera.fetch.mainCamera;
            EmitText("P6.main_camera", main.name + " depth " + Num(main.depth) + " target " + TargetText(main));
            ListCameras("flight");
            Texture bound = Shader.GetGlobalTexture("_CameraDepthTexture");
            Emit("P6.depth_global_bound", bound != null);

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
                results[tap.Camera + "|" + tap.Event + "|" + (tap.Global ? "G" : "B")] = stats;
                json.BeginObject().Text("camera", tap.Camera).Text("event", tap.Event.ToString()).Text("source", tap.Global ? "global" : "builtin").Number("executed", stats[0]).Number("mean", stats[1])
                    .Number("nonFar", stats[2]).Number("min", stats[3]).Number("max", stats[4]).EndObject();
                EmitText("P6.depth." + tap.Camera.Replace(' ', '_') + "." + tap.Event + (tap.Global ? ".global" : ".builtin"), "executed=" + Num(stats[0]) + " mean=" + stats[1].ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)
                         + " nonFar=" + Num(stats[2]));
            }
            json.EndArray().EndObject();
            json.Save(DumpPath("depth-taps.json"));

            // The verdicts per camera.
            bool readable = false, fallback = false;
            var names = new HashSet<string>();
            foreach (DepthTap tap in taps) names.Add(tap.Camera);
            foreach (string camera in names)
            {
                double[] alpha = Get(results, camera, CameraEvent.BeforeForwardAlpha, true);
                double[] after = Get(results, camera, CameraEvent.AfterForwardAlpha, true);
                double[] alphaBuiltin = Get(results, camera, CameraEvent.BeforeForwardAlpha, false);
                double[] reference = null;
                CameraEvent[] preference = { CameraEvent.AfterForwardOpaque, CameraEvent.AfterDepthTexture, CameraEvent.AfterGBuffer };
                foreach (CameraEvent evt in preference)
                {
                    double[] candidate = Get(results, camera, evt, false);
                    if (candidate != null && candidate[0] > 0) { reference = candidate; break; }
                }
                bool referenceValid = reference != null && reference[2] > 0.02 && reference[2] < 0.98;
                bool alphaSame = alpha != null && alpha[0] > 0 && reference != null
                                 && Math.Abs(alpha[1] - reference[1]) <= 0.1 * Math.Abs(reference[1]) + 1e-6
                                 && Math.Abs(alpha[2] - reference[2]) <= 0.2 * Math.Abs(reference[2]) + 1e-6;
                bool afterSame = after != null && after[0] > 0 && reference != null
                                 && Math.Abs(after[1] - reference[1]) <= 0.1 * Math.Abs(reference[1]) + 1e-6;
                Emit("P6." + camera.Replace(' ', '_') + ".reference_valid", referenceValid);
                Emit("P6." + camera.Replace(' ', '_') + ".readable_at_alpha", alphaSame);
                Emit("P6." + camera.Replace(' ', '_') + ".builtin_depth_executes_at_alpha", alphaBuiltin != null && alphaBuiltin[0] > 0);
                Emit("P6." + camera.Replace(' ', '_') + ".unchanged_after_alpha", afterSame);
                if (alphaSame) readable = true;
                if (referenceValid) fallback = true;
            }
            Emit("P6.depth_readable_3000", readable);
            Emit("P6.depth_fallback_ok", fallback);
            Emit("P6.depth_ok", readable || fallback);

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
