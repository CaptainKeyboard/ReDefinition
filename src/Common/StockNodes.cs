using System;
using System.Collections.Generic;

namespace ReDefinition.Core
{
    // The first maneuver node of a vessel that the planner's guidance did not make. Without the planner every node counts.
    internal static class StockNodes
    {
        // The first node of the vessel; null for none or before the solver exists.
        internal static ManeuverNode FirstOf(Vessel vessel)
        {
            PatchedConicSolver solver = vessel != null ? vessel.patchedConicSolver : null;
            if (solver == null) return null;
            GuidanceHook hook = PlannerHooks.Guidance;
            return First(solver.maneuverNodes, hook != null ? (Func<ManeuverNode, bool>)hook.IsGuideNode : null);
        }

        // The first item that is not skipped; all count where there is no test.
        internal static T First<T>(IList<T> items, Func<T, bool> skip) where T : class
        {
            if (items == null) return null;
            foreach (T each in items)
                if (skip == null || !skip(each)) return each;
            return null;
        }
    }
}
