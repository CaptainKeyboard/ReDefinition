using System.Collections.Generic;
using System;

namespace ReDefinition.Engineer
{
    // Who flies a leg where the vessel carries a lander or a capsule: the whole
    // vessel, the lander alone while the rest waits in orbit, the rest without the
    // lander, which stays behind, or the capsule alone, everything else left.
    internal enum LegMode
    {
        Whole,
        Lander,
        WithoutLander,
        Capsule,
    }

    // One stage as KSP's delta-v simulation gives it (DeltaVStageInfo): the mass
    // when it starts and ends burning, and its delta-v in vacuum.
    internal sealed class BudgetStage
    {
        public int Number;
        public double StartMass;
        public double EndMass;
        public double DeltaV;
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

    // The parts of the vessel that can fly apart: the lander, the capsule, and the
    // rest, everything else. The capsule is part of the lander or of the rest.
    internal enum PartGroup
    {
        Rest,
        Lander,
        Capsule,
    }

    // How the vessel's mass and stages fall into the rest, the lander and the
    // capsule (StageBudget). Per stage, by its number: the group whose parts it fires,
    // and how the propellant it burns and the mass KSP drops after it share out over
    // the groups. Built from the parts (VesselShape.Split), or from stage numbers,
    // where the lander and the capsule are each a stage and every stage after it.
    internal sealed class VesselSplit
    {
        internal const int Groups = 3;

        public bool HasLander;
        public bool HasCapsule;
        public bool CapsuleInLander;
        // The rest's part of what KSP drops as the whole vessel starts the lander's
        // stages is the rest itself, and stays while the lander is to come back
        // (ByStages). From the parts, it is what KSP drops there, and goes.
        public bool RestStaysForLander;
        // Per group, in tonnes, as the vessel starts.
        public readonly double[] Mass = new double[Groups];
        // By stage number, per group; shares, summed to one by StageBudget.
        public readonly Dictionary<int, double[]> Fuel = new Dictionary<int, double[]>();
        public readonly Dictionary<int, double[]> Drop = new Dictionary<int, double[]>();
        public readonly Dictionary<int, PartGroup> Owner = new Dictionary<int, PartGroup>();

        // The lander the stages numbered up to `lander`, the capsule those up to
        // `capsule`, -1 for none. A lander needs something to leave behind, and
        // something to be; a capsule inside the lander, or with no lander.
        internal static VesselSplit ByStages(IList<BudgetStage> stages, int lander, int capsule)
        {
            List<BudgetStage> order = new List<BudgetStage>(stages);
            order.Sort((a, b) => b.Number.CompareTo(a.Number));
            int count = order.Count;
            int firstLander = First(order, lander);
            int firstCapsule = First(order, capsule);
            VesselSplit split = new VesselSplit
            {
                HasLander = firstLander > 0 && firstLander < count, RestStaysForLander = true,
            };
            split.HasCapsule = firstCapsule > 0 && firstCapsule < count && (!split.HasLander || firstCapsule >= firstLander);
            split.CapsuleInLander = split.HasLander && split.HasCapsule;
            for (int i = 0; i < count; i++)
            {
                PartGroup owner = split.HasCapsule && i >= firstCapsule ? PartGroup.Capsule
                    : split.HasLander && i >= firstLander ? PartGroup.Lander : PartGroup.Rest;
                split.Owner[order[i].Number] = owner;
                split.Fuel[order[i].Number] = Unit(owner);
                split.Drop[order[i].Number] = Unit(owner);
            }
            double start = count > 0 ? Math.Max(0.0, order[0].StartMass) : 0.0;
            double capsuleMass = split.HasCapsule ? Math.Max(0.0, order[firstCapsule].StartMass) : 0.0;
            double landerMass = split.HasLander ? Math.Max(0.0, order[firstLander].StartMass) - capsuleMass : 0.0;
            split.Mass[(int)PartGroup.Capsule] = capsuleMass;
            split.Mass[(int)PartGroup.Lander] = Math.Max(0.0, landerMass);
            split.Mass[(int)PartGroup.Rest] = Math.Max(0.0, start - capsuleMass - Math.Max(0.0, landerMass));
            return split;
        }

        private static int First(List<BudgetStage> order, int top)
        {
            if (top < 0) return order.Count;
            for (int i = 0; i < order.Count; i++)
                if (order[i].Number <= top) return i;
            return order.Count;
        }

        internal static double[] Unit(PartGroup group)
        {
            double[] shares = new double[Groups];
            shares[(int)group] = 1.0;
            return shares;
        }
    }

    // Which of the vessel's stages pays for which leg of the route
    // (docs/development/planner.md). Worked out on the mass the stages push, so
    // that a stage burning over several legs, or a lander that lands, returns and
    // is carried on to the next body, counts with what it weighs at that moment.
    //
    // Each stage burns its propellant (start mass less end mass) at the exhaust
    // velocity its delta-v gives (Tsiolkovsky backwards). The vessel falls into the
    // rest, the lander and the capsule (VesselSplit); each stage belongs to the one
    // whose parts it fires. What flies a leg burns its own stages in KSP's order, the
    // highest number first, until empty; then what KSP drops before its next stage
    // fires goes. The whole vessel fires every stage in that order. The rest's parts
    // that the whole vessel would drop as it starts the lander's stages stay while
    // a leg of the lander alone is ahead: the lander returns to them.
    //
    // The player can save a stage on a leg: the leg stops before it, and before the
    // stages after it, since KSP cannot fire those first. The player can drop a
    // stage after a leg: at the leg's end it goes with what it still holds, and so do
    // the stages KSP fires before it on what flies the leg.
    //
    // The first leg of the capsule leaves everything else where it is, with what it
    // holds, and from then on only the capsule's stages burn, pushing the capsule.
    //
    // After a leg marked for it, a payload stays behind: what KSP separates when
    // the next stage without delta-v after the one burning fires, while that one
    // burns on with what it holds.
    //
    // After a leg marked for it, the lander, docked again, is refilled from the
    // rest: each of its stages up to what it held at the start, from the rest's
    // stages in KSP's order. Free of Unity, tested.
    internal static class StageBudget
    {
        internal sealed class StageUse
        {
            public int Stage;
            public int Leg;
            public double DeltaV;
            // What the stage pushes as it starts this part of the leg, in tonnes.
            public double Mass;
        }

        // A stage of what flies a leg, with propellant as the leg starts.
        internal sealed class StepStage
        {
            public int Stage;
            // What it gives to the leg, and what it still gives once the leg is
            // over, or what it held where it was dropped then.
            public double Given;
            public double LeftAfter;
            public bool Dropped;
            // A stage follows it on what flies the leg, so that it can be dropped.
            public bool CanDrop;
        }

        internal sealed class Result
        {
            public readonly List<StageUse> Uses = new List<StageUse>();
            // Per leg: what it needs with the margin, and what no stage covers.
            public double[] Needed;
            public double[] Short;
            // Per leg: the stages what flies it has with propellant as it starts,
            // in KSP's order.
            public List<StepStage>[] Stages;
            // Per stage, by its number, as delta-v: what it has at the end of the
            // route, or what it held when it was dropped or left behind.
            public readonly Dictionary<int, double> Left = new Dictionary<int, double>();
            public readonly Dictionary<int, double> Dropped = new Dictionary<int, double>();
            public readonly HashSet<int> LeftBehind = new HashSet<int>();
            // Per stage dropped or left behind: the last leg it was part of, -1 for
            // none.
            public readonly Dictionary<int, int> GoneAfter = new Dictionary<int, int>();
            // Per leg: the propellant moved into the lander after it, and the payload
            // left behind after it, in tonnes.
            public double[] Refuelled;
            public double[] Payload;

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
        private const int Groups = VesselSplit.Groups;

        // `lander` and `capsule`: each a stage and every stage after it, -1 for none
        // (VesselSplit.ByStages).
        internal static Result Allocate(IList<BudgetStage> stages, IList<double> legs, double margin,
                                        IList<StagePlan> plans, int lander, IList<LegMode> modes,
                                        IList<bool> refuelAfter = null, IList<bool> payloadAfter = null, int capsule = -1)
        {
            return Allocate(stages, legs, margin, plans, VesselSplit.ByStages(stages, lander, capsule), modes, refuelAfter,
                payloadAfter);
        }

        // `legs`: what each leg needs; `margin`: 0.1 for ten per cent more;
        // `plans`: per leg, the stages saved on it and dropped after it; `split`: the
        // rest, the lander and the capsule; `modes`: who flies each leg, Whole where
        // it is missing. `refuelAfter`: per leg, whether the lander is refilled once
        // it is over; `payloadAfter`: whether a payload stays behind once it is over.
        internal static Result Allocate(IList<BudgetStage> stages, IList<double> legs, double margin,
                                        IList<StagePlan> plans, VesselSplit split, IList<LegMode> modes,
                                        IList<bool> refuelAfter, IList<bool> payloadAfter)
        {
            return new Run(stages, plans, split).Allocate(legs, margin, modes, refuelAfter, payloadAfter);
        }

        private sealed class Run
        {
            private readonly List<BudgetStage> order;
            private readonly IList<StagePlan> plans;
            private readonly int count;
            private readonly double[,] fuel;
            private readonly double[,] capacity;
            private readonly double[,] drop;
            private readonly double[] exhaust;
            private readonly PartGroup[] owner;
            // Fired past: what flies no longer comes back to it.
            private readonly bool[] done;
            private readonly double[] mass = new double[Groups];

            private readonly bool hasLander;
            private readonly bool hasCapsule;
            private readonly bool capsuleInLander;
            private readonly bool restStaysForLander;
            private bool landerGone;
            private bool capsuleOn;
            // The lander flew away and has not docked again.
            private bool apart;
            private int lastAlone = -1;
            // The leg worked out now.
            private int leg;
            private Result result;

            internal Run(IList<BudgetStage> stages, IList<StagePlan> plans, VesselSplit split)
            {
                order = new List<BudgetStage>(stages);
                order.Sort((a, b) => b.Number.CompareTo(a.Number));
                this.plans = plans;
                count = order.Count;
                fuel = new double[count, Groups];
                capacity = new double[count, Groups];
                drop = new double[count, Groups];
                exhaust = new double[count];
                owner = new PartGroup[count];
                done = new bool[count];
                if (split == null) split = VesselSplit.ByStages(stages, -1, -1);
                hasLander = split.HasLander;
                hasCapsule = split.HasCapsule;
                capsuleInLander = split.CapsuleInLander && hasLander;
                restStaysForLander = split.RestStaysForLander;
                for (int g = 0; g < Groups; g++) mass[g] = Math.Max(0.0, split.Mass[g]);

                for (int i = 0; i < count; i++)
                {
                    int number = order[i].Number;
                    PartGroup group;
                    owner[i] = split.Owner.TryGetValue(number, out group) ? group : PartGroup.Rest;
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
                    double[] fuelShares = Shares(split.Fuel, number, owner[i]);
                    double[] dropShares = Shares(split.Drop, number, owner[i]);
                    for (int g = 0; g < Groups; g++)
                    {
                        fuel[i, g] = burns * fuelShares[g];
                        capacity[i, g] = fuel[i, g];
                        drop[i, g] = drops * dropShares[g];
                    }
                }
            }

            private static double[] Shares(Dictionary<int, double[]> table, int number, PartGroup fallback)
            {
                double[] shares;
                if (!table.TryGetValue(number, out shares) || shares == null) return VesselSplit.Unit(fallback);
                double sum = 0.0;
                for (int g = 0; g < Groups && g < shares.Length; g++) sum += Math.Max(0.0, shares[g]);
                if (sum <= Tiny) return VesselSplit.Unit(fallback);
                double[] result = new double[Groups];
                for (int g = 0; g < Groups && g < shares.Length; g++) result[g] = Math.Max(0.0, shares[g]) / sum;
                return result;
            }

            internal Result Allocate(IList<double> legs, double margin, IList<LegMode> modes, IList<bool> refuelAfter,
                                     IList<bool> payloadAfter)
            {
                result = new Result
                {
                    Needed = new double[legs.Count], Short = new double[legs.Count], Refuelled = new double[legs.Count],
                    Payload = new double[legs.Count], Stages = new List<StepStage>[legs.Count],
                };
                for (int at = 0; at < legs.Count; at++)
                    if (ModeOf(modes, at) == LegMode.Lander) lastAlone = at;

                for (leg = 0; leg < legs.Count; leg++)
                {
                    double need = Math.Max(0.0, legs[leg]) * (1.0 + Math.Max(0.0, margin));
                    result.Needed[leg] = need;
                    LegMode mode = ModeOf(modes, leg);
                    apart = hasLander && !landerGone && !capsuleOn && (mode == LegMode.Lander || (apart && mode != LegMode.Whole));

                    if (mode == LegMode.WithoutLander) LeaveLander();
                    if (mode == LegMode.Capsule) LeaveForCapsule();
                    bool[] side = Side(mode);
                    List<StepStage> present = Present(side);
                    if (mode != LegMode.Lander || !landerGone) Burn(side, ref need);
                    result.Short[leg] = need > 1e-6 ? need : 0.0;
                    if (refuelAfter != null && leg < refuelAfter.Count && refuelAfter[leg]) result.Refuelled[leg] = Refuel();
                    if (payloadAfter != null && leg < payloadAfter.Count && payloadAfter[leg]) result.Payload[leg] = LeavePayload(side);
                    DropAfter(present, side);
                    Close(present);
                    result.Stages[leg] = present;
                }
                Finish();
                return result;
            }

            private LegMode ModeOf(IList<LegMode> modes, int at)
            {
                if (modes == null || at >= modes.Count) return LegMode.Whole;
                LegMode mode = modes[at];
                if (mode == LegMode.Capsule) return hasCapsule ? mode : LegMode.Whole;
                return hasLander ? mode : LegMode.Whole;
            }

            // ------------------------------------------------------------ sides

            // The groups that fly a leg of this mode.
            private bool[] Side(LegMode mode)
            {
                bool[] side = new bool[Groups];
                if (capsuleOn)
                {
                    side[(int)PartGroup.Capsule] = true;
                    return side;
                }
                bool landerOn = hasLander && !landerGone;
                // The lander alone; the rest without it; or everything still there,
                // the lander docked again first where it was away (RouteSteps).
                side[(int)PartGroup.Rest] = mode != LegMode.Lander;
                side[(int)PartGroup.Lander] = landerOn && mode != LegMode.WithoutLander;
                if (hasCapsule) side[(int)PartGroup.Capsule] = capsuleInLander ? side[(int)PartGroup.Lander] : side[(int)PartGroup.Rest];
                return side;
            }

            // The groups waiting while the lander flies alone.
            private bool[] Waiting()
            {
                bool[] side = new bool[Groups];
                side[(int)PartGroup.Rest] = true;
                if (hasCapsule && !capsuleInLander) side[(int)PartGroup.Capsule] = true;
                return side;
            }

            private bool[] LanderSide()
            {
                bool[] side = new bool[Groups];
                side[(int)PartGroup.Lander] = true;
                if (capsuleInLander) side[(int)PartGroup.Capsule] = true;
                return side;
            }

            private double MassOf(bool[] side)
            {
                double total = 0.0;
                for (int g = 0; g < Groups; g++)
                    if (side[g]) total += mass[g];
                return total;
            }

            private double FuelOf(int at, bool[] side)
            {
                double total = 0.0;
                for (int g = 0; g < Groups; g++)
                    if (side[g]) total += fuel[at, g];
                return total;
            }

            // The side's stage KSP fires next, from `after` on; -1 for none.
            private int NextOf(bool[] side, int after)
            {
                for (int i = after + 1; i < count; i++)
                    if (!done[i] && side[(int)owner[i]]) return i;
                return -1;
            }

            private int FirstOf(bool[] side)
            {
                return NextOf(side, -1);
            }

            // ----------------------------------------------------------- burning

            private bool Saved(int at)
            {
                StagePlan plan = PlanOf(leg);
                return plan != null && plan.Save.Contains(order[at].Number);
            }

            private StagePlan PlanOf(int at)
            {
                return plans != null && at >= 0 && at < plans.Count ? plans[at] : null;
            }

            // True where a stage saved on this leg stopped the burn.
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
                double take = Math.Min(need, full);
                double burnt = take >= full - 1e-9 ? available : pushed * (1.0 - Math.Exp(-take / exhaust[at]));
                result.Uses.Add(new StageUse { Stage = order[at].Number, Leg = leg, DeltaV = take, Mass = pushed });
                for (int g = 0; g < Groups; g++)
                {
                    if (!side[g] || fuel[at, g] <= 0.0) continue;
                    double part = take >= full - 1e-9 ? fuel[at, g] : burnt * fuel[at, g] / available;
                    fuel[at, g] = Math.Max(0.0, fuel[at, g] - part);
                    mass[g] = Math.Max(0.0, mass[g] - part);
                }
                need -= take;
            }

            // The next stage fires: what KSP drops after `at` goes from the side. With
            // the lander as stage numbers, the rest's part of it stays where the
            // lander's stages come next and a leg of the lander alone is ahead, as the
            // lander returns to it.
            private void Pass(int at, int next, bool[] side, bool forced)
            {
                done[at] = true;
                for (int g = 0; g < Groups; g++)
                {
                    if (!side[g] || drop[at, g] <= 0.0) continue;
                    bool restStays = !forced && restStaysForLander && g == (int)PartGroup.Rest && hasLander && !landerGone
                                     && owner[next] != PartGroup.Rest && leg <= lastAlone;
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

            // --------------------------------------------------------- the leg

            // The stages of what flies the leg that still hold propellant.
            private List<StepStage> Present(bool[] side)
            {
                List<StepStage> list = new List<StepStage>();
                for (int i = FirstOf(side); i >= 0; i = NextOf(side, i))
                    if (FuelOf(i, side) > Tiny && exhaust[i] > 0.0)
                        list.Add(new StepStage { Stage = order[i].Number, CanDrop = NextOf(side, i) >= 0 });
                return list;
            }

            // Once the leg is over: what each of its stages gave and still gives.
            private void Close(List<StepStage> present)
            {
                Dictionary<int, double> left = Remaining();
                foreach (StepStage stage in present)
                {
                    foreach (StageUse use in result.Uses)
                        if (use.Leg == leg && use.Stage == stage.Stage) stage.Given += use.DeltaV;
                    double value;
                    if (stage.Dropped && result.Dropped.TryGetValue(stage.Stage, out value)) stage.LeftAfter = value;
                    else if (left.TryGetValue(stage.Stage, out value)) stage.LeftAfter = value;
                }
            }

            // The stages the player drops after this leg, with those KSP fires before
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
                for (int g = 0; g < Groups; g++)
                {
                    if (!side[g]) continue;
                    mass[g] = Math.Max(0.0, mass[g] - fuel[at, g]);
                    fuel[at, g] = 0.0;
                }
            }

            // What KSP separates when the next stage without delta-v after the burning
            // one fires stays here; the burning stage burns on with what it holds.
            private double LeavePayload(bool[] side)
            {
                int before = FirstOf(side);
                if (before < 0) return 0.0;
                for (int at = NextOf(side, before); at >= 0; before = at, at = NextOf(side, at))
                {
                    if (order[at].DeltaV > 0.5) break;
                    double payload = 0.0;
                    for (int g = 0; g < Groups; g++)
                        if (side[g]) payload += drop[before, g];
                    if (payload <= Tiny) continue;
                    for (int g = 0; g < Groups; g++)
                    {
                        if (!side[g]) continue;
                        mass[g] = Math.Max(0.0, mass[g] - drop[before, g]);
                        drop[before, g] = 0.0;
                    }
                    result.LeftBehind.Add(order[at].Number);
                    result.GoneAfter[order[at].Number] = leg;
                    return payload;
                }
                return 0.0;
            }

            // The lander, docked, filled up from the rest's tanks, as far as they
            // hold it.
            private double Refuel()
            {
                if (!hasLander || landerGone || capsuleOn) return 0.0;
                const int lander = (int)PartGroup.Lander, rest = (int)PartGroup.Rest;
                double moved = 0.0;
                int from = 0;
                for (int i = 0; i < count; i++)
                {
                    // A stage of the lander already fired past is gone.
                    if (done[i]) continue;
                    double wanted = capacity[i, lander] - fuel[i, lander];
                    while (wanted > Tiny && from < count)
                    {
                        double take = Math.Min(wanted, fuel[from, rest]);
                        fuel[from, rest] -= take;
                        fuel[i, lander] += take;
                        wanted -= take;
                        moved += take;
                        if (fuel[from, rest] <= Tiny) from++;
                    }
                }
                mass[rest] = Math.Max(0.0, mass[rest] - moved);
                mass[lander] += moved;
                return moved;
            }

            // --------------------------------------------------------- leaving

            // The rest flies on without the lander: it stays where it is, with
            // what its stages still hold.
            private void LeaveLander()
            {
                if (!hasLander || landerGone) return;
                bool[] side = LanderSide();
                double pushed = MassOf(side);
                for (int i = FirstOf(side); i >= 0; i = NextOf(side, i))
                {
                    result.LeftBehind.Add(order[i].Number);
                    result.GoneAfter[order[i].Number] = leg - 1;
                    double available = FuelOf(i, side);
                    if (available > Tiny) result.Dropped[order[i].Number] = DeltaV(i, available, pushed);
                    pushed -= available;
                    for (int g = 0; g < Groups; g++)
                    {
                        if (!side[g]) continue;
                        pushed -= drop[i, g];
                        fuel[i, g] = 0.0;
                    }
                    done[i] = true;
                }
                for (int g = 0; g < Groups; g++)
                    if (side[g]) mass[g] = 0.0;
                landerGone = true;
                apart = false;
            }

            // The capsule flies on alone: what it leaves goes with what it holds; a
            // part waiting elsewhere stays there.
            private void LeaveForCapsule()
            {
                if (capsuleOn || !hasCapsule) return;
                bool[] host = apart ? (capsuleInLander ? LanderSide() : Waiting()) : Side(LegMode.Whole);
                bool[] waiting = apart ? (capsuleInLander ? Waiting() : LanderSide()) : new bool[Groups];
                const int capsule = (int)PartGroup.Capsule;
                for (int i = 0; i < count; i++)
                {
                    for (int g = 0; g < Groups; g++)
                    {
                        if (g == capsule || fuel[i, g] <= Tiny) continue;
                        if (waiting[g]) result.LeftBehind.Add(order[i].Number);
                        else if (host[g]) result.Dropped[order[i].Number] = DeltaV(i, fuel[i, g], MassOf(host));
                        result.GoneAfter[order[i].Number] = leg - 1;
                        fuel[i, g] = 0.0;
                    }
                    if (owner[i] != PartGroup.Capsule) done[i] = true;
                }
                for (int g = 0; g < Groups; g++)
                    if (g != capsule) mass[g] = 0.0;
                capsuleOn = true;
                landerGone = true;
                apart = false;
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

            // What each stage still attached gives, burning on in order from here:
            // the lander away and the rest waiting each on their own.
            private Dictionary<int, double> Remaining()
            {
                Dictionary<int, double> left = new Dictionary<int, double>();
                if (capsuleOn) BurnOn(left, Side(LegMode.Capsule));
                else if (apart)
                {
                    BurnOn(left, Waiting());
                    BurnOn(left, LanderSide());
                }
                else BurnOn(left, Side(LegMode.Whole));
                return left;
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
                    for (int g = 0; g < Groups; g++)
                        if (side[g]) pushed -= drop[i, g];
                }
            }
        }
    }
}
