using System;
using System.Collections.Generic;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Series K-T2, the picture noise of the cloud resolution levels (package T09), and the gate before each timing window.
    //
    // Noise: after the pictures of a view (so outside the timing window), for the variants plain and recon2 to recon4 in the views ground and above, the probe reads four raw
    // frames of the held pose: two baseline frames (the pass running with no layer drawn, Frame.LayerMask = NoLayersMask) and two frames with the pass, each pair read on
    // consecutive frames (the second read starts in the tick in which the first was taken, and the read is at the end of its frame: one frame apart; T09.noise.<view>.frames_apart
    // says so). The vessel and the camera are held, so the two frames of a pair show the same picture but for what changes from frame to frame: the pass's own sample pattern
    // (the reconstruction levels march one pixel of every N x N block per frame), the stochastic dither at the cloud's edges, the upscaler's jitter and the sea's glitter.
    // The region is the deck: the pixels where the frame with the pass differs from the baseline of the pose (T09Readout.Noise), taken from the plain pass of the view and used
    // for every later variant of the start (region_fixed 1), so that the levels are read over the same pixels. hf (the spatial grain) is the measure that follows the level; the
    // flicker is read with the second frame scaled to the first one's mean (a change of the whole picture's brightness is not shimmer). Rows, per variant:
    //   T09.noise.<view>.flicker       mean absolute difference of luminance between the two frames in the region, gain matched (the shimmer measure of T05)
    //   T09.noise.<view>.flicker_raw   the same without the scaling; gain_ab is the second frame's mean luminance over the first's
    //   T09.noise.<view>.flicker_rel   flicker over the region's mean luminance
    //   T09.noise.<view>.floor_flicker the same between the two baseline frames over the same region: what shimmers without the pass
    //   T09.noise.<view>.hf            root mean square of the 4-neighbour high-pass of luminance in the region (the spatial grain)
    //   T09.noise.<view>.hf_rel        hf over the mean luminance
    //   T09.noise.<view>.mean_luma, region_fraction, frames_apart, pose_ok
    // tools/weather_metrics/kt2.py divides the flicker and hf of each level by plain's (T09.noise.<view>.flicker_over_plain, hf_over_plain).
    internal sealed partial class T09Probe
    {
        // The frames after a layer-mask change before a frame is read: the reconstruction at level 4 refills its 16-frame history, plus margin.
        private const int NoiseSettleBaselineFrames = 30, NoiseSettleFrames = 40;

        // The vessel stands within the hold's tolerance for a stretch of frames before a timing window starts (T09Hold.Stable: 30 frames within 5 m), so that the first seconds of
        // the window are not those of a vessel that is still settling. At most 300 frames; a hold that never gets there is logged and the window goes on (CheckHeld fails a pose
        // that really left).
        private IEnumerable<ProbeWait> StableGate(View view, string variant)
        {
            int waited = 0;
            while (waited < 300 && Game.HoldReached && !Game.HoldStable)
            {
                yield return ProbeWait.Frames(5);
                waited += 5;
            }
            string prefix = "T09.view." + view.Name + ".";
            bool stable = Game.HoldStable;
            Emit(prefix + "hold_stable", stable);
            Emit(prefix + "hold_gate_frames", waited);
            if (!stable || waited > 0)
                AtmospheresLog.Say("T09 hold gate: " + view.Name + " " + variant + ": " + (stable ? "stable" : "NOT stable") + " after " + waited + " frames (" + Game.HoldStatus + ")");
        }

        private T09Image noiseImage;
        private int noiseBeginFrame;
        // The region of the plain pass per view, taken for every later variant of the start.
        private readonly Dictionary<string, bool[]> noiseRegions = new Dictionary<string, bool[]>();

        // One raw read: begins, waits, takes. noiseImage is null where it failed (fault says why).
        private IEnumerable<ProbeWait> ReadRawFrame(string what)
        {
            noiseImage = null;
            string fault = null;
            bool started = false;
            try { started = Game.CaptureBeginRaw(); }
            catch (Exception e) { fault = "the read threw " + e.GetType().Name + " " + e.Message; }
            noiseBeginFrame = Game.FrameCount;
            if (started)
            {
                double giveUp = Elapsed + T09Pictures.ReadWaitS;
                while (CaptureBusy(ref fault) && Elapsed < giveUp) yield return ProbeWait.Frames(1);
                if (fault == null && CaptureBusy(ref fault)) fault = "the read did not finish in " + T09Pictures.ReadWaitS.ToString("0") + " s";
                if (fault == null)
                {
                    try { noiseImage = Game.CaptureTake(); }
                    catch (Exception e) { fault = "taking the read threw " + e.GetType().Name + " " + e.Message; }
                    if (fault == null && (noiseImage == null || noiseImage.Raw == null || noiseImage.Raw.Length != noiseImage.Width * noiseImage.Height || noiseImage.Raw.Length == 0))
                    {
                        fault = "the read gave no raw picture";
                        noiseImage = null;
                    }
                }
            }
            else if (fault == null) fault = "no read could start (one is still under way)";
            if (fault != null)
            {
                noiseImage = null;
                Warn("noise_read_failed", what + ": " + fault);
            }
        }

        private IEnumerable<ProbeWait> Noise(View view, string variant)
        {
            if (picturesStopped || !viewPoseOk) yield break;
            if (Elapsed > MaxSeconds - PictureReserveS)
            {
                Warn("noise_time", view.Name + " " + variant + ": the scenario is at " + Elapsed.ToString("0") + " s of " + MaxSeconds.ToString("0") + " s; no noise read");
                yield break;
            }
            string what = view.Name + " " + variant;
            string prefix = "T09.noise." + view.Name + ".";
            AtmospheresLog.Say("T09 noise begin: " + what);
            for (int i = 0; i < 12 && Game.HoldReached && !Game.HoldStable; i++) yield return ProbeWait.Frames(5);
            T09Pose before = Game.ReadPose();

            // The baseline pair: the pass running with no layer drawn.
            Game.SetLayerMask(NoLayersMask);
            yield return ProbeWait.Frames(NoiseSettleBaselineFrames);
            foreach (ProbeWait w in ReadRawFrame(what + " baseline 1")) yield return w;
            T09Image base1 = noiseImage;
            foreach (ProbeWait w in ReadRawFrame(what + " baseline 2")) yield return w;
            T09Image base2 = noiseImage;
            // The pass's pair, on consecutive frames.
            Game.SetLayerMask(0);
            yield return ProbeWait.Frames(NoiseSettleFrames);
            foreach (ProbeWait w in ReadRawFrame(what + " frame a")) yield return w;
            T09Image a = noiseImage;
            int beginA = noiseBeginFrame;
            foreach (ProbeWait w in ReadRawFrame(what + " frame b")) yield return w;
            T09Image b = noiseImage;
            int beginB = noiseBeginFrame;
            T09Pose after = Game.ReadPose();

            // A read that failed is a named warning (ReadRawFrame) and no noise rows; it does not count towards the pictures' fault limit.
            if (base1 == null || base2 == null || a == null || b == null) yield break;
            string poseProblem = PoseProblem(view, before) ?? PoseProblem(view, after);
            Emit(prefix + "pose_ok", poseProblem == null);
            if (poseProblem != null)
            {
                Warn("noise_pose", what + ": " + poseProblem + "; no noise rows (the baseline and the pass frames must be the same picture)");
                yield break;
            }
            if (base1.Width != a.Width || base1.Height != a.Height || base2.Width != a.Width || b.Width != a.Width || b.Height != a.Height || base2.Height != a.Height)
            {
                Warn("noise_size", what + ": the four frames differ in size");
                yield break;
            }
            // The levels of a view are read over the plain pass's region (the first variant that reads this view): each with a region of its own, the means are taken over a
            // different mix of flat deck and cloud edges (K-T2 start 46: ground region 0.69 of the picture, above 0.29, and the rows of the levels not monotone).
            bool[] fixedRegion;
            noiseRegions.TryGetValue(view.Name, out fixedRegion);
            if (fixedRegion != null && fixedRegion.Length != a.Width * a.Height) fixedRegion = null;
            T09Readout.NoiseReading n;
            try { n = T09Readout.Noise(base1.Raw, base2.Raw, a.Raw, b.Raw, a.Width, a.Height, fixedRegion); }
            catch (Exception e)
            {
                Warn("noise_readout", what + ": " + e.GetType().Name + " " + e.Message);
                yield break;
            }
            if (fixedRegion == null && n.Count >= T09Readout.NoiseMinPixels) noiseRegions[view.Name] = n.Region;
            Emit(prefix + "region_fixed", fixedRegion != null);
            Emit(prefix + "frames_apart", beginB - beginA);
            Emit(prefix + "region_fraction", n.RegionFraction);
            if (n.Count < T09Readout.NoiseMinPixels)
            {
                Warn("noise_region_small", what + ": " + n.Count + " pixels changed by the pass (at least " + T09Readout.NoiseMinPixels + " needed); no noise rows");
                yield break;
            }
            Emit(prefix + "mean_luma", n.MeanLuma);
            Emit(prefix + "flicker", n.Flicker);
            Emit(prefix + "flicker_rel", n.Flicker / Math.Max(1e-6, n.MeanLuma));
            Emit(prefix + "floor_flicker", n.FloorFlicker);
            Emit(prefix + "flicker_raw", n.FlickerRaw);
            Emit(prefix + "floor_flicker_raw", n.FloorFlickerRaw);
            Emit(prefix + "gain_ab", n.GainAB);
            if (n.HfCount > 0)
            {
                Emit(prefix + "hf", n.Hf);
                Emit(prefix + "hf_rel", n.Hf / Math.Max(1e-6, n.MeanLuma));
            }
            AtmospheresLog.Say("T09 noise end: " + what + ": region " + n.Count + " px" + (fixedRegion != null ? " (the plain pass's)" : "") + ", luma " + n.MeanLuma.ToString("0.000")
                               + ", flicker " + n.Flicker.ToString("0.00000") + " (raw " + n.FlickerRaw.ToString("0.00000") + ", frame gain " + n.GainAB.ToString("0.000") + ", floor "
                               + n.FloorFlicker.ToString("0.00000") + "), hf " + n.Hf.ToString("0.00000") + ", " + (beginB - beginA) + " frame(s) apart");
        }
    }
}
