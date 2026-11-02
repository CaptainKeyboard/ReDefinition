using System;
using UnityEngine;

namespace ReDefinition.Autopilot.Guidance
{
    // What the navigation tells the player to do now, in the flight view: where to point, how much throttle to give, and a line that says it.
    // The navigation writes it in every frame of a climb or a descent; the navball's marker and the round gauge read it. It is stale
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
        // How far the speed is from the one asked for, in m/s: over it where positive, short of it where negative. Only where the navigation has a speed
        // to ask for. Need is the thrust that asks for, from -1 for as much less to 1 for as much more as the speed may be off by. It is not worked out
        // from the gap now but from where the gap will be in a few seconds, by the way it is changing, so that the player sees the speed coming.
        internal static double SpeedGap { get; private set; }
        // The gap as it will be in the time looked ahead: what the bar stands for, so that the number and the bar say the same.
        internal static double Ahead { get; private set; }
        internal static double Need { get; private set; }
        internal static bool HasGap { get; private set; }

        // How far ahead the gap is looked at, in game seconds, and how long the way the gap changes is averaged over.
        private const double Horizon = 4.0;
        private const double TrendTime = 0.6;
        private static double slope, lastGap, lastTime;
        private static bool hadGap;
        private static System.Guid trended;

        internal static void Write(Vessel target, string line, Vector3d? direction, double? throttle, double? barFill = null, string barText = null, int barLevel = 0,
            double? speedGap = null, double span = 1.0, bool moreWhenOver = false, double? mostNeed = null)
        {
            HasGap = speedGap.HasValue && !double.IsNaN(speedGap.Value) && !double.IsInfinity(speedGap.Value) && !double.IsNaN(span) && !double.IsInfinity(span);
            if (HasGap)
            {
                // The game's time and the speed move in the physics steps only: a frame between two of them leaves the trend as it is.
                double now = Planetarium.GetUniversalTime();
                double dt = now - lastTime;
                if (!hadGap || target.id != trended || dt < 0.0 || dt > 0.5)
                {
                    slope = 0.0;
                    lastGap = speedGap.Value;
                    lastTime = now;
                }
                else if (dt > 1e-4)
                {
                    slope += ((speedGap.Value - lastGap) / dt - slope) * (1.0 - Math.Exp(-dt / TrendTime));
                    lastGap = speedGap.Value;
                    lastTime = now;
                }
                hadGap = true;
                trended = target.id;
                SpeedGap = speedGap.Value;
                Ahead = speedGap.Value + slope * Horizon;
                double ahead = Ahead / Math.Max(1.0, span);
                Need = Math.Max(-1.0, Math.Min(1.0, moreWhenOver ? ahead : -ahead));
                if (mostNeed.HasValue) Need = Math.Min(Need, mostNeed.Value);
            }
            else hadGap = false;
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
