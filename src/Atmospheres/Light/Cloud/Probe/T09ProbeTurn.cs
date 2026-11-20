using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // Series K-T2, the pinned turn (-t09Pin with -t09Turn <frames>; the history work, 2026-11-20: the owner judges the reconstruction in a TURN on full-size game pictures):
    // in the pinned state of T09ProbePin (clock, noise, maps and heights held at -t09Ut), the flight camera orbits the vessel at a fixed step a FRAME, so every variant sees
    // the same poses in the same frames, and the screen as the player sees it (after the upscaler and the post-processing, UI hidden) is kept at fixed frames of the turn.
    //   pose:     -t09TurnPad (the vessel stays where the save has it, on the launch pad) or the ground view placed as in T09ProbePin; the camera at -t09TurnDistM from the
    //             vessel, its aim at compass heading -t09TurnHeadingDeg and elevation -t09TurnElevDeg (Aim, once); every variant starts from the flight camera's heading and
    //             pitch that aim gave, so the poses are the same numbers in every variant.
    //   per variant (plain, then each -t09PinLimits set): the set applied, the camera at the start pose, the clouds' history and the upscaler's history cut there (at the
    //             host's first jitter phase), PinFrames frames held; then -t09Turn frames, each turning the camera by -t09TurnStepDeg (the heading set in the probe's tick,
    //             which runs before the flight camera's LateUpdate, so the frame renders at that heading); at the frames of -t09TurnCapture (1 is the first turned frame) the
    //             screen is copied into a render texture at that frame's end (ScreenCapture.CaptureScreenshotIntoRenderTexture: a copy on the GPU, no read and no file in the
    //             turn, so the turn's frames are not stalled); then the camera rests at the end pose PinFrames frames (no cut: the history settles as it would for the player)
    //             and the still is copied the same way, with ScreenCapture.CaptureScreenshot of the same frame beside it (the reference for orientation and colour).
    //             After that the textures are read back and written as PNGs t09-<start>-turn-<variant>-f<frame>-<w>x<h>.png and ...-still-...; the set is reverted.
    //   rows:     T09.turn.<variant>.fps (the turn's frames over their real time), .frame_ms, .consecutive (the probe's ticks were consecutive frames), .az_f<frame> (the
    //             camera's compass heading at each kept frame, the same in every variant), .recon_block / .recon_march_pass / .recon_count_target / .recon_scene_depth
    //             (what the player view's reconstruction ran), .written (the PNGs written).
    internal sealed partial class T09Probe
    {
        private int TurnFrames
        {
            get { return (int)NumberArg("-t09Turn", 0.0); }
        }

        private bool TurnPad
        {
            get { return CommandLine.Has(Args(), "-t09TurnPad", true); }
        }

        private int[] TurnCaptureFrames(int n)
        {
            string text = CommandLine.After(Args(), "-t09TurnCapture", true);
            var list = new List<int>();
            foreach (string part in (string.IsNullOrEmpty(text) ? "20,21,22,40,41,42" : text).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int f;
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out f) && f >= 1 && f <= n && !list.Contains(f)) list.Add(f);
            }
            list.Sort();
            return list.ToArray();
        }

        // The pinned turn after PinSettleSteps (the clock, the noise, the maps and the heights stand): every variant in turn. False rows fail the start by name.
        private IEnumerable<ProbeWait> PinTurn(OwnCloudModule own, List<PinLimit> limits, double viewDistM)
        {
            int n = TurnFrames;
            double stepDeg = NumberArg("-t09TurnStepDeg", 0.6), headingDeg = NumberArg("-t09TurnHeadingDeg", 70.0), elevationDeg = NumberArg("-t09TurnElevDeg", 5.0);
            double distM = NumberArg("-t09TurnDistM", viewDistM);
            int[] keep = TurnCaptureFrames(n);
            pinLabel = "turn";
            if (!PinStep("turn_args", n >= 2 && keep.Length > 0 && stepDeg > 0.0, n + " frames, step " + stepDeg.ToString("0.###", CultureInfo.InvariantCulture) + " degrees a frame, kept frames "
                                                                              + string.Join(",", Array.ConvertAll(keep, k => k.ToString(CultureInfo.InvariantCulture))))) yield break;
            foreach (ProbeWait w in Aim(headingDeg, elevationDeg, distM)) yield return w;
            float hdg0 = Game.CameraHeading, pitch0 = Game.CameraPitch;
            T09Pose start = Game.ReadPose();
            AtmospheresLog.Say("T09 turn: start pose wanted " + headingDeg.ToString("0.0", CultureInfo.InvariantCulture) + "/" + elevationDeg.ToString("0.0", CultureInfo.InvariantCulture)
                               + ", got " + start.AzimuthDeg.ToString("0.00", CultureInfo.InvariantCulture) + "/" + start.ElevationDeg.ToString("0.00", CultureInfo.InvariantCulture)
                               + ", camera " + start.CameraToVesselM.ToString("0.0", CultureInfo.InvariantCulture) + " m from the vessel, " + start.CameraAgl.ToString("0.0", CultureInfo.InvariantCulture)
                               + " m over the terrain; flight camera heading " + hdg0.ToString("R", CultureInfo.InvariantCulture) + " pitch " + pitch0.ToString("R", CultureInfo.InvariantCulture)
                               + (aimNote != null ? "; aim: " + aimNote : ""));
            PinEmit("start_az", start.AzimuthDeg);
            PinEmit("start_el", start.ElevationDeg);
            var sets = new List<PinLimit> { null };
            sets.AddRange(limits);
            var restore = new List<KeyValuePair<string, double>>();
            int sw = Game.ScreenWidth, sh = Game.ScreenHeight;
            float stepRad = (float)(stepDeg * Math.PI / 180.0);
            foreach (PinLimit set in sets)
            {
                string name = set == null ? "plain" : set.Name;
                pinLabel = "turn-" + name;
                if (set != null) PinLimitApply(set, restore);
                Game.CameraHeading = hdg0;
                Game.CameraPitch = pitch0;
                // Two frames for the switches to reach the frame's record and the camera to stand at the start pose.
                yield return ProbeWait.Frames(2);

                // The cut at the start pose: the clouds' history and the upscaler's (DLSS) together, so every variant starts alike.
                double jitterGiveUp = Elapsed + PinJitterTimeoutS;
                while (!OwnCloudModule.HostJitterAtFirstPhase() && Elapsed < jitterGiveUp) yield return ProbeWait.Frames(1);
                own.PinNoise();
                int cutsBefore = own.HistoryCuts;
                own.FrameOrigin = Game.FrameCount;
                CloudBridgeLights.RequestHistoryReset("pinned turn");
                try { new ApiBridge().RequestHistoryReset("ReDefinition pinned turn"); }
                catch (Exception e) { AtmospheresLog.Say("T09 turn: the host's history reset threw " + e.GetType().Name + " " + e.Message); }
                yield return ProbeWait.Frames(1);
                if (!PinStep("history_cut", own.HistoryCuts > cutsBefore, "cuts " + cutsBefore + " -> " + own.HistoryCuts)) { PinLimitRevert(restore); continue; }
                int cutsAfter = own.HistoryCuts;
                double giveUp = Elapsed + PinHoldTimeoutS;
                while (own.HistoryFramesSinceCut < PinFrames - 1 && own.HistoryCuts == cutsAfter && Elapsed < giveUp) yield return ProbeWait.Frames(1);
                if (!PinStep("hold", own.HistoryFramesSinceCut >= PinFrames - 1 && own.HistoryCuts == cutsAfter, "frames since the cut " + own.HistoryFramesSinceCut)) { PinLimitRevert(restore); continue; }

                // The turn: the heading of frame i is the start's plus i steps; the kept frames are copied on the GPU at their frame's end.
                var targets = new RenderTexture[keep.Length + 1];
                for (int k = 0; k < targets.Length; k++)
                {
                    targets[k] = new RenderTexture(sw, sh, 0, RenderTextureFormat.ARGB32) { name = "T09 turn frame " + k };
                    targets[k].Create();
                }
                var azimuths = new double[keep.Length];
                int firstFrame = Game.FrameCount, lastFrame = firstFrame;
                bool consecutive = true;
                string fault = null;
                float t0 = Time.realtimeSinceStartup;
                for (int i = 1; i <= n; i++)
                {
                    Game.CameraHeading = hdg0 + i * stepRad;
                    int k = Array.IndexOf(keep, i);
                    if (k >= 0)
                    {
                        try { ScreenCapture.CaptureScreenshotIntoRenderTexture(targets[k]); }
                        catch (Exception e) { fault = fault ?? "the copy of frame " + i + " threw " + e.GetType().Name + " " + e.Message; }
                    }
                    int before = Game.FrameCount;
                    yield return ProbeWait.Frames(1);
                    if (Game.FrameCount != before + 1) consecutive = false;
                    lastFrame = Game.FrameCount;
                    // The camera of the frame just drawn (its LateUpdate has run).
                    if (k >= 0)
                    {
                        double az, el;
                        Game.CameraAim(out az, out el);
                        azimuths[k] = az;
                    }
                }
                float seconds = Time.realtimeSinceStartup - t0;
                PinEmit("fps", n / Math.Max(1e-3, seconds));
                PinEmit("frame_ms", 1000.0 * seconds / n);
                Emit("T09.turn." + name + ".fps", n / Math.Max(1e-3, seconds));
                PinStep("consecutive", consecutive && lastFrame - firstFrame == n, "the turn's " + n + " ticks were frames " + firstFrame + " to " + lastFrame);
                for (int k = 0; k < keep.Length; k++) PinEmit("az_f" + keep[k].ToString(CultureInfo.InvariantCulture), azimuths[k]);
                LayerReconstructor player = own.Driver != null ? own.Driver.Player : null;
                if (player != null)
                {
                    PinEmit("recon_block", player.LastBlock);
                    PinEmit("recon_march_pass", player.LastMarchPass);
                    PinEmit("recon_count_target", player.LastCountTarget ? 1.0 : 0.0);
                    PinEmit("recon_scene_depth", player.LastSceneDepth ? 1.0 : 0.0);
                }

                // The still at the end pose: the history settles, no cut.
                int restFrom = Game.FrameCount;
                while (Game.FrameCount - restFrom < PinFrames) yield return ProbeWait.Frames(1);
                string stem = "t09-" + (StartId ?? "nostart") + "-turn-" + name;
                string stillRef = System.IO.Path.Combine(SeriesPictures.Folder, stem + "-still-" + sw + "x" + sh + "-screenshot.png");
                try
                {
                    System.IO.Directory.CreateDirectory(SeriesPictures.Folder);
                    ScreenCapture.CaptureScreenshotIntoRenderTexture(targets[keep.Length]);
                    ScreenCapture.CaptureScreenshot(stillRef);
                }
                catch (Exception e) { fault = fault ?? "the still threw " + e.GetType().Name + " " + e.Message; }
                yield return ProbeWait.Frames(2);
                double endAz, endEl;
                Game.CameraAim(out endAz, out endEl);
                PinEmit("end_az", endAz);

                // The textures read back and written, after the turn (the frames now are not measured).
                int written = 0;
                for (int k = 0; k < targets.Length; k++)
                {
                    string file = k < keep.Length ? stem + "-f" + keep[k].ToString("00", CultureInfo.InvariantCulture) + "-" + sw + "x" + sh + ".png" : stem + "-still-" + sw + "x" + sh + ".png";
                    AsyncGPUReadbackRequest req = AsyncGPUReadback.Request(targets[k], 0, TextureFormat.RGBA32);
                    double readGiveUp = Elapsed + PinReadTimeoutS;
                    while (!req.done && Elapsed < readGiveUp) yield return ProbeWait.Frames(1);
                    if (!req.done || req.hasError)
                    {
                        fault = fault ?? "the read of " + file + (req.hasError ? " failed" : " timed out");
                        continue;
                    }
                    try
                    {
                        byte[] raw = req.GetData<byte>().ToArray();
                        // The copy of the back buffer stands upside down where the graphics API's texture origin is at the top (D3D): the rows are put upright.
                        if (SystemInfo.graphicsUVStartsAtTop) FlipRows(raw, sw, sh);
                        var tex = new Texture2D(sw, sh, TextureFormat.RGBA32, false);
                        tex.LoadRawTextureData(raw);
                        tex.Apply(false);
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(SeriesPictures.Folder, file), tex.EncodeToPNG());
                        UnityEngine.Object.Destroy(tex);
                        written++;
                    }
                    catch (Exception e) { fault = fault ?? "writing " + file + " threw " + e.GetType().Name + " " + e.Message; }
                }
                foreach (RenderTexture rt in targets)
                {
                    rt.Release();
                    UnityEngine.Object.Destroy(rt);
                }
                PinEmit("written", written);
                PinStep("pictures", fault == null && written == targets.Length && System.IO.File.Exists(stillRef),
                        fault ?? (written + " of " + targets.Length + " pictures written, the still's screenshot " + (System.IO.File.Exists(stillRef) ? "written" : "missing")));
                AtmospheresLog.Say("T09 turn " + name + ": " + (n / Math.Max(1e-3, seconds)).ToString("0.0", CultureInfo.InvariantCulture) + " fps over " + n + " frames, kept "
                                   + written + " pictures, end heading " + endAz.ToString("0.00", CultureInfo.InvariantCulture));
                if (set != null) PinLimitRevert(restore);
                yield return ProbeWait.Frames(2);
            }
            pinLabel = "ground";
        }

        private static void FlipRows(byte[] raw, int width, int height)
        {
            int row = width * 4;
            var tmp = new byte[row];
            for (int y = 0; y < height / 2; y++)
            {
                int a = y * row, b = (height - 1 - y) * row;
                Buffer.BlockCopy(raw, a, tmp, 0, row);
                Buffer.BlockCopy(raw, b, raw, a, row);
                Buffer.BlockCopy(tmp, 0, raw, b, row);
            }
        }
    }
}
