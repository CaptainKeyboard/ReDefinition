using System;
using System.Globalization;
using System.Text;
using ReDefinition.Planner.Autopilot.Planning;
using UnityEngine;

namespace ReDefinition.Planner.Autopilot.Flight
{
    // One burn of a node, watched from the order to MechJeb to the end. It records when the thrust began against when it was due, how the nose pointed
    // then, how fast the vessel turned, and the orbit the burn left against the one the node showed. It sees what the burn did, and changes nothing.
    internal sealed class BurnWatch
    {
        private readonly Vessel vessel;
        private readonly ManeuverNode node;
        private readonly NodePlanner.BurnPurpose purpose;
        private readonly int number;
        private readonly double nodeUT, size, planned, begun;
        // The orbit after the node as the flight plan showed it, and the periapsis of the patch round another body it showed, if any.
        private readonly double predictedPeriapsis = double.NaN, predictedApoapsis = double.NaN, predictedTargetPeriapsis = double.NaN;
        private readonly string predictedTarget;
        private readonly double angleAtOrder = double.NaN;
        private double thrustFrom = double.NaN, thrustTo = double.NaN, rateAtStart, maxRate, angleAtStart = double.NaN;

        internal BurnWatch(Vessel vessel, ManeuverNode node, NodePlanner.BurnPurpose purpose, int number)
        {
            this.vessel = vessel;
            this.node = node;
            this.purpose = purpose;
            this.number = number;
            begun = Planetarium.GetUniversalTime();
            nodeUT = node != null ? node.UT : begun;
            size = node != null ? node.GetBurnVector(vessel.orbit).magnitude : 0.0;
            if (node != null)
            {
                Vector3 burn = (Vector3)node.GetBurnVector(vessel.orbit);
                if (burn.sqrMagnitude > 0f) angleAtOrder = Vector3.Angle(vessel.GetTransform().up, burn);
            }
            double acceleration = Engines.Acceleration(vessel);
            // The burn is centred on the node: it is to begin half its length before the node's time.
            planned = nodeUT - (acceleration > 0.0 ? 0.5 * size / acceleration : 0.0);
            if (node != null && node.nextPatch != null)
            {
                predictedPeriapsis = node.nextPatch.PeA;
                predictedApoapsis = node.nextPatch.ApA;
            }
            predictedTarget = TargetPatch(vessel, out predictedTargetPeriapsis);
        }

        // The first patch of the flight plan that is round another body than the vessel's own, and its periapsis over that body's ground.
        private static string TargetPatch(Vessel vessel, out double periapsis)
        {
            periapsis = double.NaN;
            if (vessel.patchedConicSolver == null || vessel.patchedConicSolver.flightPlan == null) return null;
            foreach (Orbit patch in vessel.patchedConicSolver.flightPlan)
                if (patch.referenceBody != vessel.mainBody)
                {
                    periapsis = patch.PeA;
                    return patch.referenceBody.bodyName;
                }
            return null;
        }

        // Called every half second while MechJeb flies the node.
        internal void Sample()
        {
            double ut = Planetarium.GetUniversalTime();
            double rate = vessel.angularVelocity.magnitude;
            if (rate > maxRate) maxRate = rate;
            if (Engines.Burning(vessel))
            {
                if (double.IsNaN(thrustFrom))
                {
                    thrustFrom = ut;
                    rateAtStart = rate;
                    if (node != null && vessel.patchedConicSolver != null && vessel.patchedConicSolver.maneuverNodes.Contains(node))
                    {
                        Vector3 burn = (Vector3)node.GetBurnVector(vessel.orbit);
                        if (burn.sqrMagnitude > 0f) angleAtStart = Vector3.Angle(vessel.GetTransform().up, burn);
                    }
                }
                thrustTo = ut;
            }
        }

        // Whether thrust was seen: a burn that MechJeb never lit is not counted.
        internal bool Burned
        {
            get { return !double.IsNaN(thrustFrom); }
        }

        // The burn's line for the log, and the same for the ledger where the burn was made.
        internal string Finish(FlightLedger ledger)
        {
            var culture = CultureInfo.InvariantCulture;
            var text = new StringBuilder();
            text.Append("burn ").Append(number + 1).Append(" (").Append(purpose).Append("): ").Append(size.ToString("0.0", culture)).Append(" m/s, the nose ");
            text.Append(double.IsNaN(angleAtOrder) ? "?" : angleAtOrder.ToString("0", culture)).Append(" degrees off it at the order, due ");
            text.Append((planned - begun).ToString("0", culture)).Append(" s after the order; ");
            if (double.IsNaN(thrustFrom)) text.Append("no thrust seen; ");
            else
                text.Append("thrust began ").Append((thrustFrom - planned).ToString("+0.0;-0.0", culture)).Append(" s against the plan and lasted ")
                    .Append((thrustTo - thrustFrom).ToString("0.0", culture)).Append(" s; ");
            text.Append(double.IsNaN(angleAtStart) ? "angle to the burn at the start: not seen" : "nose " + angleAtStart.ToString("0.0", culture) + " degrees off the burn at the start").Append("; turn at the start ");
            text.Append(rateAtStart.ToString("0.000", culture)).Append(" rad/s, at most ").Append(maxRate.ToString("0.000", culture)).Append(" rad/s; orbit after: ");
            Orbit now = vessel.orbit;
            text.Append(now.PeA.ToString("0", culture)).Append(" x ").Append(now.ApA.ToString("0", culture)).Append(" m, the node showed ");
            text.Append(double.IsNaN(predictedPeriapsis) ? "?" : predictedPeriapsis.ToString("0", culture)).Append(" x ");
            text.Append(double.IsNaN(predictedApoapsis) ? "?" : predictedApoapsis.ToString("0", culture)).Append(" m");
            double actualTarget;
            string target = TargetPatch(vessel, out actualTarget);
            if (predictedTarget != null || target != null)
                text.Append("; periapsis round ").Append(target ?? predictedTarget).Append(": ").Append(double.IsNaN(actualTarget) ? "none" : actualTarget.ToString("0", culture))
                    .Append(" m, the node showed ").Append(double.IsNaN(predictedTargetPeriapsis) ? "none" : predictedTargetPeriapsis.ToString("0", culture)).Append(" m");
            text.Append('.');
            string line = text.ToString();
            if (Burned) ledger.Add(purpose, line);
            return line;
        }
    }
}
