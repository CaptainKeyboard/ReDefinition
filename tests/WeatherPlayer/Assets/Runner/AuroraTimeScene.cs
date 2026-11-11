using System.Collections;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // Times the aurora shell (package D38): the GPU milliseconds of a frame with and without the shell at a strong aurora (activity 1,
    // sun -25) in the view that carries most of it (latitude 70, 25 degrees up), through the proxy's timestamp queries. A timing run:
    // the series takes the exclusive lock. The player draws 1280 x 720; the judged figure is four times the measured difference (the
    // pixel count of 2560 x 1440), because the shell's cost is per pixel of the dome.
    //
    //   -mode scene -scene aurora-time
    [WeatherScene("aurora-time")]
    public sealed class AuroraTimeScene : AuroraSceneBase
    {
        private const int Warm = 40, Measured = 300;
        private const double LimitMs = 0.15;

        public override IEnumerator Run()
        {
            AuroraLook look = AuroraLook.Defaults();
            AuroraFrame frame = Forced(1.0f, -25f);
            SetSun(-25f);
            // The oval of a full activity lies south of latitude 70; the view faces it and reaches 70 degrees up, where it is.
            Look(YawFor(frame, 70f), 40f);
            // The timed view must carry the aurora: the picture with the shell is brighter than without.
            Shell(false, frame, 70f, true, look);
            yield return Frames(2);
            LumaFrame without = null, with = null;
            yield return Grab(r => without = r);
            Shell(true, frame, 70f, true, look);
            yield return Frames(2);
            yield return Grab(r => with = r);
            double lit = ImageStats.MeanLuma(with, ImageStats.Rect2.Whole(with)) - ImageStats.MeanLuma(without, ImageStats.Rect2.Whole(without));
            Sink.Info("timed_view_mean_gain", lit);
            Sink.AtLeast("timed_view_is_lit", lit, 0.002);
            var timers = new FrameTimers();
            bool gpu = timers.StartGpu();
            Sink.Flag("gpu_timer_available", gpu);
            double[] off = new double[3], on = new double[3];
            // Three alternations, so that a drift of the clocks shows.
            for (int round = 0; round < 3; round++)
            {
                Shell(false, frame, 70f, true, look);
                yield return Frames(Warm);
                timers.Clear();
                for (int i = 0; i < Measured; i++)
                {
                    yield return Frames(1);
                    timers.Sample();
                }
                off[round] = timers.GpuMean;
                Shell(true, frame, 70f, true, look);
                yield return Frames(Warm);
                timers.Clear();
                for (int i = 0; i < Measured; i++)
                {
                    yield return Frames(1);
                    timers.Sample();
                }
                on[round] = timers.GpuMean;
            }
            double offMean = (off[0] + off[1] + off[2]) / 3.0, onMean = (on[0] + on[1] + on[2]) / 3.0;
            Sink.Info("gpu_ms_off_720p", offMean);
            Sink.Info("gpu_ms_on_720p", onMean);
            double shell720 = onMean - offMean;
            Sink.Info("shell_gpu_ms_720p", shell720);
            // At most the limit at 2560 x 1440 (4x the pixels of this player).
            Sink.AtMost("shell_gpu_ms_1440p_equivalent", 4.0 * shell720, LimitMs);
        }
    }
}
