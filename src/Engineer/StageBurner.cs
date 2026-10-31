using System;
using System.Collections.Generic;
using ReDefinition.Autopilot.Background;

namespace ReDefinition.Engineer
{
    // The burns of a plan shared over the vessel's stages as KSP numbers them, the highest first. A stage burns the propellant of its own tanks
    // by the rocket equation, and when it is spent the next is fired, which lets go of the parts that stage decouples.
    // The stages, and the propellant each part holds for each of them, are KSP's own simulation of the vessel (EditorReadout).
    // What is taken from the tanks and what is let go is what the flight stage by stage would do, without flying it.
    internal sealed class StageBurner : IBurnShare
    {
        private readonly List<StageRow> rows = new List<StageRow>();
        private readonly List<PartFacts> facts;
        // The stage that burns now, by its place in `rows`, and how much of its delta-v is used.
        private int current;
        private double used;

        internal StageBurner(EditorReadout readout)
        {
            facts = readout != null ? readout.PartFacts : null;
            if (readout == null || readout.Stages == null) return;
            foreach (StageRow row in readout.Stages)
                if (row.DeltaVVacuum > 0.0) rows.Add(row);
            rows.Sort((a, b) => b.Number.CompareTo(a.Number));
        }

        public bool Ready
        {
            get { return rows.Count > 0 && facts != null && facts.Count > 0; }
        }

        // The delta-v asked for that no stage had left, in m/s.
        public double Short { get; private set; }

        // The time the engines have burned in all the burns so far, in s: each stage's burn time for the share of its delta-v used.
        public double Elapsed { get; private set; }

        private static double Burnt(StageRow row, double flown)
        {
            if (row.StartMass <= row.EndMass || row.EndMass <= 0.0) return flown;
            double ratio = Math.Log(row.StartMass / row.EndMass);
            return (1.0 - Math.Exp(-flown * ratio)) / (1.0 - row.EndMass / row.StartMass);
        }

        // A burn of `deltaV` m/s. For each part that burns propellant in it, the share it keeps (multiplied into `keeps`), and the persistent ids of the parts
        // let go when a stage was spent and the next fired (added to `gone`).
        public void Burn(double deltaV, Dictionary<uint, double> keeps, List<uint> gone)
        {
            while (deltaV > 1e-6)
            {
                if (current >= rows.Count)
                {
                    Short += deltaV;
                    return;
                }
                StageRow row = rows[current];
                double take = Math.Min(deltaV, row.DeltaVVacuum - used);
                // The share of the stage's propellant gone when a share of its delta-v is flown: the rocket equation, not a straight line.
                double before = Burnt(row, used / row.DeltaVVacuum), after = Burnt(row, (used + take) / row.DeltaVVacuum);
                foreach (PartFacts fact in facts)
                {
                    double[] fuel;
                    if (!fact.StageFuel.TryGetValue(row.Number, out fuel) || fuel[0] <= 1e-9) continue;
                    double held = fuel[0] - before * (fuel[0] - fuel[1]), left = fuel[0] - after * (fuel[0] - fuel[1]);
                    if (held <= 1e-9) continue;
                    double kept;
                    keeps.TryGetValue(fact.Id, out kept);
                    keeps[fact.Id] = (kept > 0.0 ? kept : 1.0) * Math.Max(0.0, left / held);
                }
                Elapsed += row.BurnTime * take / row.DeltaVVacuum;
                used += take;
                deltaV -= take;
                if (used < row.DeltaVVacuum - 1e-6) continue;
                // The stage is spent: the next one is fired, and the stages between that have nothing to burn.
                current++;
                used = 0.0;
                int next = current < rows.Count ? rows[current].Number : 0;
                foreach (PartFacts fact in facts)
                    if (fact.DecoupleStage >= next && fact.DecoupleStage < row.Number && !gone.Contains(fact.Id)) gone.Add(fact.Id);
            }
        }
    }
}
