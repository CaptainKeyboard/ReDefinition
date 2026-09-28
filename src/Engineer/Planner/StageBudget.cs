using System.Collections.Generic;
using System;

namespace ReDefinition.Engineer
{
    // One stage as KSP's delta-v simulation gives it (DeltaVStageInfo): the mass
    // when it starts and ends burning, and its delta-v in vacuum. A solid stage burns
    // out once lit (StageRow.Solid).
    internal sealed class BudgetStage
    {
        public int Number;
        public double StartMass;
        public double EndMass;
        public double DeltaV;
        public bool Solid;
        // Burns on the route's first step only (the air-breathing engines' part of
        // the launch); what it has not burnt by then counts no more.
        public bool FirstStepOnly;
    }

    // The player's choices for the stages of one leg, by stage number: kept for a
    // later leg, or dropped once the leg is over.
    internal sealed class StagePlan
    {
        public readonly HashSet<int> Save = new HashSet<int>();
        public readonly HashSet<int> DropAfter = new HashSet<int>();

        public bool Empty
        {
            get { return Save.Count == 0 && DropAfter.Count == 0; }
        }

        public StagePlan Copy()
        {
            StagePlan copy = new StagePlan();
            copy.Save.UnionWith(Save);
            copy.DropAfter.UnionWith(DropAfter);
            return copy;
        }
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

        internal VesselSplit(int groups)
        {
            Mass = new double[Math.Max(1, groups)];
        }

        public int Groups
        {
            get { return Mass.Length; }
        }

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

        private static int First(List<BudgetStage> order, int top)
        {
            if (top < 0) return order.Count;
            for (int i = 0; i < order.Count; i++)
                if (order[i].Number <= top) return i;
            return order.Count;
        }
    }

    // A group gone for good as a step starts: it stays where it is as a vessel of its
    // own (a lander left behind) or is only parts that fall away; it was with what
    // flies, so that what it held goes with it, or waited elsewhere.
    internal sealed class GoneGroup
    {
        public int Group;
        public bool Stays;
        public bool Waiting;
    }

    // One step as the budget sees it: what it needs with no margin, the groups that
    // fly it, the groups gone for good as it starts, the group refilled from the rest
    // once it is over, and the groups left at its destination once it is over.
    internal sealed class BudgetStep
    {
        public double Need;
        public bool[] Side;
        public readonly List<GoneGroup> Gone = new List<GoneGroup>();
        public int RefuelGroup = -1;
        // The stage of the rest that refuels it first, by its number; -1 for KSP's order.
        public int RefuelFrom = -1;
        public readonly List<int> Deploy = new List<int>();
    }

    // Which of the vessel's stages pays for which step of the route
    // (docs/development/planner.md). Worked out on the mass the stages push, so
    // that a stage burning over several steps, or a lander that lands, returns and
    // is carried on to the next body, counts with what it weighs at that moment.
    //
    // Each stage burns its propellant (start mass less end mass) at the exhaust
    // velocity its delta-v gives (Tsiolkovsky backwards). The vessel falls into the
    // rest and its sections (VesselSplit); each stage belongs to the group whose parts
    // it fires. What flies a step burns its own groups' stages in KSP's order, the
    // highest number first, until empty; then what KSP drops before its next stage
    // fires goes. A group gone for good takes its stages with what they hold; a group
    // deployed stays at the step's destination with what it weighs.
    //
    // The player can save a stage on a step: the step stops before it, and before
    // the stages after it, since KSP cannot fire those first. The player can drop a
    // stage after a step: at the step's end it goes with what it still holds, and so
    // do the stages KSP fires before it on what flies the step.
    //
    // After a step marked for it, a group, docked again, is refilled from the rest:
    // each of its stages up to what it held at the start, from the rest's stages in
    // KSP's order. Free of Unity, tested.
    internal static class StageBudget
    {
        internal sealed class StageUse
        {
            public int Stage;
            public int Leg;
            public double DeltaV;
            // What the stage pushes as it starts this part of the step, and as it ends
            // it, in tonnes.
            public double Mass;
            public double EndMass;
        }

        // A stage of what flies a step, with propellant as the step starts.
        internal sealed class StepStage
        {
            public int Stage;
            // What it gives to the step, and what it still gives once the step is
            // over, or what it held where it was dropped then.
            public double Given;
            public double LeftAfter;
            public bool Dropped;
            // A stage follows it on what flies the step, so that it can be dropped.
            public bool CanDrop;
        }

        internal sealed class Result
        {
            public readonly List<StageUse> Uses = new List<StageUse>();
            // Per step: what it needs with the margin, and what no stage covers.
            public double[] Needed;
            public double[] Short;
            // Per step: the stages what flies it has with propellant as it starts,
            // in KSP's order.
            public List<StepStage>[] Stages;
            // Per stage, by its number, as delta-v: what it has at the end of the
            // route, or what it held when it was dropped or left behind.
            public readonly Dictionary<int, double> Left = new Dictionary<int, double>();
            public readonly Dictionary<int, double> Dropped = new Dictionary<int, double>();
            public readonly HashSet<int> LeftBehind = new HashSet<int>();
            // Per stage dropped or left behind: the last step it was part of, -1 for
            // none.
            public readonly Dictionary<int, int> GoneAfter = new Dictionary<int, int>();
            // Per step: the propellant moved into a docked group after it, and the mass
            // deployed after it, in tonnes.
            public double[] Refuelled;
            public double[] Payload;
            // Per step: the delta-v what flew it still has once it is over.
            public double[] LeftAfter;
            // Per step: the rest's stages that still hold propellant as it ends, by their
            // numbers, for a refill to come from.
            public List<int>[] Tankers;

            public double TotalShort
            {
                get
                {
                    double total = 0.0;
                    foreach (double value in Short) total += value;
                    return total;
                }
            }

            public double TotalLeft
            {
                get
                {
                    double total = 0.0;
                    foreach (double value in Left.Values) total += value;
                    return total;
                }
            }
        }

        private const double Tiny = 1e-9;

        // `steps`: what each step needs and who flies it; `margin`: 0.1 for ten per
        // cent more; `plans`: per step, the stages saved on it and dropped after it;
        // `split`: the rest and the sections.
        internal static Result Allocate(IList<BudgetStage> stages, IList<BudgetStep> steps, double margin, IList<StagePlan> plans,
                                        VesselSplit split)
        {
            return new Run(stages, plans, split).Allocate(steps, margin);
        }

        private sealed class Run
        {
            private readonly List<BudgetStage> order;
            private readonly IList<StagePlan> plans;
            private readonly int count;
            private readonly int groups;
            private readonly double[,] fuel;
            private readonly double[,] capacity;
            private readonly double[,] drop;
            private readonly double[] exhaust;
            private readonly bool[] solid;
            private readonly bool[] firstOnly;
            private readonly int[] owner;
            // Fired past: what flies no longer comes back to it.
            private readonly bool[] done;
            private readonly double[] mass;
            private readonly bool[] alive;
            // By stage number: what a deployed stage still gives as it stays.
            private readonly Dictionary<int, double> stayed = new Dictionary<int, double>();
            // Per group, the last step it flew, -1 for none.
            private readonly int[] lastFlown;
            private readonly bool restStaysForLander;
            private IList<BudgetStep> steps;
            // The step worked out now, and what flies it.
            private int leg;
            private bool[] flying;
            private Result result;

            internal Run(IList<BudgetStage> stages, IList<StagePlan> plans, VesselSplit split)
            {
                order = new List<BudgetStage>(stages);
                order.Sort((a, b) => b.Number.CompareTo(a.Number));
                this.plans = plans;
                if (split == null) split = VesselSplit.ByStages(stages, -1, -1);
                count = order.Count;
                groups = split.Groups;
                fuel = new double[count, groups];
                capacity = new double[count, groups];
                drop = new double[count, groups];
                exhaust = new double[count];
                solid = new bool[count];
                firstOnly = new bool[count];
                owner = new int[count];
                done = new bool[count];
                mass = new double[groups];
                alive = new bool[groups];
                lastFlown = new int[groups];
                restStaysForLander = split.RestStaysForLander;
                for (int g = 0; g < groups; g++)
                {
                    mass[g] = Math.Max(0.0, split.Mass[g]);
                    alive[g] = true;
                    lastFlown[g] = -1;
                }

                for (int i = 0; i < count; i++)
                {
                    int number = order[i].Number;
                    solid[i] = order[i].Solid;
                    firstOnly[i] = order[i].FirstStepOnly;
                    int group;
                    owner[i] = split.Owner.TryGetValue(number, out group) && group >= 0 && group < groups ? group : 0;
                    double start = Math.Max(0.0, order[i].StartMass);
                    double end = Math.Max(0.0, Math.Min(order[i].EndMass, start));
                    double burns = 0.0;
                    if (order[i].DeltaV > 0.5 && end > 0.0 && start > end)
                    {
                        burns = start - end;
                        exhaust[i] = order[i].DeltaV / Math.Log(start / end);
                    }
                    // What goes when the next stage fires: all but the propellant
                    // burnt and what the next stage starts with.
                    double drops = i + 1 < count ? Math.Max(0.0, start - burns - Math.Max(0.0, order[i + 1].StartMass)) : 0.0;
                    double[] fuelShares = Shares(split, split.Fuel, number, owner[i]);
                    double[] dropShares = Shares(split, split.Drop, number, owner[i]);
                    for (int g = 0; g < groups; g++)
                    {
                        fuel[i, g] = burns * fuelShares[g];
                        capacity[i, g] = fuel[i, g];
                        drop[i, g] = drops * dropShares[g];
                    }
                }
            }

            private double[] Shares(VesselSplit split, Dictionary<int, double[]> table, int number, int fallback)
            {
                double[] shares;
                if (!table.TryGetValue(number, out shares) || shares == null) return split.Unit(fallback);
                double sum = 0.0;
                for (int g = 0; g < groups && g < shares.Length; g++) sum += Math.Max(0.0, shares[g]);
                if (sum <= Tiny) return split.Unit(fallback);
                double[] result = new double[groups];
                for (int g = 0; g < groups && g < shares.Length; g++) result[g] = Math.Max(0.0, shares[g]) / sum;
                return result;
            }

            internal Result Allocate(IList<BudgetStep> steps, double margin)
            {
                this.steps = steps;
                result = new Result
                {
                    Needed = new double[steps.Count], Short = new double[steps.Count], Refuelled = new double[steps.Count],
                    Payload = new double[steps.Count], Stages = new List<StepStage>[steps.Count], LeftAfter = new double[steps.Count],
                    Tankers = new List<int>[steps.Count],
                };
                flying = Everything();
                for (leg = 0; leg < steps.Count; leg++)
                {
                    BudgetStep step = steps[leg];
                    if (leg == 1) Expire();
                    double need = Math.Max(0.0, step.Need) * (1.0 + Math.Max(0.0, margin));
                    result.Needed[leg] = need;
                    foreach (GoneGroup gone in step.Gone) Leave(gone);
                    bool[] side = new bool[groups];
                    for (int g = 0; g < groups; g++) side[g] = alive[g] && step.Side != null && g < step.Side.Length && step.Side[g];
                    flying = side;
                    for (int g = 0; g < groups; g++)
                        if (side[g]) lastFlown[g] = leg;
                    List<StepStage> present = Present(side);
                    Burn(side, ref need);
                    result.Short[leg] = need > 1e-6 ? need : 0.0;
                    result.Tankers[leg] = new List<int>();
                    for (int i = 0; i < count; i++)
                        if (!done[i] && alive[0] && fuel[i, 0] > Tiny) result.Tankers[leg].Add(order[i].Number);
                    if (step.RefuelGroup > 0) result.Refuelled[leg] = Refuel(step.RefuelGroup, step.RefuelFrom);
                    DropAfter(present, side);
                    // What flew the step stays there as a whole, a satellite flying itself:
                    // what it has left is what it holds as it stays.
                    bool staysWhole = step.Deploy.Count > 0;
                    for (int g = 0; g < groups; g++)
                        if (side[g] && !step.Deploy.Contains(g)) staysWhole = false;
                    if (staysWhole) result.LeftAfter[leg] = SideLeft(side);
                    foreach (int group in step.Deploy) result.Payload[leg] += Deploy(group);
                    Close(present);
                    if (!staysWhole) result.LeftAfter[leg] = SideLeft(side);
                    result.Stages[leg] = present;
                }
                Finish();
                return result;
            }

            // A stage for the first step only is done once that step is over: what it
            // did not burn stays as dead weight, what KSP drops after it goes.
            private void Expire()
            {
                for (int i = 0; i < count; i++)
                {
                    if (!firstOnly[i] || done[i]) continue;
                    for (int g = 0; g < groups; g++)
                    {
                        fuel[i, g] = 0.0;
                        mass[g] = Math.Max(0.0, mass[g] - drop[i, g]);
                        drop[i, g] = 0.0;
                    }
                    done[i] = true;
                }
            }

            private bool[] Everything()
            {
                bool[] side = new bool[groups];
                for (int g = 0; g < groups; g++) side[g] = alive[g];
                return side;
            }

            // ----------------------------------------------------------- sides

            private double MassOf(bool[] side)
            {
                double total = 0.0;
                for (int g = 0; g < groups; g++)
                    if (side[g]) total += mass[g];
                return total;
            }

            private double FuelOf(int at, bool[] side)
            {
                double total = 0.0;
                for (int g = 0; g < groups; g++)
                    if (side[g]) total += fuel[at, g];
                return total;
            }

            // The side's stage KSP fires next, from `after` on; -1 for none.
            private int NextOf(bool[] side, int after)
            {
                for (int i = after + 1; i < count; i++)
                    if (!done[i] && side[owner[i]]) return i;
                return -1;
            }

            private int FirstOf(bool[] side)
            {
                return NextOf(side, -1);
            }

            // A later step flies the group without the rest: it comes back to it.
            private bool AloneAhead(int group)
            {
                for (int k = leg + 1; k < steps.Count; k++)
                {
                    bool[] side = steps[k].Side;
                    if (side != null && group < side.Length && side[group] && !side[0]) return true;
                }
                return false;
            }

            // ----------------------------------------------------------- burning

            private StagePlan PlanOf(int at)
            {
                return plans != null && at >= 0 && at < plans.Count ? plans[at] : null;
            }

            private bool Saved(int at)
            {
                StagePlan plan = PlanOf(leg);
                return plan != null && plan.Save.Contains(order[at].Number);
            }

            // True where a stage saved on this step stopped the burn.
            private bool Burn(bool[] side, ref double need)
            {
                int at = FirstOf(side);
                while (need > 1e-6 && at >= 0)
                {
                    if (FuelOf(at, side) > Tiny && Saved(at)) return true;
                    BurnOne(at, side, ref need);
                    if (FuelOf(at, side) > Tiny && exhaust[at] > 0.0) return false;
                    int next = NextOf(side, at);
                    if (next < 0) return false;
                    // KSP cannot fire a saved stage's successors: it stays before it.
                    if (FuelOf(next, side) > Tiny && Saved(next)) return true;
                    Pass(at, next, side, false);
                    at = next;
                }
                return false;
            }

            private void BurnOne(int at, bool[] side, ref double need)
            {
                double available = FuelOf(at, side);
                if (available <= Tiny || exhaust[at] <= 0.0) return;
                double pushed = MassOf(side);
                double full = DeltaV(at, available, pushed);
                // A solid stage, once lit, gives all it has to this step: what the
                // step does not need is spent on it all the same.
                double take = solid[at] ? full : Math.Min(need, full);
                double burnt = take >= full - 1e-9 ? available : pushed * (1.0 - Math.Exp(-take / exhaust[at]));
                result.Uses.Add(new StageUse { Stage = order[at].Number, Leg = leg, DeltaV = take, Mass = pushed, EndMass = pushed - burnt });
                for (int g = 0; g < groups; g++)
                {
                    if (!side[g] || fuel[at, g] <= 0.0) continue;
                    double part = take >= full - 1e-9 ? fuel[at, g] : burnt * fuel[at, g] / available;
                    fuel[at, g] = Math.Max(0.0, fuel[at, g] - part);
                    mass[g] = Math.Max(0.0, mass[g] - part);
                }
                need = Math.Max(0.0, need - take);
            }

            // The next stage fires: what KSP drops after `at` goes from the side. In the
            // stage-number model, the rest's part of it stays where another group's
            // stages come next and that group flies alone later, as it returns to it.
            private void Pass(int at, int next, bool[] side, bool forced)
            {
                done[at] = true;
                for (int g = 0; g < groups; g++)
                {
                    if (!side[g] || drop[at, g] <= 0.0) continue;
                    bool restStays = !forced && restStaysForLander && g == 0 && owner[next] != 0 && AloneAhead(owner[next]);
                    if (restStays) continue;
                    mass[g] = Math.Max(0.0, mass[g] - drop[at, g]);
                    drop[at, g] = 0.0;
                }
            }

            private double DeltaV(int at, double available, double pushed)
            {
                if (available <= Tiny || exhaust[at] <= 0.0 || pushed <= available) return 0.0;
                return exhaust[at] * Math.Log(pushed / (pushed - available));
            }

            // --------------------------------------------------------- the step

            // The stages of what flies the step that still hold propellant.
            private List<StepStage> Present(bool[] side)
            {
                List<StepStage> list = new List<StepStage>();
                for (int i = FirstOf(side); i >= 0; i = NextOf(side, i))
                    if (FuelOf(i, side) > Tiny && exhaust[i] > 0.0)
                        list.Add(new StepStage { Stage = order[i].Number, CanDrop = NextOf(side, i) >= 0 });
                return list;
            }

            // Once the step is over: what each of its stages gave and still gives.
            private void Close(List<StepStage> present)
            {
                Dictionary<int, double> left = Remaining();
                foreach (StepStage stage in present)
                {
                    foreach (StageUse use in result.Uses)
                        if (use.Leg == leg && use.Stage == stage.Stage) stage.Given += use.DeltaV;
                    double value;
                    if (stage.Dropped && result.Dropped.TryGetValue(stage.Stage, out value)) stage.LeftAfter = value;
                    else if (stayed.TryGetValue(stage.Stage, out value)) stage.LeftAfter = value;
                    else if (left.TryGetValue(stage.Stage, out value)) stage.LeftAfter = value;
                }
            }

            // The stages the player drops after this step, with those KSP fires before
            // them on what flies it.
            private void DropAfter(List<StepStage> present, bool[] side)
            {
                StagePlan plan = PlanOf(leg);
                if (plan == null || plan.DropAfter.Count == 0) return;
                foreach (StepStage stage in present)
                {
                    if (!stage.CanDrop || !plan.DropAfter.Contains(stage.Stage)) continue;
                    int target = order.FindIndex(row => row.Number == stage.Stage);
                    for (int at = FirstOf(side); at >= 0 && at <= target; at = FirstOf(side))
                    {
                        Jettison(at, side);
                        int next = NextOf(side, at);
                        if (next < 0) break;
                        Pass(at, next, side, true);
                    }
                }
                foreach (StepStage stage in present)
                {
                    int gone;
                    stage.Dropped = result.GoneAfter.TryGetValue(stage.Stage, out gone) && gone == leg
                                    && result.Dropped.ContainsKey(stage.Stage) && !result.LeftBehind.Contains(stage.Stage);
                }
            }

            private void Jettison(int at, bool[] side)
            {
                double available = FuelOf(at, side);
                if (available <= Tiny) return;
                result.Dropped[order[at].Number] = DeltaV(at, available, MassOf(side));
                result.GoneAfter[order[at].Number] = leg;
                for (int g = 0; g < groups; g++)
                {
                    if (!side[g]) continue;
                    mass[g] = Math.Max(0.0, mass[g] - fuel[at, g]);
                    fuel[at, g] = 0.0;
                }
            }

            // A docked group filled up from the rest's tanks, as far as they hold it: the
            // stage the player names first, then the others in KSP's order.
            private double Refuel(int into, int first)
            {
                if (into <= 0 || into >= groups || !alive[into] || !alive[0]) return 0.0;
                List<int> sources = new List<int>();
                int named = order.FindIndex(row => row.Number == first);
                if (named >= 0) sources.Add(named);
                for (int i = 0; i < count; i++)
                    if (i != named) sources.Add(i);
                double moved = 0.0;
                int from = 0;
                for (int i = 0; i < count; i++)
                {
                    // A stage already fired past is gone.
                    if (done[i]) continue;
                    double wanted = capacity[i, into] - fuel[i, into];
                    while (wanted > Tiny && from < sources.Count)
                    {
                        int source = sources[from];
                        double take = Math.Min(wanted, fuel[source, 0]);
                        fuel[source, 0] -= take;
                        fuel[i, into] += take;
                        wanted -= take;
                        moved += take;
                        if (fuel[source, 0] <= Tiny) from++;
                    }
                }
                mass[0] = Math.Max(0.0, mass[0] - moved);
                mass[into] += moved;
                return moved;
            }

            // --------------------------------------------------------- leaving

            // A group gone for good as the step starts: its stages go with what they
            // hold. What stays is left behind, the stages it owns with it; what was with
            // what flew is dropped with the delta-v it held.
            private void Leave(GoneGroup gone)
            {
                int group = gone.Group;
                if (group < 0 || group >= groups || !alive[group]) return;
                // What stays is a vessel of its own; what falls away pushed with what flew.
                bool[] with = !gone.Stays && !gone.Waiting && flying[group] ? flying : Single(group);
                double pushed = MassOf(with);
                for (int i = 0; i < count; i++)
                {
                    bool owned = owner[i] == group && !done[i];
                    bool holds = fuel[i, group] > Tiny;
                    if (!owned && !holds) continue;
                    if (gone.Stays || gone.Waiting) result.LeftBehind.Add(order[i].Number);
                    if (!gone.Waiting && holds) result.Dropped[order[i].Number] = DeltaV(i, fuel[i, group], pushed);
                    // Where it stays: where it last flew to.
                    result.GoneAfter[order[i].Number] = gone.Waiting || gone.Stays ? lastFlown[group] : leg - 1;
                    pushed -= fuel[i, group];
                    fuel[i, group] = 0.0;
                    if (owned) done[i] = true;
                }
                mass[group] = 0.0;
                alive[group] = false;
            }

            // A group left at the step's destination, with what it weighs.
            private double Deploy(int group)
            {
                if (group <= 0 || group >= groups || !alive[group]) return 0.0;
                double weight = mass[group];
                double pushed = weight;
                for (int i = 0; i < count; i++)
                {
                    if (owner[i] != group && fuel[i, group] <= Tiny) continue;
                    if (fuel[i, group] > Tiny)
                    {
                        stayed[order[i].Number] = DeltaV(i, fuel[i, group], pushed);
                        pushed -= fuel[i, group];
                    }
                    if (owner[i] == group)
                    {
                        result.LeftBehind.Add(order[i].Number);
                        result.GoneAfter[order[i].Number] = leg;
                        done[i] = true;
                    }
                    fuel[i, group] = 0.0;
                }
                mass[group] = 0.0;
                alive[group] = false;
                return weight;
            }

            private bool[] Single(int group)
            {
                bool[] side = new bool[groups];
                side[group] = true;
                return side;
            }

            // ------------------------------------------------------ what is left

            // What each stage still has, burning on in order from where the route
            // ends. A stage already burnt out has nothing.
            private void Finish()
            {
                for (int i = 0; i < count; i++)
                    if (!result.Dropped.ContainsKey(order[i].Number)) result.Left[order[i].Number] = 0.0;
                foreach (KeyValuePair<int, double> pair in Remaining()) result.Left[pair.Key] = pair.Value;
            }

            // What each stage still there gives, burning on in order from here: what
            // flies, and what waits elsewhere, each on its own.
            private Dictionary<int, double> Remaining()
            {
                Dictionary<int, double> left = new Dictionary<int, double>();
                bool[] waiting = new bool[groups];
                bool any = false;
                for (int g = 0; g < groups; g++)
                {
                    waiting[g] = alive[g] && !flying[g];
                    any |= waiting[g];
                }
                BurnOn(left, flying);
                if (any) BurnOn(left, waiting);
                return left;
            }

            // What the side still gives, burning on in order.
            private double SideLeft(bool[] side)
            {
                Dictionary<int, double> left = new Dictionary<int, double>();
                BurnOn(left, side);
                double total = 0.0;
                foreach (double value in left.Values) total += value;
                return total;
            }

            private void BurnOn(Dictionary<int, double> left, bool[] side)
            {
                double pushed = MassOf(side);
                for (int i = FirstOf(side); i >= 0; i = NextOf(side, i))
                {
                    double available = FuelOf(i, side);
                    if (!result.Dropped.ContainsKey(order[i].Number) || available > Tiny)
                        left[order[i].Number] = DeltaV(i, available, pushed);
                    pushed -= available;
                    if (NextOf(side, i) < 0) break;
                    for (int g = 0; g < groups; g++)
                        if (side[g]) pushed -= drop[i, g];
                }
            }
        }
    }
}
