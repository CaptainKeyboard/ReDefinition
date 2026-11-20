using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // Series K-T2, the pinned turn (-t09Pin with -t09Turn <frames>; the history work, 2026-11-20: the owner judges the reconstruction in a TURN on full-size game pictures):
    // in the pinned state of T09ProbePin (clock, noise, wind and drift, maps and heights held at -t09Ut), the flight camera orbits the vessel at a fixed step a FRAME, so every
    // variant sees the same poses in the same frames, and the screen as the player sees it (after the upscaler and the post-processing, UI hidden) is kept at fixed frames.
    //   pose:     -t09TurnPad (the vessel stays where the save has it, on the launch pad) or the ground view placed as in T09ProbePin; the camera at -t09TurnDistM from the
    //             vessel, its aim at compass heading -t09TurnHeadingDeg and elevation -t09TurnElevDeg (Aim, once); every variant starts from the flight camera's heading and
    //             pitch that aim gave. At that pose one pinned capture (tag pin-<view>-pose-...) gives the share of the frame covered by scene nearer than 500 m and the
    //             share the clouds cover (cloud.npy alpha above 0.05).
    //   variants: "none" first (the pass with no layer drawn: the baseline of the cloud share and the reference of the poses), then plain, then each -t09PinLimits set.
    //             Per variant: the set applied, the camera at the start pose, the clouds' and the upscaler's (DLSS) history cut there at the host's first jitter phase,
    //             -t09TurnHold frames held (default 144: 3 x 3 rotation's 9 phases at weight 0.2 give a pixel about 16 updates); then -t09Turn frames, each turning the
    //             camera by -t09TurnStepDeg (the heading set in the probe's tick, before the flight camera's LateUpdate: the frame renders at it); at the frames of
    //             -t09TurnCapture (1 = the first turned frame) the screen is copied into a render texture at that frame's end (CaptureScreenshotIntoRenderTexture: a GPU copy,
    //             no read and no file in the turn); then the camera rests -t09TurnHold frames at the end pose (no cut) and the still is copied the same way, with
    //             CaptureScreenshot of the same frame beside it. After that the copies are read back and written as PNGs t09-<start>-turn-<variant>-f<frame>-<w>x<h>.png
    //             and ...-still-..., and the set is reverted.
    //   checks:   consecutive (the turn's ticks were consecutive frames); same_poses (every kept frame's compass heading and elevation within 0.01 degrees and the camera's
    //             height within 0.05 m of the "none" turn's); state (the reconstruction's mode, block, march pass, count targets, scene depth and, where -t09TurnExpect asks,
    //             a march target of the render size, as -t09TurnExpect names them for the variant, recorded in the turn's last frames: values of an earlier variant cannot
    //             pass, the read-outs are cleared per variant); cloud_share (plain's still at least -t09TurnMinCloud of the frame changed by the clouds, else the start
    //             ends before the other variants); still_copy (the still's copy against its CaptureScreenshot: the mean difference of the upright copy below 3 of 255 and
    //             below the flipped one's); pictures (every PNG written).
    internal sealed partial class T09Probe
    {
        // The kept frames' poses of the reference turn ("none"): compass heading, elevation, the camera's height over the terrain.
        private double[] refAz, refEl, refHeight;
        // The baseline pictures (the "none" turn's copies, RGBA bytes upright), for the cloud share of each variant's first kept frame and still.
        private byte[][] turnBaseline;

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

        // -t09TurnExpect name=mode/block/pass/count/scenedepth[/r];... : the reconstruction's state each variant must show (count and scenedepth 0 or 1; r: the march target is the
        // camera's render size). A variant without an entry fails its state check.
        private Dictionary<string, string[]> TurnExpect()
        {
            var map = new Dictionary<string, string[]>();
            string text = CommandLine.After(Args(), "-t09TurnExpect", true);
            foreach (string entry in (text ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = entry.IndexOf('=');
                if (eq > 0) map[entry.Substring(0, eq).Trim()] = entry.Substring(eq + 1).Split('/');
            }
            return map;
        }

        // The pinned turn after PinSettleSteps (the clock, the noise, the maps and the heights stand): the pose capture, then every variant in turn.
        private IEnumerable<ProbeWait> PinTurn(OwnCloudModule own, List<PinLimit> limits, View view)
        {
            int n = TurnFrames, hold = (int)NumberArg("-t09TurnHold", 144.0);
            double stepDeg = NumberArg("-t09TurnStepDeg", 0.6), headingDeg = NumberArg("-t09TurnHeadingDeg", 70.0), elevationDeg = NumberArg("-t09TurnElevDeg", 5.0);
            double distM = NumberArg("-t09TurnDistM", view.CameraDistM), minCloud = NumberArg("-t09TurnMinCloud", 0.2);
            int[] keep = TurnCaptureFrames(n);
            Dictionary<string, string[]> expect = TurnExpect();
            pinLabel = "turn";
            if (!PinStep("turn_args", n >= 2 && keep.Length > 0 && stepDeg > 0.0 && hold >= 1, n + " frames, step " + stepDeg.ToString("0.###", CultureInfo.InvariantCulture) + " degrees a frame, hold "
                                                                                             + hold + " frames, kept frames " + string.Join(",", Array.ConvertAll(keep, k => k.ToString(CultureInfo.InvariantCulture))))) yield break;
            if (!TurnVesselStands()) yield break;
            string prefix = "t09-" + (StartId ?? "nostart") + "-turn-";
            // Old turn pictures of this start: gone, so no check can pass on a stale file.
            try
            {
                if (System.IO.Directory.Exists(SeriesPictures.Folder))
                    foreach (string old in System.IO.Directory.GetFiles(SeriesPictures.Folder, prefix + "*.png")) System.IO.File.Delete(old);
            }
            catch (Exception e) { AtmospheresLog.Say("T09 turn: removing old pictures threw " + e.GetType().Name + " " + e.Message); }

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
            PinEmit("start_camera_agl_m", start.CameraAgl);

            // The view at the start pose: one pinned capture (plain), its scene depth and its clouds.
            pinLabel = "turn-pose";
            lastPinDir = null;
            foreach (ProbeWait w in PinCutHoldCapture(view, "plain", "pose")) yield return w;
            pinLabel = "turn";
            double nearShare = double.NaN, poseCloud = double.NaN;
            try
            {
                if (lastPinDir != null)
                {
                    float[] depth = PinFiles.ReadFloat32(System.IO.Path.Combine(lastPinDir, "depth.npy"));
                    float[] cloud = PinFiles.ReadFloat32(System.IO.Path.Combine(lastPinDir, "cloud.npy"));
                    int near = 0, clouds = 0;
                    for (int i = 0; i < depth.Length; i++) if (depth[i] > 0f && depth[i] < 500f) near++;
                    for (int i = 3; i < cloud.Length; i += 4) if (cloud[i] > 0.05f) clouds++;
                    nearShare = depth.Length > 0 ? (double)near / depth.Length : double.NaN;
                    poseCloud = cloud.Length > 0 ? (double)clouds / (cloud.Length / 4) : double.NaN;
                }
            }
            catch (Exception e) { AtmospheresLog.Say("T09 turn: reading the pose capture threw " + e.GetType().Name + " " + e.Message); }
            PinEmit("pose_near_share", nearShare);
            PinEmit("pose_cloud_share", poseCloud);
            AtmospheresLog.Say("T09 turn: at the start pose " + (100.0 * nearShare).ToString("0.0", CultureInfo.InvariantCulture) + " % of the frame is scene nearer than 500 m, "
                               + (100.0 * poseCloud).ToString("0.0", CultureInfo.InvariantCulture) + " % cloud (the pass's alpha above 0.05); files in " + (lastPinDir ?? "none"));

            refAz = refEl = refHeight = null;
            turnBaseline = null;
            var sets = new List<PinLimit> { new PinLimit { Name = "none" }, null };
            sets.AddRange(limits);
            var restore = new List<KeyValuePair<string, double>>();
            int sw = Game.ScreenWidth, sh = Game.ScreenHeight;
            float stepRad = (float)(stepDeg * Math.PI / 180.0);
            foreach (PinLimit set in sets)
            {
                string name = set == null ? "plain" : set.Name;
                bool baseline = name == "none";
                pinLabel = "turn-" + name;
                if (baseline) Game.SetLayerMask(NoLayersMask);
                else if (set != null) PinLimitApply(set, restore);
                Game.CameraHeading = hdg0;
                Game.CameraPitch = pitch0;
                LayerReconstructor player = own.Driver != null ? own.Driver.Player : null;
                if (player != null) player.ClearReadouts();
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
                bool cut = PinStep("history_cut", own.HistoryCuts > cutsBefore, "cuts " + cutsBefore + " -> " + own.HistoryCuts);
                int cutsAfter = own.HistoryCuts;
                double giveUp = Elapsed + TurnHoldTimeoutS;
                while (cut && own.HistoryFramesSinceCut < hold && own.HistoryCuts == cutsAfter && Elapsed < giveUp) yield return ProbeWait.Frames(1);
                if (!cut || !PinStep("hold", own.HistoryFramesSinceCut >= hold && own.HistoryCuts == cutsAfter, "frames since the cut " + own.HistoryFramesSinceCut + " of " + hold))
                {
                    TurnRevert(baseline, set, restore);
                    yield return ProbeWait.Frames(2);
                    continue;
                }

                // The turn: the heading of frame i is the start's plus i steps; the kept frames are copied on the GPU at their frame's end.
                var targets = new RenderTexture[keep.Length + 1];
                for (int k = 0; k < targets.Length; k++)
                {
                    targets[k] = new RenderTexture(sw, sh, 0, RenderTextureFormat.ARGB32) { name = "T09 turn frame " + k };
                    targets[k].Create();
                }
                var az = new double[keep.Length];
                var el = new double[keep.Length];
                var height = new double[keep.Length];
                var jitter = new Vector4[keep.Length];
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
                        T09Pose p = Game.ReadPose();
                        az[k] = p.AzimuthDeg;
                        el[k] = p.ElevationDeg;
                        height[k] = p.CameraAgl;
                        // The host's jitter of the frame just drawn (render pixels; 0, 0 with the jitter off or without the upscaler).
                        jitter[k] = OwnCloudModule.HostJitterNow();
                    }
                }
                float seconds = Time.realtimeSinceStartup - t0;
                PinEmit("fps", n / Math.Max(1e-3, seconds));
                PinEmit("frame_ms", 1000.0 * seconds / n);
                Emit("T09.turn." + name + ".fps", n / Math.Max(1e-3, seconds));
                PinStep("consecutive", consecutive && lastFrame - firstFrame == n, "the turn's " + n + " ticks were frames " + firstFrame + " to " + lastFrame);
                for (int k = 0; k < keep.Length; k++)
                {
                    string f = keep[k].ToString(CultureInfo.InvariantCulture);
                    PinEmit("az_f" + f, az[k]);
                    PinEmit("el_f" + f, el[k]);
                    PinEmit("camera_agl_m_f" + f, height[k]);
                    PinEmit("jitter_x_f" + f, jitter[k].x);
                    PinEmit("jitter_y_f" + f, jitter[k].y);
                }
                if (baseline)
                {
                    refAz = az;
                    refEl = el;
                    refHeight = height;
                }
                else if (refAz != null)
                {
                    double worstAngle = 0.0, worstHeight = 0.0;
                    for (int k = 0; k < keep.Length; k++)
                    {
                        worstAngle = Math.Max(worstAngle, Math.Max(Math.Abs(WrapDeg(az[k] - refAz[k])), Math.Abs(el[k] - refEl[k])));
                        worstHeight = Math.Max(worstHeight, Math.Abs(height[k] - refHeight[k]));
                    }
                    PinEmit("pose_diff_deg", worstAngle);
                    PinEmit("pose_diff_m", worstHeight);
                    PinStep("same_poses", worstAngle < 0.01 && worstHeight < 0.05, "the kept frames differ from the reference turn's by up to " + worstAngle.ToString("0.0000", CultureInfo.InvariantCulture)
                                                                                  + " degrees (heading, elevation) and " + worstHeight.ToString("0.000", CultureInfo.InvariantCulture) + " m (camera height)");
                }
                else PinStep("same_poses", false, "the reference turn (none) did not run, so there are no poses to compare with");
                TurnState(player, name, expect, lastFrame);

                // The still at the end pose: the history settles, no cut.
                int restFrom = Game.FrameCount;
                giveUp = Elapsed + TurnHoldTimeoutS;
                while (Game.FrameCount - restFrom < hold && Elapsed < giveUp) yield return ProbeWait.Frames(1);
                string stem = prefix + name;
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

                // The copies read back and written, after the turn (the frames now are not measured).
                int written = 0;
                var pictures = new byte[targets.Length][];
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
                        pictures[k] = raw;
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
                // The still's screenshot is written at that frame's end by Unity: wait for the file.
                double fileGiveUp = Elapsed + PinReadTimeoutS;
                while (!System.IO.File.Exists(stillRef) && Elapsed < fileGiveUp) yield return ProbeWait.Frames(1);
                PinEmit("written", written);
                PinStep("pictures", fault == null && written == targets.Length && System.IO.File.Exists(stillRef),
                        fault ?? (written + " of " + targets.Length + " pictures written, the still's screenshot " + (System.IO.File.Exists(stillRef) ? "written" : "missing")));
                StillAgainstScreenshot(pictures[keep.Length], stillRef, sw, sh);

                // The cloud share of the first kept frame and of the still against the reference turn's pictures (no layer drawn).
                if (baseline) turnBaseline = pictures;
                else
                {
                    double first = ChangedShare(pictures[0], turnBaseline != null ? turnBaseline[0] : null);
                    double still = ChangedShare(pictures[keep.Length], turnBaseline != null ? turnBaseline[keep.Length] : null);
                    PinEmit("cloud_share_first", first);
                    PinEmit("cloud_share_still", still);
                    if (name == "plain" && !PinStep("cloud_share", !double.IsNaN(still) && still >= minCloud,
                                                     "the clouds change " + (double.IsNaN(still) ? "an unknown share" : still.ToString("0.000", CultureInfo.InvariantCulture)) + " of plain's still (at least "
                                                     + minCloud.ToString("0.00", CultureInfo.InvariantCulture) + " wanted); the other variants are not run"))
                    {
                        TurnRevert(baseline, set, restore);
                        break;
                    }
                }
                AtmospheresLog.Say("T09 turn " + name + ": " + (n / Math.Max(1e-3, seconds)).ToString("0.0", CultureInfo.InvariantCulture) + " fps over " + n + " frames, kept "
                                   + written + " pictures, end heading " + endAz.ToString("0.00", CultureInfo.InvariantCulture));
                TurnRevert(baseline, set, restore);
                yield return ProbeWait.Frames(2);
            }
            turnBaseline = null;
            pinLabel = "ground";
        }

        // The longest wait for a turn's hold or rest (144 frames at 3 fps are 48 s).
        private const double TurnHoldTimeoutS = 60.0;

        private void TurnRevert(bool baseline, PinLimit set, List<KeyValuePair<string, double>> restore)
        {
            if (baseline) Game.SetLayerMask(0);
            else if (set != null) PinLimitRevert(restore);
        }

        // The reconstruction's state in the turn's last frames against -t09TurnExpect (mode/block/pass/count/scenedepth[/r]).
        private void TurnState(LayerReconstructor player, string name, Dictionary<string, string[]> expect, int lastFrame)
        {
            if (player == null)
            {
                PinStep("state", false, "the player view has no reconstruction");
                return;
            }
            string seen = player.LastMode + "/" + player.LastBlock + "/" + player.LastMarchPass + "/" + (player.LastCountTarget ? 1 : 0) + "/" + (player.LastSceneDepth ? 1 : 0);
            EmitText("T09.pin." + pinLabel + ".recon_mode", player.LastMode.ToString());
            PinEmit("recon_block", player.LastBlock);
            PinEmit("recon_march_pass", player.LastMarchPass);
            PinEmit("recon_count_target", player.LastCountTarget ? 1.0 : 0.0);
            PinEmit("recon_scene_depth", player.LastSceneDepth ? 1.0 : 0.0);
            PinEmit("recon_record_frame", player.LastRecordFrame);
            EmitText("T09.pin." + pinLabel + ".march_target", player.LastMarchWidth + "x" + player.LastMarchHeight + " of " + player.LastRenderWidth + "x" + player.LastRenderHeight);
            string[] want;
            bool recorded = player.LastRecordFrame >= lastFrame - 2;
            bool ok = recorded && expect.TryGetValue(name, out want) && want.Length >= 5 && seen == string.Join("/", want, 0, 5) && (want.Length < 6 || TargetAsWanted(player, want[5]));
            string wanted = expect.TryGetValue(name, out want) ? string.Join("/", want) : "nothing (no -t09TurnExpect entry)";
            PinStep("state", ok, "mode/block/pass/count/scenedepth " + seen + ", march target " + player.LastMarchWidth + "x" + player.LastMarchHeight + " of " + player.LastRenderWidth + "x"
                                 + player.LastRenderHeight + ", recorded in frame " + player.LastRecordFrame + " (the turn ended in " + lastFrame + "); wanted " + wanted);
        }

        // The 6th field of -t09TurnExpect: "r" the march target is the render size; a number s the march target is the render size times s, rounded (the mode Low's
        // Mathf.RoundToInt in LayerReconstructor.Record).
        private static bool TargetAsWanted(LayerReconstructor player, string want)
        {
            if (want == "r") return player.LastMarchWidth == player.LastRenderWidth && player.LastMarchHeight == player.LastRenderHeight;
            float s;
            if (!float.TryParse(want, NumberStyles.Float, CultureInfo.InvariantCulture, out s)) return false;
            return player.LastMarchWidth == Mathf.Max(2, Mathf.RoundToInt(player.LastRenderWidth * s)) && player.LastMarchHeight == Mathf.Max(2, Mathf.RoundToInt(player.LastRenderHeight * s));
        }

        // The vessel stands where the save put it: KSP's own correction at the flight's load ("[<vessel>]: ground contact! - error. Moving Vessel up|down <d>m", KSP.log) moved
        // it at most 1 m (a correction of 7.69 m dropped the pad vessel on 2026-11-20 and broke it apart). No such line: no correction.
        private bool TurnVesselStands()
        {
            double moved = 0.0;
            string line = null;
            try
            {
                string log = System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "KSP.log");
                using (var stream = new System.IO.FileStream(log, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
                using (var reader = new System.IO.StreamReader(stream))
                {
                    string l;
                    while ((l = reader.ReadLine()) != null)
                    {
                        int at = l.IndexOf("Moving Vessel", StringComparison.Ordinal);
                        if (at < 0) continue;
                        line = l.Trim();
                        string rest = l.Substring(at + "Moving Vessel".Length).Replace("up", " ").Replace("down", " ").Replace("m", " ").Trim();
                        double d;
                        if (double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) moved = Math.Max(moved, Math.Abs(d));
                        else moved = double.NaN;
                    }
                }
            }
            catch (Exception e)
            {
                return PinStep("vessel_stands", false, "KSP.log could not be read: " + e.GetType().Name + " " + e.Message);
            }
            PinEmit("vessel_moved_m", moved);
            return PinStep("vessel_stands", !double.IsNaN(moved) && moved <= 1.0, line == null ? "no 'Moving Vessel' line: KSP did not correct the vessel at the load" : line);
        }

        // The still's copy against its CaptureScreenshot of the same frame: the mean channel difference (0..255) of the upright copy and of the copy flipped back.
        private void StillAgainstScreenshot(byte[] still, string screenshotPath, int w, int h)
        {
            double upright = double.NaN, flipped = double.NaN;
            try
            {
                if (still != null && System.IO.File.Exists(screenshotPath))
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (tex.LoadImage(System.IO.File.ReadAllBytes(screenshotPath)) && tex.width == w && tex.height == h)
                    {
                        Color32[] shot = tex.GetPixels32();
                        long a = 0, b = 0;
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                            {
                                Color32 s = shot[y * w + x];
                                int i = (y * w + x) * 4, j = ((h - 1 - y) * w + x) * 4;
                                a += Math.Abs(still[i] - s.r) + Math.Abs(still[i + 1] - s.g) + Math.Abs(still[i + 2] - s.b);
                                b += Math.Abs(still[j] - s.r) + Math.Abs(still[j + 1] - s.g) + Math.Abs(still[j + 2] - s.b);
                            }
                        upright = a / (3.0 * w * h);
                        flipped = b / (3.0 * w * h);
                    }
                    UnityEngine.Object.Destroy(tex);
                }
            }
            catch (Exception e) { AtmospheresLog.Say("T09 turn: comparing the still threw " + e.GetType().Name + " " + e.Message); }
            PinEmit("still_vs_screenshot", upright);
            PinEmit("still_vs_screenshot_flipped", flipped);
            PinStep("still_copy", upright < 3.0 && upright < flipped, "mean channel difference of the still's copy against its screenshot " + upright.ToString("0.00", CultureInfo.InvariantCulture)
                                                                    + " of 255 (flipped " + flipped.ToString("0.00", CultureInfo.InvariantCulture) + ")");
        }

        // The share of the pixels whose largest channel differs by 13 of 255 or more (0.05, the picture share's threshold) between a picture and its baseline; NaN without both.
        private static double ChangedShare(byte[] picture, byte[] baseline)
        {
            if (picture == null || baseline == null || picture.Length != baseline.Length || picture.Length == 0) return double.NaN;
            long changed = 0;
            for (int i = 0; i < picture.Length; i += 4)
            {
                int d = Math.Max(Math.Abs(picture[i] - baseline[i]), Math.Max(Math.Abs(picture[i + 1] - baseline[i + 1]), Math.Abs(picture[i + 2] - baseline[i + 2])));
                if (d >= 13) changed++;
            }
            return changed / (picture.Length / 4.0);
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
