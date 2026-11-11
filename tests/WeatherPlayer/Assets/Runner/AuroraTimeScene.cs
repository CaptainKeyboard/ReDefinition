using System;
using System.Collections;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // Times the aurora shell (package D38): the GPU milliseconds of a frame with and without the shell at a dear pose (activity 1,
    // latitude 78, 10 degrees up, sun -12: the longest paths through the layer, the most steps) through the proxy's timestamp queries. A timing run: the series takes the exclusive lock. The player draws 1280 x 720; the
    // judged figure is four times the measured difference (the pixel count of 2560 x 1440), because the shell's cost is per pixel of
    // the dome. The clocks of the GPU drift, so the run alternates off, on and a second off five times and reports the difference of
    // the two off means as the noise of the method; a shell figure that is smaller than that noise is "below the noise", not zero.
    //
    //   -mode scene -scene aurora-time
    [WeatherScene("aurora-time")]
    public sealed class AuroraTimeScene : AuroraSceneBase
    {
        // Short segments in many rounds, so that the drift of the GPU clock cancels between the neighbours.
        private const int Warm = 20, Measured = 120, Rounds = 15;
        private const double LimitMs = 0.15;
        private const float Latitude = 78f;

        public override IEnumerator Run()
        {
            AuroraLook look = AuroraLook.Defaults();
            AuroraFrame frame = Forced(1.0f, -12f);
            SetSun(-12f);
            // Latitude 78 sees the oval bright (the strong-event pose at 60 is dim in the player); the view faces it low, where the paths
            // are longest. The march runs for every pixel the shell is not discarded on, whatever its brightness.
            Look(YawFor(frame, Latitude), 10f);
            // The timed view must carry the aurora: the picture with the shell is brighter than without.
            Shell(false, frame, Latitude, true, look);
            yield return Frames(2);
            LumaFrame without = null, with = null;
            yield return Grab(r => without = r);
            Shell(true, frame, Latitude, true, look);
            yield return Frames(2);
            yield return Grab(r => with = r);
            double lit = ImageStats.MeanLuma(with, ImageStats.Rect2.Whole(with)) - ImageStats.MeanLuma(without, ImageStats.Rect2.Whole(without));
            Sink.Info("timed_view_mean_gain", lit);
            Sink.AtLeast("timed_view_is_lit", lit, 0.002);
            var timers = new FrameTimers();
            bool gpu = timers.StartGpu();
            Sink.Flag("gpu_timer_available", gpu);
            double offSum = 0, offAgainSum = 0, onSum = 0;
            for (int round = 0; round < Rounds; round++)
            {
                Shell(false, frame, Latitude, true, look);
                yield return Frames(Warm);
                timers.Clear();
                for (int i = 0; i < Measured; i++)
                {
                    yield return Frames(1);
                    timers.Sample();
                }
                offSum += timers.GpuMean;
                Shell(true, frame, Latitude, true, look);
                yield return Frames(Warm);
                timers.Clear();
                for (int i = 0; i < Measured; i++)
                {
                    yield return Frames(1);
                    timers.Sample();
                }
                onSum += timers.GpuMean;
                Shell(false, frame, Latitude, true, look);
                yield return Frames(Warm);
                timers.Clear();
                for (int i = 0; i < Measured; i++)
                {
                    yield return Frames(1);
                    timers.Sample();
                }
                offAgainSum += timers.GpuMean;
            }
            double offMean = offSum / Rounds, onMean = onSum / Rounds, offAgainMean = offAgainSum / Rounds;
            Sink.Info("gpu_ms_off_720p", offMean);
            Sink.Info("gpu_ms_on_720p", onMean);
            Sink.Info("gpu_ms_off_again_720p", offAgainMean);
            // The shell against the mean of the two off runs around it (a drift of the clock cancels to first order).
            double shell720 = onMean - 0.5 * (offMean + offAgainMean);
            double noise720 = Math.Abs(offMean - offAgainMean);
            Sink.Info("shell_gpu_ms_720p", shell720);
            Sink.Info("noise_off_off_ms_720p", noise720);
            // The noise at 1440p scale; a shell figure below it is "below the noise".
            Sink.Info("noise_off_off_ms_1440p_equivalent", 4.0 * noise720);
            Sink.Flag("shell_below_noise", Math.Abs(shell720) <= noise720);
            // At most the limit at 2560 x 1440 (4x the pixels of this player); a negative figure counts as zero.
            Sink.AtMost("shell_gpu_ms_1440p_equivalent", Math.Max(0.0, 4.0 * shell720), LimitMs);
            // The method must be able to see the limit: its noise at 1440p scale is under the limit.
            Sink.AtMost("timing_noise_ms_1440p_equivalent", 4.0 * noise720, LimitMs);
        }
    }
}
