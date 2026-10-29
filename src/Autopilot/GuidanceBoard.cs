using UnityEngine;

namespace ReDefinition.Autopilot
{
    // What the autonavigation tells the player to do now, in the flight view: where to point, how much throttle to give, and a line that says it.
    // The autonavigation writes it in every frame of a climb or a descent; the navball's marker and the round gauge read it. It is stale
    // when nothing wrote it for a moment, or for another vessel.
    internal static class GuidanceBoard
    {
        private const float Fresh = 0.5f;

        private static float written = -100f;
        private static System.Guid vessel;

        // The unit vector in the world to point the nose along, where there is one to ask for.
        internal static Vector3d Direction { get; private set; }
        internal static bool HasDirection { get; private set; }
        // The throttle to give, 0 to 1, where there is one to ask for.
        internal static double Throttle { get; private set; }
        internal static bool HasThrottle { get; private set; }
        internal static string Line { get; private set; }
        // The bar on the round gauge: how full its arc is, the text on it and its colour. The mark is in the middle: over it the throttle is to go
        // up, under it down.
        internal static double BarFill { get; private set; }
        internal static string BarText { get; private set; }
        internal static int BarLevel { get; private set; }
        internal static bool HasBar { get; private set; }

        internal static void Write(Vessel target, string line, Vector3d? direction, double? throttle, double? barFill = null, string barText = null, int barLevel = 0)
        {
            HasBar = barFill.HasValue;
            if (HasBar)
            {
                BarFill = barFill.Value;
                BarText = barText ?? "";
                BarLevel = barLevel;
            }
            written = Time.realtimeSinceStartup;
            vessel = target.id;
            Line = line;
            HasDirection = direction.HasValue;
            if (direction.HasValue) Direction = direction.Value.normalized;
            HasThrottle = throttle.HasValue;
            if (throttle.HasValue) Throttle = throttle.Value;
        }

        // Whether the board was written for this vessel a moment ago.
        internal static bool Current(Vessel target)
        {
            return target != null && target.id == vessel && Time.realtimeSinceStartup - written < Fresh;
        }
    }
}
