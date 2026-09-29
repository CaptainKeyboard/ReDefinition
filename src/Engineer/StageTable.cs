using System.Collections.Generic;

namespace ReDefinition.Engineer
{
    // One stage of the vessel in the editor, as the planner takes it: what KSP's
    // own delta-v simulation gives for it (VesselDeltaV) and what is worked out
    // beside it. Stages are numbered as KSP numbers them: the highest fires
    // first, stage 0 last.
    internal sealed class StageRow
    {
        public int Number;

        // For the situation chosen (body, sea level, altitude or vacuum).
        public double DeltaV;
        public double Thrust;
        public double BurnTime;
        public double StartMass;
        public double EndMass;

        // In vacuum, whatever situation is chosen: what the planner budgets with.
        public double DeltaVVacuum;
        public double ThrustVacuum;
        // At the surface of each body with air, by its name (CelestialBody.bodyName).
        public Dictionary<string, double> ThrustAtSurface = new Dictionary<string, double>();

        // Its engines cannot be shut down or throttled (solid rocket boosters,
        // ModuleEngines.throttleLocked or !allowShutdown): once lit it burns out.
        public bool Solid;
        // The air-breathing engines' part of the launch (AirLaunch), not a stage of
        // KSP's.
        public bool Air;
        // The electric charge its engines burn at full thrust, in units per second
        // (an ion engine's), which KSP's simulation leaves out.
        public double ElectricRate;

        // A copy to change, its thrust at the surfaces its own.
        internal StageRow Copy()
        {
            StageRow copy = (StageRow)MemberwiseClone();
            copy.ThrustAtSurface = new Dictionary<string, double>(ThrustAtSurface);
            return copy;
        }
    }

    // The rocket equation and the propellant thrusters can burn, as the planner
    // works with them. Free of Unity, tested.
    internal static class StageTable
    {
        // The delta-v a stage gets from its engines' specific impulse and the
        // propellant it burns: Tsiolkovsky, with standard gravity as KSP uses it.
        internal static double RocketDeltaV(double isp, double startMass, double endMass)
        {
            if (isp <= 0.0 || startMass <= 0.0 || endMass <= 0.0 || endMass >= startMass) return 0.0;
            return isp * StandardGravity * System.Math.Log(startMass / endMass);
        }

        // The propellant thrusters can burn from what is held, in tonnes: they draw
        // their propellants in the mix they burn them in (`mix`, per resource its
        // share of the flow by mass, in any unit), so the resource that runs out
        // first ends it and the rest of the others stays. `held`: per resource, in
        // tonnes.
        internal static double UsablePropellant(IDictionary<string, double> mix, IDictionary<string, double> held)
        {
            double total = 0.0;
            foreach (double share in mix.Values) total += System.Math.Max(0.0, share);
            if (total <= 0.0) return 0.0;
            double usable = double.MaxValue;
            foreach (KeyValuePair<string, double> share in mix)
            {
                if (share.Value <= 0.0) continue;
                double amount;
                held.TryGetValue(share.Key, out amount);
                usable = System.Math.Min(usable, System.Math.Max(0.0, amount) * total / share.Value);
            }
            return usable == double.MaxValue ? 0.0 : usable;
        }

        // Thrusters of several mixes (monopropellant blocks and Vernors) each burn their
        // own: the sum of what each mix can burn of what is held, each resource
        // counted once, for the mix that burns the most of it.
        internal static double UsablePropellant(IDictionary<string, Dictionary<string, double>> mixes, IDictionary<string, double> held)
        {
            double total = 0.0;
            Dictionary<string, double> left = new Dictionary<string, double>(held);
            foreach (Dictionary<string, double> mix in mixes.Values)
            {
                double usable = UsablePropellant(mix, left);
                double sum = 0.0;
                foreach (double share in mix.Values) sum += System.Math.Max(0.0, share);
                if (usable <= 0.0 || sum <= 0.0) continue;
                total += usable;
                foreach (KeyValuePair<string, double> share in mix)
                {
                    double amount;
                    if (left.TryGetValue(share.Key, out amount)) left[share.Key] = System.Math.Max(0.0, amount - usable * share.Value / sum);
                }
            }
            return total;
        }

        // PhysicsGlobals.GravitationalAcceleration, KSP's g0.
        internal const double StandardGravity = 9.80665;
    }
}
