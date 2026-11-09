using System;
using System.Collections.Generic;
using ReDefinition.Planner.Engineer;

namespace ReDefinition.Planner.Planning
{
    // The air-breathing engines' part of a launch from a body (AirLaunch), over the planner's body figures.
    // Free of Unity, tested.
    internal static class LaunchShare
    {
        // The part of the launch into the body's low orbit (RoutePlanner.Launch) the
        // air-breathing engines fly: in the share of the orbital speed (less the
        // ground's own) they reach. 0 where the air holds no oxygen.
        internal static double Of(PlannerBody body, double mach)
        {
            if (body == null || !body.Oxygen || mach <= 0.0 || body.SpeedOfSound <= 0.0) return 0.0;
            double orbital = RoutePlanner.Circular(body.Mu, body.Radius + body.LowOrbitAltitude);
            double rotation = body.RotationPeriod > 0.0 ? 2.0 * Math.PI * body.Radius / body.RotationPeriod : 0.0;
            double reference = orbital - rotation;
            if (reference <= 0.0) return 0.0;
            return RoutePlanner.Launch(body) * Math.Min(1.0, mach * body.SpeedOfSound / reference);
        }

        // AirLaunch.Plan for a route that lifts off from `launch` (null where it does not start on a surface).
        internal static List<StageRow> Plan(IList<StageRow> rows, IList<PartFacts> facts, IList<SimPart> sim,
                                            IDictionary<string, double> density, PlannerBody launch, string launchId, out int airRow)
        {
            return AirLaunch.Plan(rows, facts, sim, density, launch != null ? (Func<double, double>)(mach => Of(launch, mach)) : null,
                launchId, out airRow);
        }
    }
}
