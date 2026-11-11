using System;

namespace ReDefinition.Atmospheres.Audio
{
    // Values a series probe sets instead of the game's, so that a scenario (calm, blizzard, a cell at 12 km, a warp, a pause) is exact. All null in
    // play; the module reads them first. Nothing here is set outside a probe.
    internal static class AudioOverrides
    {
        internal static WeatherAudioInputs? Inputs;
        internal static ListenerState? Listener;
        internal static bool? Paused;
        internal static bool? OnRailsWarp;
        internal static AudioSwitches? Switches;
        // Footsteps: the probe feeds a walk of a given length at a speed over a cover instead of a kerbal.
        internal static float? ForcedCover;
        // A simulated walker: the speed over the ground in m/s while Walking is true (grounded, no ladder). The module integrates the distance
        // from it and plays the steps as it does for a kerbal.
        internal static bool Walking;
        internal static float WalkSpeedMs;
        // The game's volume sliders: a probe sets them to full so that the absolute rows do not depend on the player's settings.
        internal static GameVolumes? Volumes;

        internal static void Clear()
        {
            Volumes = null;
            Walking = false;
            WalkSpeedMs = 0f;
            Inputs = null;
            Listener = null;
            Paused = null;
            OnRailsWarp = null;
            Switches = null;
            ForcedCover = null;
        }
    }
}
