namespace ReDefinition.Atmospheres
{
    // A path that fails several frames in a row stands down for a while and then tries again, instead of switching off for the session:
    // a failure that belongs to a scene (the main menu, a load, a vessel switch) ends with the scene. A frame that works clears the count.
    internal sealed class StandDown
    {
        private readonly int limit;
        private readonly float pauseSeconds;
        private int failures;
        private float left;

        internal StandDown(int limit, float pauseSeconds)
        {
            this.limit = limit;
            this.pauseSeconds = pauseSeconds;
        }

        // The failures in a row so far.
        internal int Failures
        {
            get { return failures; }
        }

        // Whether the path is standing down.
        internal bool Paused
        {
            get { return left > 0f; }
        }

        // Time passes by dt seconds (call it every frame, also while paused).
        internal void Elapse(float dt)
        {
            if (left > 0f) left -= dt;
        }

        internal void Succeeded()
        {
            failures = 0;
        }

        // Counts a failure; true when this one starts a pause (the count then starts again).
        internal bool Failed()
        {
            failures++;
            if (failures < limit) return false;
            failures = 0;
            left = pauseSeconds;
            return true;
        }
    }
}
