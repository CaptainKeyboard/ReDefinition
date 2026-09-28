using System.Collections.Generic;
using System;

namespace ReDefinition.Engineer
{
    // What the planner needs to know of one part to find the lander and the capsule:
    // the stage KSP decouples it at (DeltaVPartInfo.decoupleStage, -1 for never; the
    // part is attached while a stage burns whose number is no lower) and what it is.
    internal sealed class PartFacts
    {
        public int DecoupleStage;
        public bool Command;
        public bool Engine;
        public bool Legs;
        public bool Parachute;
        // A decoupler or a docking port (IStageSeparator).
        public bool Separator;
    }

    // The lander and the capsule as the vessel's parts give them, for "Automatic"
    // (docs/development/planner.md). Parts decoupled at the same stage go together,
    // so the vessel falls into groups by that stage. A stage number stands for what
    // is attached while that stage burns: the parts decoupled at it or later.
    //
    // The capsule: the command part KSP keeps longest, with everything that stays as
    // long, where that carries parachutes. The lander: the fewest groups before the
    // capsule, from the capsule up, with engines and landing legs, separated from
    // the rest by a decoupler or a docking port, where the rest keeps a command part
    // of its own to wait for it. Free of Unity, tested.
    internal static class VesselShape
    {
        // `stages`: the vessel's stage numbers in KSP's order, the highest first.
        // -1 for none.
        internal static int Capsule(IList<PartFacts> parts, IList<int> stages)
        {
            int keeps = int.MaxValue;
            foreach (PartFacts part in parts)
                if (part.Command) keeps = Math.Min(keeps, part.DecoupleStage);
            if (keeps == int.MaxValue) return -1;
            int stage = StageOf(parts, stages, keeps);
            if (stage < 0) return -1;
            foreach (PartFacts part in parts)
                if (part.Parachute && part.DecoupleStage <= stage) return stage;
            return -1;
        }

        // `capsule`: the capsule's stage, -1 for none. -1 for no lander.
        internal static int Lander(IList<PartFacts> parts, IList<int> stages, int capsule)
        {
            int below = capsule >= 0 ? capsule : int.MinValue;
            SortedSet<int> bounds = new SortedSet<int>();
            foreach (PartFacts part in parts)
                if (part.DecoupleStage > below) bounds.Add(part.DecoupleStage);
            foreach (int bound in bounds)
            {
                bool engine = false, legs = false, separator = false, command = false, rest = false;
                int next = int.MaxValue;
                foreach (PartFacts part in parts)
                {
                    if (part.DecoupleStage > bound)
                    {
                        rest = true;
                        command |= part.Command;
                        next = Math.Min(next, part.DecoupleStage);
                    }
                    else if (part.DecoupleStage > below)
                    {
                        engine |= part.Engine;
                        legs |= part.Legs;
                        separator |= part.Separator;
                    }
                }
                // With nothing left above it, the lander would be the whole vessel.
                if (!rest) return -1;
                if (!engine || !legs || !command) continue;
                foreach (PartFacts part in parts)
                    if (part.DecoupleStage == next && part.Separator) separator = true;
                if (!separator) continue;
                int stage = StageOf(parts, stages, bound);
                if (stage >= 0 && stage > capsule) return stage;
            }
            return -1;
        }

        // The highest stage but the first at which only the parts decoupled at
        // `bound` or later are attached, -1 where there is none: before the parts of
        // the next group go, nothing of it is left.
        private static int StageOf(IList<PartFacts> parts, IList<int> stages, int bound)
        {
            int next = int.MaxValue;
            foreach (PartFacts part in parts)
                if (part.DecoupleStage > bound) next = Math.Min(next, part.DecoupleStage);
            if (next == int.MaxValue) return -1;
            for (int i = 1; i < stages.Count; i++)
                if (stages[i] < next && stages[i] >= bound) return stages[i];
            return -1;
        }
    }
}
