namespace ReDefinition.Upscaler
{
    // Running mean of the frame time. The first frames after a switch -- shader
    // compilation, resources -- are left out by a warmup.
    internal class FrameRateMeter
    {
        private const int WarmupFrames = 30;
        // Frames measured before the mean counts.
        private const int MinimumSamples = 30;

        private double accumulated;
        private int samples;
        private int warmup;

        public bool HasData { get { return samples >= MinimumSamples; } }
        public float AverageFps { get { return samples > 0 ? (float)(samples / accumulated) : 0f; } }

        public void Sample(float deltaTime)
        {
            if (deltaTime <= 0f) return;
            if (warmup < WarmupFrames) { warmup++; return; }

            accumulated += deltaTime;
            samples++;

            // Start over after roughly ten seconds so the value follows the
            // current scene instead of freezing an old average.
            if (accumulated > 10.0)
            {
                accumulated *= 0.5;
                samples /= 2;
            }
        }

        public void Reset()
        {
            accumulated = 0.0;
            samples = 0;
            warmup = 0;
        }

        public string Describe()
        {
            return HasData ? AverageFps.ToString("0.0") : "--";
        }
    }
}
