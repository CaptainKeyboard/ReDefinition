namespace ReDefinition.Seasons
{
    // Counts calls that fail one after the other. A path is switched off after that many failures in a row. A call that works
    // starts the count again, so one bad quad does not end a path that works for the others.
    internal sealed class FailureStreak
    {
        private readonly int limit;
        private int failures;

        internal FailureStreak(int limit)
        {
            this.limit = limit;
        }

        internal int Failures
        {
            get { return failures; }
        }

        // Whether the limit is reached.
        internal bool Disabled
        {
            get { return failures >= limit; }
        }

        internal void Succeeded()
        {
            failures = 0;
        }

        // Counts a failure; true when this one reached the limit.
        internal bool Failed()
        {
            failures++;
            return failures == limit;
        }
    }
}
