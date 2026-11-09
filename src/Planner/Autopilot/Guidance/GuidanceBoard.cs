using System;
using UnityEngine;

namespace ReDefinition.Autopilot.Guidance
{
    // What a step of the guidance writes on the board in a frame. Fields left empty are not asked for.
    internal struct GuidanceFrame
    {
        public string Line;
        // The unit vector in the world to point the nose along.
        public Vector3d? Direction;
        // The throttle to give, 0 to 1: what a pilot is to set.
        public double? Throttle;
        // The throttle the gauges compare with the throttle given. Empty where the gauges have nothing to show.
        public double? IdealThrottle;
        // Whether the ideal throttle is smoothed on the board: a climb's limits change quickly; a descent's throttle is eased by its guidance already.
        public bool EaseIdeal;
        // The throttle the pilot gives now.
        public double Given;
        // The burn that the fall of the throttle covers, in m/s: the scale on which the need is told as a speed.
        public double Span;
        // The size of the burn of the guide node, in m/s.
        public double Remaining;
        // The height of the orbit a climb aims for, in m. Empty for a guidance that is not a climb.
        public double? ClimbAltitude;
        // Whether the guidance has done what it was for.
        public bool Done;
    }

    // What the navigation tells the player to do now, in the flight view: where to point, how much throttle to give, and a line that says it.
    // The navigation writes it in every frame of a climb or a descent; the navball's marker, the gauges and the guide node read it. It is stale
    // when nothing wrote it for a moment, or for another vessel.
    internal static class GuidanceBoard
    {
        private const float Fresh = 0.5f;
        // How far the throttle given may be from the ideal, as a share of the whole range, for the bar to count as on target, and for it to be at its
        // end. Both gauges colour and mark by them.
        internal const double OnTarget = 0.06;
        internal const double FullColour = 0.6;
        // How quickly an ideal throttle that changes fast is followed, and how quickly the mark slides off the gauges, in s.
        private const double IdealEase = 0.6;
        private const double SlideEase = 0.5;

        private static float written = -100f;
        private static Guid vessel;
        private static double idealTime;
        private static Guid idealVessel;
        private static float markTime;

        internal static string Line { get; private set; }
        internal static Vector3d Direction { get; private set; }
        internal static bool HasDirection { get; private set; }
        internal static double Throttle { get; private set; }
        internal static bool HasThrottle { get; private set; }
        internal static double Remaining { get; private set; }
        internal static double ClimbAltitude { get; private set; }
        internal static bool HasClimb { get; private set; }

        // Whether the gauges have a bar to show: the ideal throttle against the throttle given.
        internal static bool HasBar { get; private set; }
        // The ideal throttle, 0 to 1, and the need: from -1 for as much less to 1 for as much more throttle as there is range.
        internal static double Ideal { get; private set; }
        internal static double Need { get; private set; }
        // The burn that the fall of the throttle covers, in m/s.
        internal static double Span { get; private set; }
        // Whether the guidance has done what it was for. The mark and the bar then slide off the gauges, and the game's own burn display takes over.
        internal static bool Done { get; private set; }
        // Where the mark stands on the gauges, from a half to nothing.
        internal static double MarkAt { get; private set; } = 0.5;

        // The bar as the gauges show it, 0 to 1 with the mark at a half: under the mark where thrust is to be given, over it where less is wanted.
        internal static double Arc
        {
            get { return ArcOf(Need, Done); }
        }

        internal static double ArcOf(double need, bool done)
        {
            return done ? 0.0 : 0.5 - 0.5 * need;
        }

        // The need: the ideal throttle less the one given, from -1 to 1.
        internal static double NeedOf(double ideal, double given)
        {
            return Math.Max(-1.0, Math.Min(1.0, ideal - given));
        }

        // How far off the bar is: 0 on target, 2 at the full colour, 1 between.
        internal static int Level
        {
            get { return LevelOf(Need, Done); }
        }

        internal static int LevelOf(double need, bool done)
        {
            double off = Math.Abs(need);
            return done || off < OnTarget ? 0 : off >= FullColour ? 2 : 1;
        }

        // The need as a speed, in m/s: over the one asked for where positive, short of it where negative. It says the same as the bar, by its sign.
        internal static string NumberText
        {
            get { return HasBar ? NumberTextOf(Need, Span) : ""; }
        }

        internal static string NumberTextOf(double need, double span)
        {
            if (!(span > 0.0)) return "";
            double gap = -need * span;
            if (Math.Abs(gap) < 1.0) return "0 m/s";
            return Math.Abs(gap).ToString("0") + (gap > 0.0 ? " m/s over" : " m/s short");
        }

        internal static void Publish(Vessel target, GuidanceFrame frame)
        {
            written = Time.realtimeSinceStartup;
            vessel = target.id;
            Line = frame.Line;
            HasDirection = frame.Direction.HasValue;
            if (HasDirection) Direction = frame.Direction.Value.normalized;
            HasThrottle = frame.Throttle.HasValue;
            if (HasThrottle) Throttle = frame.Throttle.Value;
            Remaining = double.IsNaN(frame.Remaining) || double.IsInfinity(frame.Remaining) ? 0.0 : Math.Max(0.0, frame.Remaining);
            HasClimb = frame.ClimbAltitude.HasValue;
            ClimbAltitude = HasClimb ? frame.ClimbAltitude.Value : 0.0;
            Done = frame.Done;
            Slide(frame.Done);
            HasBar = frame.IdealThrottle.HasValue && !double.IsNaN(frame.IdealThrottle.Value);
            if (!HasBar) return;
            Ease(target, frame.IdealThrottle.Value, frame.EaseIdeal);
            Need = NeedOf(Ideal, frame.Given);
            Span = frame.Span;
        }

        // The ideal throttle moves towards where it is asked to be, where it is to be eased, so that a limit that changes quickly moves the bar evenly.
        private static void Ease(Vessel target, double asked, bool eased)
        {
            double now = Planetarium.GetUniversalTime();
            double dt = now - idealTime;
            if (!eased || target.id != idealVessel || dt < 0.0 || dt > 0.5) Ideal = asked;
            else if (dt > 1e-4) Ideal += (asked - Ideal) * (1.0 - Math.Exp(-dt / IdealEase));
            idealTime = now;
            idealVessel = target.id;
        }

        // The mark slides down once the guidance is done, and stands in the middle until then.
        private static void Slide(bool done)
        {
            float now = Time.realtimeSinceStartup;
            float elapsed = Math.Min(0.5f, Math.Max(0f, now - markTime));
            markTime = now;
            MarkAt = done ? MarkAt - MarkAt * (1.0 - Math.Exp(-elapsed / SlideEase)) : 0.5;
        }

        // The board is taken as not written: the guidance has ended, and the gauges are the game's.
        internal static void Forget()
        {
            written = -100f;
        }

        // Whether the board was written for this vessel a moment ago.
        internal static bool Current(Vessel target)
        {
            return target != null && target.id == vessel && Time.realtimeSinceStartup - written < Fresh;
        }
    }
}
