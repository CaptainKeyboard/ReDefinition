namespace ReDefinition.Atmospheres
{
    // Decides when a lookup that may not work yet (another mod that loads later) is tried again: at most every interval, at most the
    // given number of times, then never. Time is passed in, so that it is tested outside the game.
    internal sealed class RetryGate
    {
        private readonly float interval;
        private readonly int maxTries;
        private int tries;
        private float next;

        internal RetryGate(float intervalSeconds, int maxTries)
        {
            interval = intervalSeconds;
            this.maxTries = maxTries;
        }

        // The tries used so far.
        internal int Tries
        {
            get { return tries; }
        }

        // Whether all tries are used.
        internal bool GaveUp
        {
            get { return tries >= maxTries; }
        }

        // Whether to try now; true counts as a try and starts the wait for the next one.
        internal bool Due(float now)
        {
            if (tries >= maxTries || now < next) return false;
            tries++;
            next = now + interval;
            return true;
        }
    }
}
