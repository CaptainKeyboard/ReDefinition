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

    // Which of the vessel's stages pays for which leg of the route
    // (docs/development/planner.md). Worked out on the mass the stages push, so
    // that a stage burning over several legs, or a lander that lands, returns and
    // is carried on to the next body, counts with what it weighs at that moment.
    //
    // Each stage burns its propellant (start mass less end mass) at the exhaust
    // velocity its delta-v gives (Tsiolkovsky backwards). Stages burn in KSP's
    // order, the highest number first, until empty; then what KSP drops before
    // the next one fires goes. The lander is the stages numbered up to a chosen
    // one: what KSP's staging leaves once the stages above it are gone. The last
    // stage above it stays attached while the lander is away, and goes once the
    // whole vessel burns the lander's stages with no leg of the lander alone
    // ahead.
    //
    // The player can save a stage on a leg: the leg stops before it, and before the
    // stages after it, since KSP cannot fire those first. The player can drop a
    // stage after a leg: at the leg's end it goes with what it still holds, and so do
    // the stages KSP fires before it.
    //
    // The capsule is the stages numbered up to a chosen one, as the lander is; the
    // first leg of the capsule leaves everything else where it is, with what it
    // holds, and from then on only the capsule's stages burn, pushing the capsule.
    //
    // After a leg marked for it, a payload stays behind: what KSP separates when
    // the next stage without delta-v after the one burning fires, while that one
    // burns on with what it holds.
    //
    // After a leg marked for it, the lander, docked again, is refilled from the
    // rest: each of its stages still attached up to what it held at the start, from
    // the rest's stages in KSP's order, the one burning first. Free of Unity, tested.
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

        // `legs`: what each leg needs; `margin`: 0.1 for ten per cent more;
        // `plans`: per leg, the stages saved on it and dropped after it; `lander`:
        // the highest stage of the lander, -1 for none; `modes`: who flies each leg,
        // Whole where it is missing.
        // `refuelAfter`: per leg, whether the lander is refilled once it is over;
        // `payloadAfter`: whether a payload stays behind once it is over.
        internal static Result Allocate(IList<BudgetStage> stages, IList<double> legs, double margin,
                                        IList<StagePlan> plans, int lander, IList<LegMode> modes,
                                        IList<bool> refuelAfter = null, IList<bool> payloadAfter = null, int capsule = -1)
        {
            return new Run(stages, plans, lander, capsule).Allocate(legs, margin, modes, refuelAfter, payloadAfter);
        }

        private sealed class Run
        {
            private readonly List<BudgetStage> order;
            private readonly IList<StagePlan> plans;
            private readonly double[] fuel;
            private readonly double[] capacity;
            private readonly double[] exhaust;
            private readonly double[] drop;
            // The main group is [0, firstLander), the lander [firstLander, count);
            // once the capsule flies alone, the main group is the capsule's stages.
            private int firstLander;
            private readonly bool hasLander;
            private readonly int firstCapsule;
            private readonly bool hasCapsule;
            private bool capsuleOn;

            private double total;   // everything still attached, the lander with it
            private double landerMass;
            private bool landerGone;
            // The lander flew away and has not docked again.
            private bool apart;
            private bool restDropped;
            private int mainAt;
            private int landerAt;
            // The leg worked out now.
            private int leg;
            private Result result;

            internal Run(IList<BudgetStage> stages, IList<StagePlan> plans, int lander, int capsule)
            {
                order = new List<BudgetStage>(stages);
                order.Sort((a, b) => b.Number.CompareTo(a.Number));
                this.plans = plans;
                int count = order.Count;
                fuel = new double[count];
                capacity = new double[count];
                exhaust = new double[count];
                drop = new double[count];
                for (int i = 0; i < count; i++)
                {
                    double start = Math.Max(0.0, order[i].StartMass);
                    double end = Math.Max(0.0, Math.Min(order[i].EndMass, start));
                    if (order[i].DeltaV > 0.5 && end > 0.0 && start > end)
                    {
                        fuel[i] = start - end;
                        capacity[i] = fuel[i];
                        exhaust[i] = order[i].DeltaV / Math.Log(start / end);
                    }
                    // What goes when the next stage fires: all but the propellant
                    // burnt and what the next stage starts with.
                    drop[i] = i + 1 < count ? Math.Max(0.0, start - fuel[i] - Math.Max(0.0, order[i + 1].StartMass)) : 0.0;
                }

                firstLander = count;
                if (lander >= 0)
                    for (int i = 0; i < count; i++)
                        if (order[i].Number <= lander) { firstLander = i; break; }
                // A lander needs something to leave behind, and something to be.
                hasLander = firstLander > 0 && firstLander < count;
                if (!hasLander) firstLander = count;

                firstCapsule = count;
                if (capsule >= 0)
                    for (int i = 0; i < count; i++)
                        if (order[i].Number <= capsule) { firstCapsule = i; break; }
                hasCapsule = firstCapsule > 0 && firstCapsule < count;

                total = count > 0 ? Math.Max(0.0, order[0].StartMass) : 0.0;
                landerMass = hasLander ? Math.Max(0.0, order[firstLander].StartMass) : 0.0;
                mainAt = 0;
                landerAt = firstLander;
            }

            internal Result Allocate(IList<double> legs, double margin, IList<LegMode> modes, IList<bool> refuelAfter,
                                     IList<bool> payloadAfter)
            {
                result = new Result
                {
                    Needed = new double[legs.Count], Short = new double[legs.Count], Refuelled = new double[legs.Count],
                    Payload = new double[legs.Count], Stages = new List<StepStage>[legs.Count],
                };
                int lastAlone = -1;
                for (int at = 0; at < legs.Count; at++)
                    if (ModeOf(modes, at) == LegMode.Lander) lastAlone = at;

                for (leg = 0; leg < legs.Count; leg++)
                {
                    double need = Math.Max(0.0, legs[leg]) * (1.0 + Math.Max(0.0, margin));
                    result.Needed[leg] = need;
                    LegMode mode = ModeOf(modes, leg);
                    apart = hasLander && !landerGone && (mode == LegMode.Lander || (apart && mode != LegMode.Whole));

                    if (mode == LegMode.WithoutLander) LeaveLander();
                    if (mode == LegMode.Capsule) LeaveForCapsule();
                    List<StepStage> present = Present(mode);

                    if (mode == LegMode.Capsule)
                    {
                        if (capsuleOn) Burn(ref mainAt, firstLander, false, false, ref need);
                    }
                    else if (mode == LegMode.Lander)
                    {
                        if (!landerGone) Burn(ref landerAt, order.Count, true, true, ref need);
                    }
                    else
                    {
                        // A stage of the rest saved for a later leg holds back the
                        // lander's stages too: KSP fires them after it.
                        bool held = Burn(ref mainAt, firstLander, false, false, ref need);
                        // A lander stage saved here fires nothing, so the rest stays.
                        bool landerSaved = landerAt < order.Count && fuel[landerAt] > Tiny && Saved(landerAt);
                        if (hasLander && !landerGone && !held && !landerSaved && need > 1e-6)
                        {
                            if (leg > lastAlone) DropRest();
                            Burn(ref landerAt, order.Count, true, false, ref need);
                        }
                    }
                    result.Short[leg] = need > 1e-6 ? need : 0.0;
                    if (refuelAfter != null && leg < refuelAfter.Count && refuelAfter[leg]) result.Refuelled[leg] = Refuel();
                    if (payloadAfter != null && leg < payloadAfter.Count && payloadAfter[leg]) result.Payload[leg] = LeavePayload();
                    DropAfter(present);
                    Close(present);
                    result.Stages[leg] = present;
                }
                Finish();
                return result;
            }

            // The stages of what flies the leg that still hold propellant: the lander
            // alone, or the main group with the lander where it is attached.
            private List<StepStage> Present(LegMode mode)
            {
                List<StepStage> list = new List<StepStage>();
                bool landerOn = hasLander && !landerGone;
                if (mode != LegMode.Lander) AddPresent(list, mainAt, firstLander, landerOn && !apart);
                if (landerOn && (mode == LegMode.Lander || !apart)) AddPresent(list, landerAt, order.Count, false);
                return list;
            }

            private void AddPresent(List<StepStage> list, int from, int end, bool followed)
            {
                for (int i = from; i < end; i++)
                    if (fuel[i] > Tiny && exhaust[i] > 0.0)
                        list.Add(new StepStage { Stage = order[i].Number, CanDrop = i + 1 < end || followed });
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
            // them.
            private void DropAfter(List<StepStage> present)
            {
                StagePlan plan = PlanOf(leg);
                if (plan == null || plan.DropAfter.Count == 0) return;
                foreach (StepStage stage in present)
                {
                    if (!stage.CanDrop || !plan.DropAfter.Contains(stage.Stage)) continue;
                    int at = order.FindIndex(row => row.Number == stage.Stage);
                    if (at >= 0) DropThrough(at);
                }
                foreach (StepStage stage in present)
                {
                    int gone;
                    stage.Dropped = result.GoneAfter.TryGetValue(stage.Stage, out gone) && gone == leg
                                    && result.Dropped.ContainsKey(stage.Stage) && !result.LeftBehind.Contains(stage.Stage);
                }
            }

            // The stage at `at` goes, and every stage KSP fires before it on the same
            // vessel: the rest's first, where the lander is attached to it.
            private void DropThrough(int at)
            {
                if (at >= firstLander)
                {
                    if (!hasLander || landerGone) return;
                    if (!apart)
                    {
                        DropThrough(firstLander - 1);
                        DropRest();
                    }
                    while (landerAt <= at && landerAt < order.Count)
                    {
                        Jettison(landerAt, true, apart ? landerMass : total);
                        Next(ref landerAt, order.Count, true);
                    }
                    return;
                }
                while (mainAt <= at)
                {
                    Jettison(mainAt, false, apart ? total - landerMass : total);
                    if (mainAt + 1 < firstLander)
                    {
                        Next(ref mainAt, firstLander, false);
                        continue;
                    }
                    // The rest's last stage: with it the rest is gone.
                    mainAt++;
                    if (hasLander && !landerGone) DropRest();
                }
            }

            private void Jettison(int at, bool inLander, double mass)
            {
                if (fuel[at] <= Tiny) return;
                result.Dropped[order[at].Number] = DeltaV(at, mass);
                result.GoneAfter[order[at].Number] = leg;
                Remove(fuel[at], inLander);
                fuel[at] = 0.0;
            }

            private StagePlan PlanOf(int at)
            {
                return plans != null && at >= 0 && at < plans.Count ? plans[at] : null;
            }

            // What KSP separates when the next stage without delta-v after the burning
            // one fires stays here; the burning stage burns on with what it holds.
            private double LeavePayload()
            {
                int at = apart ? landerAt : mainAt;
                int end = apart || at >= firstLander ? order.Count : firstLander;
                for (int i = at + 1; i < end; i++)
                {
                    if (order[i].DeltaV > 0.5) break;
                    double mass = drop[i - 1];
                    if (mass <= Tiny) continue;
                    Remove(mass, at >= firstLander);
                    drop[i - 1] = 0.0;
                    result.LeftBehind.Add(order[i].Number);
                    result.GoneAfter[order[i].Number] = leg;
                    return mass;
                }
                return 0.0;
            }

            // The lander, docked, filled up from the rest's tanks, as far as they
            // hold it.
            private double Refuel()
            {
                if (!hasLander || landerGone) return 0.0;
                double moved = 0.0;
                int from = mainAt;
                for (int i = landerAt; i < order.Count; i++)
                {
                    double wanted = capacity[i] - fuel[i];
                    while (wanted > Tiny && from < firstLander)
                    {
                        double take = Math.Min(wanted, fuel[from]);
                        fuel[from] -= take;
                        fuel[i] += take;
                        wanted -= take;
                        moved += take;
                        if (fuel[from] <= Tiny) from++;
                    }
                }
                landerMass += moved;
                return moved;
            }

            private LegMode ModeOf(IList<LegMode> modes, int at)
            {
                if (modes == null || at >= modes.Count) return LegMode.Whole;
                LegMode mode = modes[at];
                if (mode == LegMode.Capsule) return hasCapsule ? mode : LegMode.Whole;
                return hasLander ? mode : LegMode.Whole;
            }

            // The capsule flies on alone: what it leaves goes with what it holds.
            private void LeaveForCapsule()
            {
                if (capsuleOn || !hasCapsule) return;
                bool fromLander = apart && hasLander && !landerGone;
                // Where it burns now: the lander away, the whole vessel into the
                // lander's stages, or the rest's.
                int at = fromLander || (hasLander && !landerGone && mainAt >= firstLander) ? landerAt : mainAt;
                double now = fromLander ? landerMass : total;
                int from = Math.Max(firstCapsule, at);
                for (int i = 0; i < from; i++)
                {
                    if (fuel[i] <= Tiny) continue;
                    // The rest waiting elsewhere stays there with what it holds.
                    if (fromLander && i < firstLander) result.LeftBehind.Add(order[i].Number);
                    else result.Dropped[order[i].Number] = DeltaV(i, now);
                    result.GoneAfter[order[i].Number] = leg - 1;
                    fuel[i] = 0.0;
                }
                total = at < firstCapsule ? Math.Min(now, Math.Max(0.0, order[firstCapsule].StartMass)) : now;
                landerMass = 0.0;
                landerGone = true;
                apart = false;
                restDropped = true;
                mainAt = from;
                firstLander = order.Count;
                capsuleOn = true;
            }

            // The whole vessel burns the lander's stages and the lander flies
            // alone no more: the rest's last stage is staged away, as KSP's own
            // simulation does.
            private void DropRest()
            {
                if (restDropped || mainAt < firstLander) return;
                Remove(drop[firstLander - 1], false);
                restDropped = true;
            }

            // What the stages of a group push: the lander alone, or everything
            // attached.
            private double Mass(bool alone)
            {
                return alone ? landerMass : total;
            }

            private bool Saved(int at)
            {
                StagePlan plan = PlanOf(leg);
                return plan != null && plan.Save.Contains(order[at].Number);
            }

            private void BurnOne(int at, bool inLander, bool alone, ref double need)
            {
                if (fuel[at] <= Tiny || exhaust[at] <= 0.0) return;
                double mass = Mass(alone);
                double full = DeltaV(at, mass);
                double take = Math.Min(need, full);
                double burnt = take >= full - 1e-9 ? fuel[at] : mass * (1.0 - Math.Exp(-take / exhaust[at]));
                result.Uses.Add(new StageUse { Stage = order[at].Number, Leg = leg, DeltaV = take, Mass = mass });
                Remove(burnt, inLander);
                fuel[at] -= burnt;
                if (take >= full - 1e-9) fuel[at] = 0.0;
                need -= take;
            }

            // True where a stage saved on this leg stopped the burn.
            private bool Burn(ref int at, int end, bool inLander, bool alone, ref double need)
            {
                while (need > 1e-6 && at < end)
                {
                    if (fuel[at] > Tiny && Saved(at)) return true;
                    BurnOne(at, inLander, alone, ref need);
                    if (fuel[at] > Tiny && exhaust[at] > 0.0) return false;
                    Next(ref at, end, inLander);
                }
                return false;
            }

            // The next stage of the group fires: what KSP drops with it goes. The
            // last stage of the main group stays: the lander returns to it.
            private void Next(ref int at, int end, bool inLander)
            {
                if (at + 1 < end) Remove(drop[at], inLander);
                at++;
            }

            // The rest flies on without the lander: it stays where it is, with
            // what its stages still hold.
            private void LeaveLander()
            {
                if (!hasLander || landerGone) return;
                double mass = landerMass;
                for (int i = landerAt; i < order.Count; i++)
                {
                    result.LeftBehind.Add(order[i].Number);
                    result.GoneAfter[order[i].Number] = leg - 1;
                    if (fuel[i] > Tiny) result.Dropped[order[i].Number] = DeltaV(i, mass);
                    mass -= fuel[i] + (i + 1 < order.Count ? drop[i] : 0.0);
                    fuel[i] = 0.0;
                }
                total -= landerMass;
                landerMass = 0.0;
                landerGone = true;
                landerAt = order.Count;
            }

            private void Remove(double mass, bool inLander)
            {
                total = Math.Max(0.0, total - mass);
                if (inLander) landerMass = Math.Max(0.0, landerMass - mass);
            }

            private double DeltaV(int stage, double mass)
            {
                if (fuel[stage] <= Tiny || exhaust[stage] <= 0.0 || mass <= fuel[stage]) return 0.0;
                return exhaust[stage] * Math.Log(mass / (mass - fuel[stage]));
            }

            // What each stage still has, burning on in order from where the route
            // ends: the rest with the lander attached, the lander alone. A stage
            // already burnt out has nothing.
            private void Finish()
            {
                for (int i = 0; i < order.Count; i++)
                    if (!result.Dropped.ContainsKey(order[i].Number)) result.Left[order[i].Number] = 0.0;
                foreach (KeyValuePair<int, double> pair in Remaining()) result.Left[pair.Key] = pair.Value;
            }

            // What each stage still attached gives, burning on in order from here:
            // the rest on its own where the lander flew away and did not come back.
            private Dictionary<int, double> Remaining()
            {
                Dictionary<int, double> left = new Dictionary<int, double>();
                BurnOn(left, mainAt, firstLander, apart ? total - landerMass : total);
                if (hasLander && !landerGone) BurnOn(left, landerAt, order.Count, landerMass);
                return left;
            }

            private void BurnOn(Dictionary<int, double> left, int from, int end, double mass)
            {
                for (int i = from; i < end; i++)
                {
                    if (result.Dropped.ContainsKey(order[i].Number)) continue;
                    left[order[i].Number] = DeltaV(i, mass);
                    mass -= fuel[i] + (i + 1 < end ? drop[i] : 0.0);
                }
            }
        }
    }
}
