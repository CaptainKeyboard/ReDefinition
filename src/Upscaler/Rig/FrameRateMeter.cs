namespace ReDefinition.Upscaler
{
    // Running mean of the frame time. The first frames after a switch -- shader
    // compilation, resources -- are left out by a warmup.
    internal class FrameRateMeter
    {
        // Frames left out after a reset before measuring starts.
        private const int WarmupFrames = 30;
        // Frames measured before the mean counts.
        private const int MinimumSamples = 30;

        // The summed frame times in seconds, the frames in that sum, and the warmup frames skipped so far.
        private double accumulated;
        private int samples;
        private int warmup;

        // Whether enough frames are measured for the mean to be shown.
        public bool HasData { get { return samples >= MinimumSamples; } }
        // Frames per second over the measured frames; 0 before the first.
        public float AverageFps { get { return samples > 0 ? (float)(samples / accumulated) : 0f; } }

        // Adds one frame's time in seconds, once the warmup is past; non-positive times are ignored.
        public void Sample(float deltaTime)
        {
            if (deltaTime <= 0f) return;
            if (warmup < WarmupFrames) { warmup++; return; }

            accumulated += deltaTime;
            samples++;

            // Halve the sums after roughly ten seconds so the value follows the
            // current scene instead of freezing an old average.
            if (accumulated > 10.0)
            {
                accumulated *= 0.5;
                samples /= 2;
            }
        }

        // Forgets all frames and starts the warmup again, as after a switch.
        public void Reset()
        {
            accumulated = 0.0;
            samples = 0;
            warmup = 0;
        }

        // The mean as text with one decimal, "--" while it has too few frames.
        public string Describe()
        {
            return HasData ? AverageFps.ToString("0.0") : "--";
        }
    }
}
