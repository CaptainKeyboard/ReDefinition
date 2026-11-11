using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // The cost of the cover shells (package D34, the L2 spike): 200 dark box parts, each with a shell, seen from above at 2560x1440, the
    // GPU milliseconds of the frame through the proxy's timestamp queries with 0, 100 and 200 shells drawn. Run it under the exclusive
    // lock (python tools/build_weather_player.py --series shell-time).
    //
    // Reference expectation: 100 shells add at most 0.3 ms and 200 shells at most 0.6 ms (the brief's budget, the upper 95 % bound of
    // the mean difference). The blocks alternate off, 100, off, 200, off ... and each "on" block is compared with the mean of the "off"
    // blocks on both sides, as the lamp's timing; the spread of the differences must stay under 0.15 ms. The positive control reads
    // the cover from the target: a cost near zero is a cheap draw, not a draw that did not happen. The scene is static: the game's
    // motion-vector pass also draws the shells of a moving vessel, which this figure does not include; the shell casts no shadow.
    //
    //   -mode scene -scene shell-time
    [WeatherScene("shell-time")]
    internal sealed class ShellTimeScene : ShellSceneBase
    {
        private const int TimeWidth = 2560, TimeHeight = 1440;
        private const int Settle = 12, BlockFrames = 120, Cycles = 8;

        protected override void BuildContent()
        {
            MakeMaterials();
            var big = new RenderTexture(TimeWidth, TimeHeight, 24, RenderTextureFormat.DefaultHDR) { name = "shell time target" };
            big.Create();
            Target = big;
            Cam.targetTexture = big;
            Cam.clearFlags = CameraClearFlags.Skybox;
            Mesh box = BoxMesh(new Vector3(4f, 1f, 2f));
            var root = new GameObject("Parts");
            for (int i = 0; i < 200; i++)
            {
                Vector3 at = Stand + new Vector3((i % 20 - 9.5f) * 6f, 0f, (i / 20 - 4.5f) * 4f);
                AddPart(box, at, Quaternion.identity, root.transform, true);
            }
            Cam.transform.position = Stand + new Vector3(0f, 55f, -45f);
            Cam.transform.rotation = Quaternion.LookRotation(Stand - Cam.transform.position, Vector3.up);
            ShellsOn(false);
        }

        // The first `count` shells drawn, the others not.
        private void Draw(int count)
        {
            for (int i = 0; i < Shells.Count; i++) Shells[i].GetComponent<MeshRenderer>().enabled = i < count;
        }

        private IEnumerator Block(FrameTimers timers, int count, Action<double> done)
        {
            Draw(count);
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

        public override IEnumerator Run()
        {
            var timers = new FrameTimers();
            bool available = timers.StartGpu();
            yield return Frames(30);
            Sink.Flag("timer.available", available && timers.GpuAvailable);

            // off, 100, off, 200, off, ... : Cycles of each.
            var off = new List<double>();
            var a = new List<double>();
            var b = new List<double>();
            double value = double.NaN;
            yield return Block(timers, 0, v => value = v);
            off.Add(value);
            for (int c = 0; c < Cycles; c++)
            {
                yield return Block(timers, 100, v => value = v);
                a.Add(value);
                yield return Block(timers, 0, v => value = v);
                off.Add(value);
                yield return Block(timers, 200, v => value = v);
                b.Add(value);
                yield return Block(timers, 0, v => value = v);
                off.Add(value);
            }
            var diffA = new List<double>();
            var diffB = new List<double>();
            for (int c = 0; c < Cycles; c++)
            {
                diffA.Add(a[c] - 0.5 * (off[2 * c] + off[2 * c + 1]));
                diffB.Add(b[c] - 0.5 * (off[2 * c + 1] + off[2 * c + 2]));
            }
            double meanA = Mean(diffA), meanB = Mean(diffB), spreadA = Spread(diffA, meanA), spreadB = Spread(diffB, meanB);
            double offMean = Mean(off);
            Sink.Info("gpu_ms.shells_off_mean", offMean);
            Sink.Info("gpu_ms.shells_100_mean", Mean(a));
            Sink.Info("gpu_ms.shells_200_mean", Mean(b));
            Sink.AtMost("gpu_ms.noise_100", spreadA, 0.15);
            Sink.AtMost("gpu_ms.noise_200", spreadB, 0.15);
            Sink.Info("shell_ms_100_mean", meanA);
            Sink.Info("shell_ms_200_mean", meanB);
            // The upper end of the mean (t(7) = 2.36 standard errors) is held to the budget; a mean under minus one spread is a fault of
            // the timer, not a cheap pass.
            Sink.AtMost("shell_ms_per_100_parts", meanA + 2.36 * spreadA / Math.Sqrt(Cycles), 0.3);
            Sink.AtMost("shell_ms_200_parts", meanB + 2.36 * spreadB / Math.Sqrt(Cycles), 0.6);
            Sink.AtLeast("shell_ms_100_not_negative", meanA, -spreadA);
            Sink.AtLeast("shell_ms_200_not_negative", meanB, -spreadB);
            Sink.AtLeast("gpu_frames_min_per_block", minGpuFrames == int.MaxValue ? 0 : minGpuFrames, 0.6 * BlockFrames);

            // The positive control: the shells really draw into the target.
            RgbFrame offFrame = null, onFrame = null;
            Draw(0);
            yield return Frames(3);
            yield return GrabRgb(f => offFrame = f);
            Draw(200);
            yield return Frames(3);
            yield return GrabRgb(f => onFrame = f);
            double added = RgbFrame.Difference(onFrame, offFrame).Mean(new ImageStats.Rect2(0, 0, onFrame.Width, onFrame.Height), 3);
            Sink.Info("added_mean_luma", added);
            Sink.AtLeast("shells_draw_into_the_target", added, 0.005);
        }

        private static double Mean(List<double> v)
        {
            double s = 0;
            foreach (double x in v) s += x;
            return v.Count == 0 ? double.NaN : s / v.Count;
        }

        private static double Spread(List<double> v, double mean)
        {
            double s = 0;
            foreach (double x in v) s += (x - mean) * (x - mean);
            return v.Count < 2 ? double.NaN : Math.Sqrt(s / (v.Count - 1));
        }
    }
}
