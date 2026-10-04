using System.Collections.Generic;
using System.Linq;
using System;
using UnityEngine;

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
    // for never; AttachedIn) and the one it is fired in (Part.inverseStage), what it
    // is, and its masses.
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
        // A wing: a lifting surface (ModuleLiftingSurface) that is no pod or heat
        // shield, which carry one for their body's lift. Wheels: landing gear, not a
        // landing leg. With both the vessel can take off along a runway, below a TWR
        // of 1.
        public bool Lift;
        public bool Wheels;
        // Seats for crew (Part.CrewCapacity).
        public int Crew;
        // A launch clamp: it stays on the ground, part of nothing that flies.
        public bool Clamp;
        public Separation Separator;
        public uint Id;
        public string Title = "";
        // In tonnes: with its resources as the vessel starts, and without.
        public double Mass;
        public double DryMass;
        // Its RCS thrusters: their thrust in vacuum in kN at their thrust limit, that
        // thrust over their vacuum Isp (so that thrusters combine), and per mix of
        // propellants they burn (its names joined), per propellant that flow's share
        // of it by mass (StageTable.UsablePropellant); the mass of each resource any RCS
        // thruster on the vessel burns that the part holds, in tonnes.
        public double RcsThrust;
        public double RcsThrustOverIsp;
        public readonly Dictionary<string, Dictionary<string, double>> RcsMix = new Dictionary<string, Dictionary<string, double>>();
        public readonly Dictionary<string, double> RcsResources = new Dictionary<string, double>();
        // Where it sits and how large it is, for how the vessel turns (Turning): its
        // centre of mass, in metres in the vessel's frame (the root part's: x pitch,
        // y roll, z yaw), and the size of the box round its colliders there.
        public Vector3 Position;
        public Vector3 Size;
        // Its reaction wheels' torque at their authority limiter, in kN m: pitch,
        // roll, yaw.
        public Vector3 WheelTorque;
        // Its RCS thrusters' nozzles.
        public readonly List<Nozzle> Nozzles = new List<Nozzle>();
        // Its gimballing engines' thrust in vacuum at their thrust limit times the
        // sine of their gimbal range at its limiter, in kN: the side force they can
        // turn the vessel with while they burn.
        public double GimbalSide;
        // A control surface (ModuleControlSurface): it steers in the air, by as much
        // as the air allows, which Turning does not count.
        public bool Fin;
        // By stage number: its resources' mass as the stage starts and as it ends.
        public readonly Dictionary<int, double[]> StageFuel = new Dictionary<int, double[]>();
        // The part itself, for the colours on the vessel.
        public object Tag;

        // Whether the part is on the vessel as stage `stage` burns; -1: as the vessel
        // starts. As KSP's DeltaVStageInfo.GetStageMass counts a stage: of the parts
        // decoupled at its number or never, less those it drops as it fires
        // (decoupleStage equal to its number).
        internal bool AttachedIn(int stage)
        {
            return AttachedIn(DecoupleStage, stage);
        }

        internal static bool AttachedIn(int decoupleStage, int stage)
        {
            return stage < 0 || decoupleStage < stage;
        }
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
        // A command part with seats for crew.
        public bool Crewed;
        public bool Parachute;
        public bool DockingPort;
    }

    // The sections of the vessel as parts of it (docs/development/planner.md). Each
    // decoupler and docking port cuts the part tree in two; each side is a choice, less
    // the launch clamps and the parts KSP stages away before its command part, its legs
    // and its parachutes (unless its own engines still carry them), and of what is left
    // only the parts still joined to its command part. The planner finds a crewed
    // vessel's capsule, an uncrewed vessel's one satellite and a lander by itself;
    // the rest is the player's pick. Sections lie inside one another or apart, and the vessel
    // falls into the rest and the sections by them (Split). Free of Unity, tested.
    internal static class VesselShape
    {
        internal static List<PartGroupChoice> Candidates(IList<PartFacts> parts)
        {
            List<PartGroupChoice> choices = new List<PartGroupChoice>();
            Dictionary<string, PartGroupChoice> seen = new Dictionary<string, PartGroupChoice>();
            List<int>[] children = Children(parts);
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].Separator == Separation.None) continue;
                // Of two docking ports joined in the editor, the one under the other
                // undocks: its cut alone.
                if (parts[i].Separator == Separation.DockingPort && children[i].Any(child => parts[child].Separator == Separation.DockingPort))
                    continue;
                HashSet<int> under = Subtree(children, i);
                if (parts[i].Separator == Separation.Decoupler) under.Remove(i);
                HashSet<int> over = new HashSet<int>(Enumerable.Range(0, parts.Count).Where(at => !under.Contains(at)));
                // A separator with nothing on one side, such as a docking port on a
                // pod's nose, cuts nothing off.
                if (!under.Any(at => at != i && !parts[at].Clamp) || !over.Any(at => at != i && !parts[at].Clamp)) continue;
                Add(choices, seen, parts, Joined(parts, children, Trim(parts, under)), parts[i], "under");
                Add(choices, seen, parts, Joined(parts, children, Trim(parts, over)), parts[i], "over");
            }
            choices.Sort((a, b) => a.Mass.CompareTo(b.Mass));
            return choices;
        }

        // Wings and landing gear: the vessel takes off along a runway.
        internal static bool Runway(IList<PartFacts> parts)
        {
            bool wings = false, wheels = false;
            foreach (PartFacts part in parts)
            {
                wings |= part.Lift && !part.Clamp;
                wheels |= part.Wheels && !part.Clamp;
            }
            return wings && wheels;
        }

        // The vessel has seats for crew in a command part.
        internal static bool Crewed(IList<PartFacts> parts)
        {
            foreach (PartFacts part in parts)
                if (part.Command && part.Crew > 0) return true;
            return false;
        }

        // A crewed vessel's capsule: the lightest choice with a crewed command part and
        // parachutes, else the lightest crewed one, so that a crewed lander without them
        // is not taken for it.
        internal static PartGroupChoice AutoCapsule(IList<PartGroupChoice> choices)
        {
            foreach (PartGroupChoice choice in choices)
                if (choice.Crewed && choice.Parachute) return choice;
            foreach (PartGroupChoice choice in choices)
                if (choice.Crewed) return choice;
            return null;
        }

        // An uncrewed vessel's satellite: of the choices with a command part and no
        // landing legs that leave the launch stage's engines behind, the one inside
        // no other, where there is exactly one such.
        internal static PartGroupChoice AutoSatellite(IList<PartFacts> parts, IList<PartGroupChoice> choices)
        {
            int first = FirstFiring(parts);
            List<PartGroupChoice> found = new List<PartGroupChoice>();
            foreach (PartGroupChoice choice in choices)
                if (choice.Command && !choice.Legs && !Launches(parts, choice, first)) found.Add(choice);
            List<PartGroupChoice> innermost = found.FindAll(choice =>
                !found.Exists(other => other != choice && other.Members.IsProperSubsetOf(choice.Members)));
            return innermost.Count == 1 ? innermost[0] : null;
        }

        // A lander: the lightest choice with a command part, an engine and landing
        // legs whose engines do not fire with the launch stage, so that no booster with
        // legs that lands by itself is taken for it; inside the capsule or the
        // satellite or apart from them, not one of them.
        internal static PartGroupChoice AutoLander(IList<PartFacts> parts, IList<PartGroupChoice> choices, PartGroupChoice capsule,
                                                   PartGroupChoice satellite)
        {
            int first = FirstFiring(parts);
            foreach (PartGroupChoice choice in choices)
                if (choice.Command && choice.Engine && choice.Legs && !Launches(parts, choice, first) && Fits(choice, capsule)
                    && Fits(choice, satellite))
                    return choice;
            return null;
        }

        // A lander can dock again: a docking port among its parts, and one among the
        // vessel's others.
        internal static bool CanDock(IList<PartFacts> parts, PartGroupChoice lander)
        {
            if (lander == null) return false;
            bool inside = false, outside = false;
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].Separator != Separation.DockingPort) continue;
                if (lander.Members.Contains(i)) inside = true;
                else outside = true;
            }
            return inside && outside;
        }

        // The stage KSP fires the first engines in.
        private static int FirstFiring(IList<PartFacts> parts)
        {
            int first = int.MinValue;
            foreach (PartFacts part in parts)
                if (part.Engine) first = Math.Max(first, part.ActivatesAt);
            return first;
        }

        // An engine of the choice fires with the launch stage.
        private static bool Launches(IList<PartFacts> parts, PartGroupChoice choice, int first)
        {
            foreach (int at in choice.Members)
                if (parts[at].Engine && parts[at].ActivatesAt == first) return true;
            return false;
        }

        // Two sections inside one another or apart; not overlapping.
        internal static bool Fits(PartGroupChoice a, PartGroupChoice b)
        {
            if (a == null || b == null) return true;
            if (a.Key == b.Key) return false;
            return a.Members.IsSubsetOf(b.Members) || b.Members.IsSubsetOf(a.Members) || !a.Members.Overlaps(b.Members);
        }

        // The section a section is part of: the smallest other one holding all its
        // parts; -1 for the rest.
        internal static int Parent(IList<PartGroupChoice> sections, int index)
        {
            int best = -1;
            for (int i = 0; i < sections.Count; i++)
            {
                if (i == index || sections[i] == null || sections[index] == null) continue;
                if (!sections[index].Members.IsProperSubsetOf(sections[i].Members)) continue;
                if (best < 0 || sections[i].Members.Count < sections[best].Members.Count) best = i;
            }
            return best;
        }

        // The vessel's stages shared out over the rest (group 0) and the sections
        // (group i + 1 for sections[i]), each part in the smallest section holding it,
        // by the parts each stage fires, burns and drops; the groups' masses scaled to
        // KSP's own mass of the whole vessel, launch clamps left out as KSP leaves them.
        internal static VesselSplit Split(IList<BudgetStage> stages, IList<PartFacts> parts, IList<PartGroupChoice> sections)
        {
            VesselSplit split = new VesselSplit(sections.Count + 1);
            int[] group = new int[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                int best = -1;
                for (int s = 0; s < sections.Count; s++)
                    if (sections[s] != null && sections[s].Members.Contains(i)
                        && (best < 0 || sections[s].Members.Count < sections[best].Members.Count)) best = s;
                group[i] = best + 1;
            }

            double total = 0.0;
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].Clamp) continue;
                split.Mass[group[i]] += Math.Max(0.0, parts[i].Mass);
                total += Math.Max(0.0, parts[i].Mass);
            }
            double start = stages.Count > 0 ? stages.Max(stage => stage.StartMass) : 0.0;
            if (total > 0.0 && start > 0.0)
                for (int g = 0; g < split.Groups; g++) split.Mass[g] *= start / total;

            foreach (BudgetStage stage in stages)
            {
                int number = stage.Number;
                // The stage after it (the next lower number), whose firing drops what
                // StageBudget counts as this stage's drop.
                int next = int.MinValue;
                foreach (BudgetStage other in stages)
                    if (other.Number < number) next = Math.Max(next, other.Number);
                double[] fuel = new double[split.Groups];
                double[] drop = new double[split.Groups];
                double[] fired = new double[split.Groups];
                double[] engines = new double[split.Groups];
                for (int i = 0; i < parts.Count; i++)
                {
                    PartFacts part = parts[i];
                    if (part.Clamp) continue;
                    int g = group[i];
                    double[] mass;
                    bool known = part.StageFuel.TryGetValue(number, out mass) && mass != null && mass.Length >= 2;
                    if (known) fuel[g] += Math.Max(0.0, mass[0] - mass[1]);
                    // On the vessel as this stage burns, gone as the next one does.
                    if (next != int.MinValue && part.AttachedIn(number) && !part.AttachedIn(next))
                        drop[g] += Math.Max(0.0, Turning.MassAt(part, next));
                    if (part.ActivatesAt == number)
                    {
                        fired[g] += 1.0;
                        if (part.Engine) engines[g] += 1.0;
                    }
                }
                split.Owner[number] = Largest(engines) ?? Largest(fired) ?? Largest(fuel) ?? 0;
                split.Fuel[number] = fuel;
                split.Drop[number] = drop;
            }
            return split;
        }

        private static int? Largest(double[] values)
        {
            int best = -1;
            for (int g = 0; g < values.Length; g++)
                if (values[g] > 1e-9 && (best < 0 || values[g] > values[best])) best = g;
            return best >= 0 ? (int?)best : null;
        }

        // A side less its launch clamps and the parts KSP stages away before the ones
        // that make it what it is: its command parts, landing legs and parachutes. What
        // its own engines still carry when they fire stays: a lander's drop tanks.
        private static HashSet<int> Trim(IList<PartFacts> parts, HashSet<int> side)
        {
            HashSet<int> kept = new HashSet<int>(side.Where(at => !parts[at].Clamp));
            int latest = int.MinValue;
            foreach (int at in kept)
                if (parts[at].Command || parts[at].Legs || parts[at].Parachute) latest = Math.Max(latest, parts[at].DecoupleStage);
            if (latest == int.MinValue) return kept;
            int fires = latest;
            foreach (int at in kept)
                if (parts[at].Engine && parts[at].DecoupleStage <= latest) fires = Math.Max(fires, parts[at].ActivatesAt);
            return new HashSet<int>(kept.Where(at => parts[at].DecoupleStage <= fires));
        }

        // Of a trimmed side, the parts still joined in the part tree to its heaviest
        // command part, or else to its heaviest part: what is cut off by the parts
        // taken out does not fly with it.
        private static HashSet<int> Joined(IList<PartFacts> parts, List<int>[] children, HashSet<int> side)
        {
            if (side.Count == 0) return side;
            int seed = side.OrderByDescending(at => parts[at].Command ? 1 : 0).ThenByDescending(at => parts[at].Mass).First();
            HashSet<int> joined = new HashSet<int>();
            Stack<int> open = new Stack<int>();
            open.Push(seed);
            while (open.Count > 0)
            {
                int at = open.Pop();
                if (!side.Contains(at) || !joined.Add(at)) continue;
                foreach (int child in children[at]) open.Push(child);
                if (parts[at].Parent >= 0) open.Push(parts[at].Parent);
            }
            return joined;
        }

        // The same parts cut off by several separators are one choice; a docking port
        // among them lets it come back, whatever the parts' order.
        private static void Add(List<PartGroupChoice> choices, Dictionary<string, PartGroupChoice> seen, IList<PartFacts> parts,
                                HashSet<int> side, PartFacts separator, string which)
        {
            if (side.Count == 0 || side.Count == parts.Count(part => !part.Clamp)) return;
            string members = string.Join(",", side.OrderBy(at => at).Select(at => at.ToString()).ToArray());
            PartGroupChoice known;
            if (seen.TryGetValue(members, out known))
            {
                known.DockingPort |= separator.Separator == Separation.DockingPort;
                return;
            }
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
                choice.Crewed |= part.Command && part.Crew > 0;
                choice.Parachute |= part.Parachute;
                if (named == null || Rank(part) > Rank(named) || (Rank(part) == Rank(named) && part.Mass > named.Mass)) named = part;
            }
            choice.Name = named.Title + ", " + side.Count + (side.Count == 1 ? " part" : " parts");
            choices.Add(choice);
            seen[members] = choice;
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
