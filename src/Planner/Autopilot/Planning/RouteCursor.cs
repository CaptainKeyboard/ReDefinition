using System;
using System.Collections.Generic;

namespace ReDefinition.Planner.Autopilot.Planning
{
    // Where a vessel is on its route: the stop it is at, the last stop it has reached, and the stop it is on its way to. The question is answered from
    // what is known of each stop (whether the vessel is at it, whether it comes again) and of the count of stops reached so far, with no game object in it.
    internal struct RouteCursor
    {
        // The last stop the vessel is at by where it is, or -1; the stop it is on its way from, or -1; and the stop it goes to, or -1 where there is none.
        public int At;
        public int Last;
        public int Next;
        // Whether the route is done: the vessel is at its last stop.
        public bool Done;

        // What is known of each stop: whether the vessel is at or past it, and whether it comes again in the route.
        // The count of stops reached so far is raised to the last stop passed, where one is kept.
        // The vessel may stand on the first stop, a surface, and it may be on its way from a stop; which stop a vessel at none is headed for is asked for.
        internal static RouteCursor Locate(IList<bool> passed, IList<bool> repeats, ref int reached, bool counted, bool onFirstSurface, Func<int, bool> onTheWayFrom,
                                           Func<int> headedFor)
        {
            int last = -1;
            for (int i = 0; i < passed.Count; i++)
                if (passed[i] && (!counted || !repeats[i] || reached >= i - 1)) last = i;
            if (counted && last > reached) reached = last;
            // The first stop, a surface, counts as passed wherever the vessel is round its body, so a vessel back at that body is on its way from the stop it
            // reached last, not from the first. A vessel still on the ground at the first stop is at it.
            int at = last;
            if (counted && reached > last && !(at == 0 && onFirstSurface) && onTheWayFrom(reached)) last = reached;
            bool done = last + 1 >= passed.Count;
            int next = done ? -1 : last >= 0 ? last + 1 : headedFor();
            return new RouteCursor { At = at, Last = last, Next = next, Done = done };
        }
    }
}
