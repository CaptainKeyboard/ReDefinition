using System.Collections.Generic;

namespace ReDefinition.Engineer
{
    // One stage of the vessel in the editor, as the engineer window lists it: what
    // KSP's own delta-v simulation gives for it (VesselDeltaV) and what is worked
    // out beside it. Stages are numbered as KSP numbers them: the highest fires
    // first, stage 0 last.
    internal sealed class StageRow
    {
        public int Number;
        public bool HasEngines;

        // For the situation chosen (body, sea level, altitude or vacuum).
        public double DeltaV;
        public double Isp;
        public double Thrust;
        public double Twr;
        public double BurnTime;
        public double StartMass;
        public double EndMass;

        // In vacuum, whatever situation is chosen: what the planner budgets with.
        public double DeltaVVacuum;
        public double ThrustVacuum;
        // At the surface of each body with air, by its name (CelestialBody.bodyName).
        public Dictionary<string, double> ThrustAtSurface = new Dictionary<string, double>();
        // The engines that burn in it, as "2 x LV-T45"; empty where none do.
        public string Engines = "";

        // What is dropped with this stage, and what is attached while it burns.
        public int OwnParts;
        public int AttachedParts;
        public double OwnCost;
        public double AttachedCost;

        // Of the engines burning in this stage, about the centre of mass at its
        // start: how much their thrust turns the vessel.
        public double ThrustTorque;

        // Filled by StageTable.Complete.
        public double DeltaVRemaining;
        public double TwrMax;

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

    // The table the engineer window shows, worked out from the stages: delta-v left
    // from each stage on, and the thrust-to-weight ratio at each stage's end. Free
    // of Unity, tested.
    internal static class StageTable
    {
        // The rows in KSP's order, stage 0 first. The TWR at a stage's end is the
        // same thrust over the mass it ends with: the weight the TWR divides by
        // comes from the TWR KSP gives at the start.
        internal static List<StageRow> Complete(IEnumerable<StageRow> stages)
        {
            List<StageRow> rows = new List<StageRow>(stages);
            rows.Sort((a, b) => a.Number.CompareTo(b.Number));

            double remaining = 0.0;
            foreach (StageRow row in rows)
            {
                remaining += row.DeltaV;
                row.DeltaVRemaining = remaining;
                row.TwrMax = row.Twr > 0.0 && row.EndMass > 0.0 && row.StartMass > 0.0
                    ? row.Twr * row.StartMass / row.EndMass
                    : row.Twr;
            }
            return rows;
        }

        internal static double TotalDeltaV(IList<StageRow> rows)
        {
            double total = 0.0;
            foreach (StageRow row in rows) total += row.DeltaV;
            return total;
        }

        // The stage that fires first and has engines: the TWR the vessel lifts
        // off with. Null where no stage has thrust.
        internal static StageRow FirstBurning(IList<StageRow> rows)
        {
            StageRow first = null;
            foreach (StageRow row in rows)
            {
                if (!row.HasEngines || row.Thrust <= 0.0) continue;
                if (first == null || row.Number > first.Number) first = row;
            }
            return first;
        }

        // The rows the table shows: every stage, or only those with delta-v.
        internal static List<StageRow> Shown(IList<StageRow> rows, bool allStages)
        {
            List<StageRow> shown = new List<StageRow>();
            foreach (StageRow row in rows)
                if (allStages || row.DeltaV > 0.0) shown.Add(row);
            return shown;
        }

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
