using System.Collections.Generic;
using System.Linq;
using System;

namespace ReDefinition.Engineer
{
    // How a part separates the vessel: a decoupler stays with the side it is
    // attached to; a radial decoupler and a docking port go with the side under
    // them in the part tree.
    internal enum Separation
    {
        None,
        Decoupler,
        Anchored,
        DockingPort,
    }

    // What the planner needs to know of one part (EditorReadout): where it hangs in
    // the part tree, the stage KSP decouples it at (DeltaVPartInfo.decoupleStage, -1
    // for never; the part is attached while a stage burns whose number is no lower)
    // and the one it is fired in (Part.inverseStage), what it is, and its masses.
    internal sealed class PartFacts
    {
        // Its parent's index in the list, -1 for the root.
        public int Parent = -1;
        public int DecoupleStage = -1;
        public int ActivatesAt = -1;
        public bool Command;
        public bool Engine;
        public bool Legs;
        public bool Parachute;
        public bool HeatShield;
        public Separation Separator;
        public uint Id;
        public string Title = "";
        // In tonnes: with its resources as the vessel starts, and without.
        public double Mass;
        public double DryMass;
        // By stage number: its resources' mass as the stage starts and as it ends.
        public readonly Dictionary<int, double[]> StageFuel = new Dictionary<int, double[]>();
        // The part itself, for the colours on the vessel.
        public object Tag;
    }

    // A part of the vessel that can fly apart from the rest: the parts on one side of a
    // decoupler or docking port that are still attached when it separates.
    internal sealed class PartGroupChoice
    {
        // The separator's id and the side, kept in the plan.
        public string Key;
        public string Name;
        public readonly HashSet<int> Members = new HashSet<int>();
        public double Mass;
        public bool Engine;
        public bool Legs;
        public bool Command;
        public bool Parachute;
        public bool DockingPort;
    }

    // The lander and the capsule as parts of the vessel (docs/development/planner.md).
    // Each decoupler and docking port cuts the part tree in two; each side is a
    // choice, less the parts KSP stages away before its command part, its legs and
    // its parachutes. Automatic: the capsule is the lightest choice with a command
    // part and parachutes; the lander the lightest one with engines and landing legs
    // that a docking port separates, so that it can come back. The rest is everything
    // else; the capsule is part of the lander or of the rest. Free of Unity, tested.
    internal static class VesselShape
    {
        internal static List<PartGroupChoice> Candidates(IList<PartFacts> parts)
        {
            List<PartGroupChoice> choices = new List<PartGroupChoice>();
            HashSet<string> seen = new HashSet<string>();
            List<int>[] children = Children(parts);
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].Separator == Separation.None) continue;
                HashSet<int> under = Subtree(children, i);
                if (parts[i].Separator == Separation.Decoupler) under.Remove(i);
                HashSet<int> over = new HashSet<int>(Enumerable.Range(0, parts.Count).Where(at => !under.Contains(at)));
                Add(choices, seen, parts, Trim(parts, under), parts[i], "under");
                Add(choices, seen, parts, Trim(parts, over), parts[i], "over");
            }
            choices.Sort((a, b) => a.Mass.CompareTo(b.Mass));
            return choices;
        }

        internal static PartGroupChoice AutoCapsule(IList<PartGroupChoice> choices)
        {
            foreach (PartGroupChoice choice in choices)
                if (choice.Command && choice.Parachute) return choice;
            return null;
        }

        internal static PartGroupChoice AutoLander(IList<PartGroupChoice> choices, PartGroupChoice capsule)
        {
            foreach (PartGroupChoice choice in choices)
                if (choice.Engine && choice.Legs && choice.DockingPort && Fits(choice, capsule)) return choice;
            return null;
        }

        // The capsule inside the lander, or apart from it.
        internal static bool Fits(PartGroupChoice lander, PartGroupChoice capsule)
        {
            if (lander == null || capsule == null) return true;
            if (lander.Key == capsule.Key) return false;
            return Inside(capsule, lander) || !capsule.Members.Overlaps(lander.Members);
        }

        internal static bool Inside(PartGroupChoice capsule, PartGroupChoice lander)
        {
            return capsule != null && lander != null && capsule.Members.IsSubsetOf(lander.Members);
        }

        // The vessel's stages shared out over the rest, the lander and the capsule, by
        // the parts each stage fires, burns and drops; the groups' masses scaled to
        // KSP's own mass of the whole vessel.
        internal static VesselSplit Split(IList<BudgetStage> stages, IList<PartFacts> parts, PartGroupChoice lander,
                                          PartGroupChoice capsule)
        {
            VesselSplit split = new VesselSplit
            {
                HasLander = lander != null, HasCapsule = capsule != null, CapsuleInLander = Inside(capsule, lander),
            };
            PartGroup[] group = new PartGroup[parts.Count];
            for (int i = 0; i < parts.Count; i++)
                group[i] = capsule != null && capsule.Members.Contains(i) ? PartGroup.Capsule
                    : lander != null && lander.Members.Contains(i) ? PartGroup.Lander : PartGroup.Rest;

            double total = 0.0;
            for (int i = 0; i < parts.Count; i++)
            {
                split.Mass[(int)group[i]] += Math.Max(0.0, parts[i].Mass);
                total += Math.Max(0.0, parts[i].Mass);
            }
            double start = stages.Count > 0 ? stages.Max(stage => stage.StartMass) : 0.0;
            if (total > 0.0 && start > 0.0)
                for (int g = 0; g < VesselSplit.Groups; g++) split.Mass[g] *= start / total;

            foreach (BudgetStage stage in stages)
            {
                int number = stage.Number;
                double[] fuel = new double[VesselSplit.Groups];
                double[] drop = new double[VesselSplit.Groups];
                double[] fired = new double[VesselSplit.Groups];
                double[] engines = new double[VesselSplit.Groups];
                for (int i = 0; i < parts.Count; i++)
                {
                    PartFacts part = parts[i];
                    int g = (int)group[i];
                    double[] mass;
                    bool known = part.StageFuel.TryGetValue(number, out mass) && mass != null && mass.Length >= 2;
                    if (known) fuel[g] += Math.Max(0.0, mass[0] - mass[1]);
                    if (part.DecoupleStage == number) drop[g] += Math.Max(0.0, part.DryMass + (known ? mass[1] : part.Mass - part.DryMass));
                    if (part.ActivatesAt == number)
                    {
                        fired[g] += 1.0;
                        if (part.Engine) engines[g] += 1.0;
                    }
                }
                PartGroup owner = Largest(engines) ?? Largest(fired) ?? Largest(fuel) ?? PartGroup.Rest;
                split.Owner[number] = owner;
                split.Fuel[number] = fuel;
                split.Drop[number] = drop;
            }
            return split;
        }

        private static PartGroup? Largest(double[] values)
        {
            int best = -1;
            for (int g = 0; g < values.Length; g++)
                if (values[g] > 1e-9 && (best < 0 || values[g] > values[best])) best = g;
            return best >= 0 ? (PartGroup?)best : null;
        }

        // A side less the parts KSP stages away before the ones that make it what it
        // is: its command parts, landing legs and parachutes.
        private static HashSet<int> Trim(IList<PartFacts> parts, HashSet<int> side)
        {
            int latest = int.MinValue;
            foreach (int at in side)
                if (parts[at].Command || parts[at].Legs || parts[at].Parachute) latest = Math.Max(latest, parts[at].DecoupleStage);
            if (latest == int.MinValue) return side;
            return new HashSet<int>(side.Where(at => parts[at].DecoupleStage <= latest));
        }

        private static void Add(List<PartGroupChoice> choices, HashSet<string> seen, IList<PartFacts> parts, HashSet<int> side,
                                PartFacts separator, string which)
        {
            if (side.Count == 0 || side.Count == parts.Count) return;
            string members = string.Join(",", side.OrderBy(at => at).Select(at => at.ToString()).ToArray());
            if (!seen.Add(members)) return;
            PartGroupChoice choice = new PartGroupChoice { Key = separator.Id + ":" + which, DockingPort = separator.Separator == Separation.DockingPort };
            choice.Members.UnionWith(side);
            PartFacts named = null;
            foreach (int at in side)
            {
                PartFacts part = parts[at];
                choice.Mass += Math.Max(0.0, part.Mass);
                choice.Engine |= part.Engine;
                choice.Legs |= part.Legs;
                choice.Command |= part.Command;
                choice.Parachute |= part.Parachute;
                if (named == null || Rank(part) > Rank(named) || (Rank(part) == Rank(named) && part.Mass > named.Mass)) named = part;
            }
            choice.Name = named.Title + ", " + side.Count + (side.Count == 1 ? " part" : " parts");
            choices.Add(choice);
        }

        // What names a group: a command part before an engine before anything else.
        private static int Rank(PartFacts part)
        {
            return part.Command ? 2 : part.Engine ? 1 : 0;
        }

        private static List<int>[] Children(IList<PartFacts> parts)
        {
            List<int>[] children = new List<int>[parts.Count];
            for (int i = 0; i < parts.Count; i++) children[i] = new List<int>();
            for (int i = 0; i < parts.Count; i++)
                if (parts[i].Parent >= 0 && parts[i].Parent < parts.Count) children[parts[i].Parent].Add(i);
            return children;
        }

        private static HashSet<int> Subtree(List<int>[] children, int top)
        {
            HashSet<int> set = new HashSet<int>();
            Stack<int> open = new Stack<int>();
            open.Push(top);
            while (open.Count > 0)
            {
                int at = open.Pop();
                if (!set.Add(at)) continue;
                foreach (int child in children[at]) open.Push(child);
            }
            return set;
        }
    }
}
