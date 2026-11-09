using System;
using System.Collections.Generic;

namespace ReDefinition.Planner.Engineer
{
    // One stage as KSP's delta-v simulation gives it (DeltaVStageInfo): the mass
    // when it starts and ends burning, and its delta-v in vacuum. A solid stage burns
    // out once lit (StageRow.Solid).
    internal sealed class BudgetStage
    {
        // KSP's stage number; the mass as it starts and ends burning, in tonnes; its delta-v in vacuum; whether it is solid.
        public int Number;
        public double StartMass;
        public double EndMass;
        public double DeltaV;
        public bool Solid;
        // Burns on the route's first step only (the air-breathing engines' part of
        // the launch); what it has not burnt by then counts no more.
        public bool FirstStepOnly;
    }

    // How the vessel's mass and stages fall into groups (StageBudget): group 0 the
    // rest, each other group a section of the vessel (PlannerState.Sections) less the
    // sections inside it. Per stage, by its number: the group whose parts it fires,
    // and how the propellant it burns and the mass KSP drops after it share out over
    // the groups. Built from the parts (VesselShape.Split), or from stage numbers
    // (ByStages).
    internal sealed class VesselSplit
    {
        // Per group, in tonnes, as the vessel starts.
        public readonly double[] Mass;
        // By stage number, per group; shares, summed to one by StageBudget.
        public readonly Dictionary<int, double[]> Fuel = new Dictionary<int, double[]>();
        public readonly Dictionary<int, double[]> Drop = new Dictionary<int, double[]>();
        public readonly Dictionary<int, int> Owner = new Dictionary<int, int>();
        // What KSP drops as the whole vessel starts the stages of a group that flies
        // alone later is the rest itself, and stays while that group is to come back
        // (ByStages). From the parts, it is what KSP drops there, and goes.
        public bool RestStaysForLander;

        // Every group's mass zero; at least one group.
        internal VesselSplit(int groups)
        {
            Mass = new double[Math.Max(1, groups)];
        }

        // How many groups: the rest and the sections.
        public int Groups
        {
            get { return Mass.Length; }
        }

        // Shares that give everything to the group, held within the groups there are.
        internal double[] Unit(int group)
        {
            double[] shares = new double[Groups];
            shares[Math.Max(0, Math.Min(group, Groups - 1))] = 1.0;
            return shares;
        }

        // The stage-number model, where the vessel has no parts to go by: group 1 the
        // lander, the stages numbered up to `lander` less the capsule's; group 2 the
        // capsule, those up to `capsule`; -1 for none. A lander needs something to
        // leave behind, and something to be; a capsule inside the lander, or with no
        // lander.
        internal static VesselSplit ByStages(IList<BudgetStage> stages, int lander, int capsule)
        {
            List<BudgetStage> order = new List<BudgetStage>(stages);
            order.Sort((a, b) => b.Number.CompareTo(a.Number));
            int count = order.Count;
            int firstLander = First(order, lander);
            int firstCapsule = First(order, capsule);
            bool hasLander = firstLander > 0 && firstLander < count;
            bool hasCapsule = firstCapsule > 0 && firstCapsule < count && (!hasLander || firstCapsule >= firstLander);
            VesselSplit split = new VesselSplit(3) { RestStaysForLander = true };
            for (int i = 0; i < count; i++)
            {
                int owner = hasCapsule && i >= firstCapsule ? 2 : hasLander && i >= firstLander ? 1 : 0;
                split.Owner[order[i].Number] = owner;
                split.Fuel[order[i].Number] = split.Unit(owner);
                split.Drop[order[i].Number] = split.Unit(owner);
            }
            double start = count > 0 ? Math.Max(0.0, order[0].StartMass) : 0.0;
            double capsuleMass = hasCapsule ? Math.Max(0.0, order[firstCapsule].StartMass) : 0.0;
            double landerMass = hasLander ? Math.Max(0.0, Math.Max(0.0, order[firstLander].StartMass) - capsuleMass) : 0.0;
            split.Mass[2] = capsuleMass;
            split.Mass[1] = landerMass;
            split.Mass[0] = Math.Max(0.0, start - capsuleMass - landerMass);
            return split;
        }

        // Where in `order`, highest number first, the stages numbered up to `top` begin; the count for none or for -1.
        private static int First(List<BudgetStage> order, int top)
        {
            if (top < 0) return order.Count;
            for (int i = 0; i < order.Count; i++)
                if (order[i].Number <= top) return i;
            return order.Count;
        }
    }
}
