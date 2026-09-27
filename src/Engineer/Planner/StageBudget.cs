using System.Collections.Generic;
using System;

namespace ReDefinition.Engineer
{
    // Who flies a leg where the vessel carries a lander: the whole vessel, the
    // lander alone while the rest waits in orbit, or the rest without the lander,
    // which stays behind.
    internal enum LegMode
    {
        Whole,
        Lander,
        WithoutLander,
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
    // ahead. A stage the player keeps for a leg is not touched before that leg,
    // nor any stage after it in KSP's order; what the stages before it still hold
    // when it takes over is dropped. Free of Unity, tested.
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

        internal sealed class Result
        {
            public readonly List<StageUse> Uses = new List<StageUse>();
            // Per leg: what it needs with the margin, and what no stage covers.
            public double[] Needed;
            public double[] Short;
            // Per stage, by its number, as delta-v: what it has at the end of the
            // route, or what it held when it was dropped or left behind.
            public readonly Dictionary<int, double> Left = new Dictionary<int, double>();
            public readonly Dictionary<int, double> Dropped = new Dictionary<int, double>();
            public readonly HashSet<int> LeftBehind = new HashSet<int>();

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
        // `kept`: stage number to the leg it is kept for; `lander`: the highest
        // stage of the lander, -1 for none; `modes`: who flies each leg, Whole
        // where it is missing.
        internal static Result Allocate(IList<BudgetStage> stages, IList<double> legs, double margin,
                                        IDictionary<int, int> kept, int lander, IList<LegMode> modes)
        {
            return new Run(stages, kept, lander).Allocate(legs, margin, modes);
        }

        private sealed class Run
        {
            private readonly List<BudgetStage> order;
            private readonly IDictionary<int, int> kept;
            private readonly double[] fuel;
            private readonly double[] exhaust;
            private readonly double[] drop;
            // The main group is [0, firstLander), the lander [firstLander, count).
            private readonly int firstLander;
            private readonly bool hasLander;

            private double total;   // everything still attached, the lander with it
            private double landerMass;
            private bool landerGone;
            private bool restDropped;
            private int mainAt;
            private int landerAt;
            private Result result;

            internal Run(IList<BudgetStage> stages, IDictionary<int, int> kept, int lander)
            {
                order = new List<BudgetStage>(stages);
                order.Sort((a, b) => b.Number.CompareTo(a.Number));
                this.kept = kept;
                int count = order.Count;
                fuel = new double[count];
                exhaust = new double[count];
                drop = new double[count];
                for (int i = 0; i < count; i++)
                {
                    double start = Math.Max(0.0, order[i].StartMass);
                    double end = Math.Max(0.0, Math.Min(order[i].EndMass, start));
                    if (order[i].DeltaV > 0.5 && end > 0.0 && start > end)
                    {
                        fuel[i] = start - end;
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

                total = count > 0 ? Math.Max(0.0, order[0].StartMass) : 0.0;
                landerMass = hasLander ? Math.Max(0.0, order[firstLander].StartMass) : 0.0;
                mainAt = 0;
                landerAt = firstLander;
            }

            internal Result Allocate(IList<double> legs, double margin, IList<LegMode> modes)
            {
                result = new Result { Needed = new double[legs.Count], Short = new double[legs.Count] };
                int lastAlone = -1;
                for (int leg = 0; leg < legs.Count; leg++)
                    if (ModeOf(modes, leg) == LegMode.Lander) lastAlone = leg;

                for (int leg = 0; leg < legs.Count; leg++)
                {
                    double need = Math.Max(0.0, legs[leg]) * (1.0 + Math.Max(0.0, margin));
                    result.Needed[leg] = need;
                    LegMode mode = ModeOf(modes, leg);

                    if (mode == LegMode.WithoutLander) LeaveLander();
                    // While the lander flies alone, the rest waits and stages
                    // nothing.
                    if (mode != LegMode.Lander) TakeOver(ref mainAt, 0, firstLander, leg, false, false);
                    if (hasLander && !landerGone)
                        TakeOver(ref landerAt, firstLander, order.Count, leg, true, mode == LegMode.Lander);

                    if (mode == LegMode.Lander)
                    {
                        if (!landerGone) Burn(ref landerAt, order.Count, true, true, leg, ref need);
                    }
                    else
                    {
                        // A stage of the rest kept for a later leg holds back the
                        // lander's stages too: KSP fires them after it.
                        bool held = Burn(ref mainAt, firstLander, false, false, leg, ref need);
                        if (hasLander && !landerGone && !held && need > 1e-6)
                        {
                            if (leg > lastAlone) DropRest();
                            Burn(ref landerAt, order.Count, true, false, leg, ref need);
                        }
                    }
                    result.Short[leg] = need > 1e-6 ? need : 0.0;
                }
                Finish();
                return result;
            }

            private LegMode ModeOf(IList<LegMode> modes, int leg)
            {
                return modes != null && leg < modes.Count && hasLander ? modes[leg] : LegMode.Whole;
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

            // True where a stage kept for a later leg stopped the burn.
            private bool Burn(ref int at, int end, bool inLander, bool alone, int leg, ref double need)
            {
                while (need > 1e-6 && at < end)
                {
                    int keptLeg;
                    if (kept != null && kept.TryGetValue(order[at].Number, out keptLeg) && keptLeg > leg) return true;
                    if (fuel[at] > Tiny && exhaust[at] > 0.0)
                    {
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

            // A stage kept for this leg takes over here: the ones before it in its
            // group are dropped with what they still hold.
            private void TakeOver(ref int at, int start, int end, int leg, bool inLander, bool alone)
            {
                if (kept == null) return;
                int keptAt = -1;
                for (int i = start; i < end; i++)
                {
                    int keptLeg;
                    if (kept.TryGetValue(order[i].Number, out keptLeg) && keptLeg == leg) { keptAt = i; break; }
                }
                while (at < keptAt)
                {
                    if (fuel[at] > Tiny)
                    {
                        result.Dropped[order[at].Number] = DeltaV(at, Mass(alone));
                        Remove(fuel[at], inLander);
                        fuel[at] = 0.0;
                    }
                    Next(ref at, end, inLander);
                }
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
                BurnOn(mainAt, firstLander, total);
                if (hasLander && !landerGone) BurnOn(landerAt, order.Count, landerMass);
            }

            private void BurnOn(int from, int end, double mass)
            {
                for (int i = from; i < end; i++)
                {
                    if (result.Dropped.ContainsKey(order[i].Number)) continue;
                    result.Left[order[i].Number] = DeltaV(i, mass);
                    mass -= fuel[i] + (i + 1 < end ? drop[i] : 0.0);
                }
            }
        }
    }
}
