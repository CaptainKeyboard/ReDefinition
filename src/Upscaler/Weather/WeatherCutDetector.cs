using System;

namespace ReDefinition.Upscaler
{
    // Finds the frames at which the weather jumps instead of moving: the precipitation level changes by more than
    // CutLevelStep in one frame, or the body the weather belongs to changes. Such a frame drops the temporal history
    // (Frame.RequestHistoryReset("weather cut")): flakes, veil and fog of the old state would otherwise smear into the new one.
    // Pure state; the caller passes what WeatherHooks.View holds.
    internal sealed class WeatherCutDetector
    {
        // A level step in one frame beyond this is a cut (a front passing is slower than 0.3 per frame by far).
        internal const float CutLevelStep = 0.3f;

        // The reason handed to Frame.RequestHistoryReset.
        internal const string Reason = "weather cut";

        private bool seen;
        private float lastLevel;
        private string lastBody = "";

        // The weather of this frame; true where it is a cut from the last frame seen. An inactive view forgets the state, so the
        // first active frame after it is no cut by itself (the camera cuts cover loads and teleports).
        internal bool Observe(bool active, string body, float level)
        {
            if (!active || float.IsNaN(level))
            {
                seen = false;
                return false;
            }
            string name = body ?? "";
            bool cut = seen && (!string.Equals(name, lastBody, StringComparison.Ordinal)
                                || Math.Abs(level - lastLevel) > CutLevelStep);
            seen = true;
            lastLevel = level;
            lastBody = name;
            return cut;
        }
    }
}
