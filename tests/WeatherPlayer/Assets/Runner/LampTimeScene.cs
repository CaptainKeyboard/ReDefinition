using System;
using System.Collections;
using System.Collections.Generic;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // The cost of the lamp scatter: four lamps at 2560x1440 in snow (sigma 0.03), the GPU milliseconds of the frame through the proxy's
    // timestamp queries with the lamps and without them. Run it under the exclusive lock
    // (python tools/build_weather_player.py --series lamp-time): nothing else may use the GPU.
    //
    // Reference expectation: the lamps add at most 0.5 ms (the brief's budget for 4 lights at 2560x1440, depth copy included). The GPU
    // clock and the frame time drift (a first block of frames ran 0.5 ms slower than a later one in a trial), so the blocks alternate
    // off, on, off, on ... and each "on" block is compared with the mean of the "off" blocks on both sides of it; the spread of those
    // differences is the noise and must stay below 0.15 ms. The positive control reads the added light from the target.
    //
    //   -mode scene -scene lamp-time
    [WeatherScene("lamp-time")]
    internal sealed class LampTimeScene : LampSceneBase
    {
        private const int TimeWidth = 2560, TimeHeight = 1440;
        // The proxy reads the GPU time of a frame up to 6 presents late, so a block settles for 12 frames.
        // 120 frames a block: with 40 the spread of the differences was 0.14 and 0.24 ms in two runs of the same build.
        private const int Settle = 12, BlockFrames = 120, Cycles = 8, ControlCycles = 3;

        protected override void BuildContent()
        {
            MakePass();
            // The scene's own target is 1280x720; the cost is measured at the size of the brief.
            var big = new RenderTexture(TimeWidth, TimeHeight, 24, RenderTextureFormat.DefaultHDR) { name = "lamp time target" };
            big.Create();
            Target = big;
            Cam.targetTexture = big;
            Cam.clearFlags = CameraClearFlags.Skybox;
            Cam.transform.position = LampOrigin + new Vector3(0f, 0f, -14f);
            Cam.transform.rotation = Quaternion.identity;
        }

        private LampDraw[] Four()
        {
            // Four lamps in a row 1.2 m apart, all lighting the view: the worst case for the fill.
            var draws = new LampDraw[4];
            for (int i = 0; i < 4; i++)
            {
                Vector3 at = LampOrigin + new Vector3(-1.8f + 1.2f * i, 0f, 0f);
                draws[i] = Lamp(at, Vector3.forward, 0.03f, 0.03f, 0.35f, 12f);
            }
            return draws;
        }

        // One block: attach (or not), let the frames settle, then the mean GPU milliseconds of BlockFrames frames.
        private IEnumerator Block(FrameTimers timers, LampDraw[] draws, int count, Action<double> done)
        {
            Attach(draws, count, 1f);
            yield return Frames(Settle);
            timers.Clear();
            for (int i = 0; i < BlockFrames; i++)
            {
                yield return Frames(1);
                timers.Sample();
            }
            if (timers.GpuFrames < minGpuFrames) minGpuFrames = timers.GpuFrames;
            done(timers.GpuMean);
        }

        private int minGpuFrames = int.MaxValue;

        // One block with only the depth copy attached (no lamp is drawn): the cost of the copy at the size of the brief, apart from the
        // draws. The copy is a full-size RFloat blit every drawn frame, so it is reported on its own.
        private IEnumerator CopyBlock(FrameTimers timers, int copies, Action<double> done)
        {
            Detach();
            Pass.EnsureDepth(Cam.targetTexture.width, Cam.targetTexture.height);
            DepthBuffer = new CommandBuffer { name = "lamp depth copy only" };
            DrawBuffer = new CommandBuffer { name = "lamp draws none" };
            for (int k = 0; k < copies; k++) Pass.RecordDepthCopy(DepthBuffer);
            Cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, DepthBuffer);
            Cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, DrawBuffer);
            // The copy buffer is on the camera at the event the module uses; a block without it would read as a free copy.
            bool attached = false;
            foreach (CommandBuffer attachedBuffer in Cam.GetCommandBuffers(CameraEvent.BeforeForwardAlpha)) if (attachedBuffer == DepthBuffer) attached = true;
            copyAttached &= attached;
            yield return Frames(Settle);
            timers.Clear();
            for (int i = 0; i < BlockFrames; i++)
            {
                yield return Frames(1);
                timers.Sample();
            }
            if (timers.GpuFrames < minGpuFrames) minGpuFrames = timers.GpuFrames;
            done(timers.GpuMean);
        }

        private bool copyAttached = true;

        // 16 lamps in a 4 x 4 grid 1.2 m apart: four times the load of Four(), for the timer's sensitivity control.
        private LampDraw[] Sixteen()
        {
            var draws = new LampDraw[16];
            for (int i = 0; i < 16; i++)
            {
                Vector3 at = LampOrigin + new Vector3(-1.8f + 1.2f * (i % 4), -1.8f + 1.2f * (i / 4), 0f);
                draws[i] = Lamp(at, Vector3.forward, 0.03f, 0.03f, 0.35f, 12f);
            }
            return draws;
        }

        public override IEnumerator Run()
        {
            var timers = new FrameTimers();
            bool available = timers.StartGpu();
            yield return Frames(30);
            Sink.Flag("timer.available", available && timers.GpuAvailable);

            // off, on, off, on, ... off: Cycles "on" blocks, each between two "off" blocks.
            var off = new List<double>();
            var on = new List<double>();
            double value = double.NaN;
            LampDraw[] lamps = Four();
            yield return Block(timers, null, 0, v => value = v);
            off.Add(value);
            for (int c = 0; c < Cycles; c++)
            {
                yield return Block(timers, lamps, 4, v => value = v);
                on.Add(value);
                yield return Block(timers, null, 0, v => value = v);
                off.Add(value);
            }

            var diffs = new List<double>();
            double sum = 0;
            for (int c = 0; c < Cycles; c++)
            {
                double d = on[c] - 0.5 * (off[c] + off[c + 1]);
                diffs.Add(d);
                sum += d;
            }
            double mean = sum / Cycles, squares = 0;
            foreach (double d in diffs) squares += (d - mean) * (d - mean);
            // The sample standard deviation of the differences, and a 95 % bound on the mean with Student's t for 7 degrees of freedom.
            double spread = Math.Sqrt(squares / (Cycles - 1));
            double offMean = 0, onMean = 0;
            foreach (double v in off) offMean += v / off.Count;
            foreach (double v in on) onMean += v / on.Count;
            Sink.Info("gpu_ms.lamps_off_mean", offMean);
            Sink.Info("gpu_ms.lamps_on_mean", onMean);
            Sink.AtMost("gpu_ms.noise_of_the_differences", spread, 0.15);
            Sink.Info("lamp_ms_2560x1440_4_mean", mean);
            // The verdict holds the upper end of the mean (t(7) = 2.36 standard errors) to the budget; a mean below minus one spread is
            // a fault of the timer, not a cheap pass.
            double upper = mean + 2.36 * spread / Math.Sqrt(Cycles);
            Sink.AtMost("lamp_ms_2560x1440_4", upper, 0.5);
            Sink.AtLeast("lamp_ms_not_negative", mean, -spread);

            // The sensitivity control: 16 lamps (four times the load) between off blocks; the timer must see a cost above its own noise.
            var control = new List<double>();
            var controlOff = new List<double>();
            LampDraw[] many = Sixteen();
            yield return Block(timers, null, 0, v => value = v);
            controlOff.Add(value);
            for (int c = 0; c < ControlCycles; c++)
            {
                yield return Block(timers, many, 16, v => value = v);
                control.Add(value);
                yield return Block(timers, null, 0, v => value = v);
                controlOff.Add(value);
            }
            double controlSum = 0;
            for (int c = 0; c < ControlCycles; c++) controlSum += control[c] - 0.5 * (controlOff[c] + controlOff[c + 1]);
            double controlMean = controlSum / ControlCycles;
            Sink.Info("gpu_ms.control_16_lamps", controlMean);
            Sink.AtLeast("timer.sensitive_to_16_lamps", controlMean, 0.15);
            Sink.AtLeast("timer.sixteen_to_four_ratio", controlMean / Math.Max(mean, 1e-6), 1.5);
            // At least 60 % of the frames of every block carry a GPU time (29 of 40 in one earlier run).
            Sink.AtLeast("gpu_frames_min_per_block", minGpuFrames == int.MaxValue ? 0 : minGpuFrames, 0.6 * BlockFrames);

            // The depth copy alone between off blocks, as the control above: its bound is the share of the 0.5 ms the copy takes.
            // Cycles of off, one copy, off, eight copies (the sensitivity control: eight times the load of the copy, which the timer must
            // see), off.
            var copy = new List<double>();
            var copy8 = new List<double>();
            var copyOff = new List<double>();
            yield return Block(timers, null, 0, v => value = v);
            copyOff.Add(value);
            for (int c = 0; c < Cycles; c++)
            {
                yield return CopyBlock(timers, 1, v => value = v);
                copy.Add(value);
                yield return Block(timers, null, 0, v => value = v);
                copyOff.Add(value);
                yield return CopyBlock(timers, 8, v => value = v);
                copy8.Add(value);
                yield return Block(timers, null, 0, v => value = v);
                copyOff.Add(value);
            }
            var copyDiffs = new List<double>();
            double copySum = 0, copy8Sum = 0;
            for (int c = 0; c < Cycles; c++)
            {
                double baseline1 = 0.5 * (copyOff[2 * c] + copyOff[2 * c + 1]);
                double baseline8 = 0.5 * (copyOff[2 * c + 1] + copyOff[2 * c + 2]);
                copyDiffs.Add(copy[c] - baseline1);
                copySum += copy[c] - baseline1;
                copy8Sum += copy8[c] - baseline8;
            }
            double copyMean = copySum / Cycles, copy8Mean = copy8Sum / Cycles, copySquares = 0;
            foreach (double d in copyDiffs) copySquares += (d - copyMean) * (d - copyMean);
            double copySpread = Math.Sqrt(copySquares / (Cycles - 1));
            double copyUpper = copyMean + 2.36 * copySpread / Math.Sqrt(Cycles);
            Sink.Info("gpu_ms.depth_copy_only", copyMean);
            Sink.Info("gpu_ms.depth_copy_x8", copy8Mean);
            Sink.AtMost("gpu_ms.depth_copy_noise", copySpread, 0.15);
            Sink.Flag("depth_copy_attached", copyAttached);
            Sink.AtLeast("timer.sensitive_to_8_copies", copy8Mean, 0.05);
            Sink.AtLeast("gpu_frames_min_per_block_with_copies", minGpuFrames == int.MaxValue ? 0 : minGpuFrames, 0.6 * BlockFrames);
            Sink.AtMost("depth_copy_ms_2560x1440", copyUpper, 0.3);

            // The positive control: the lamps really draw into the 2560x1440 target (the mean of the added light over the frame), so that
            // a cost near zero is a cheap draw and not a draw that did not happen.
            RgbFrame offFrame = null, onFrame = null;
            Detach();
            yield return Frames(3);
            yield return GrabRgb(f => offFrame = f);
            Attach(lamps, 4, 1f);
            yield return Frames(3);
            yield return GrabRgb(f => onFrame = f);
            double added = RgbFrame.Difference(onFrame, offFrame).Mean(new ImageStats.Rect2(0, 0, onFrame.Width, onFrame.Height), 3);
            Sink.Info("added_mean_luma", added);
            Sink.AtLeast("lamps_draw_into_the_target", added, 0.002);
            Detach();
        }
    }
}
