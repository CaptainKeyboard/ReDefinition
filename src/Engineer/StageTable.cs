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
        // The thrust's lever about that centre, in metres (ThrustTorque over the sum
        // of the engines' thrust), and the most torque its gimbals can set against it.
        public double ThrustArm;
        public double GimbalTorque;

        // Filled by StageTable.Complete.
        public double DeltaVRemaining;
        public double TwrMax;

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

        // PhysicsGlobals.GravitationalAcceleration, KSP's g0.
        internal const double StandardGravity = 9.80665;
    }
}
