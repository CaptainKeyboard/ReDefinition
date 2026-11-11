using System;
using System.Collections.Generic;

namespace AtmoSim.Core
{
    // Epoch re-anchoring: the state at any time is a pure function of the most recent epoch boundary, re-initialised from
    // climatology a fixed spin-up window earlier, so reaching any time costs at most spinUp + epochSteps + crossfade steps per
    // trajectory, however long the career. Around a boundary two overlapping trajectories are blended.
    public sealed class EpochField
    {
        public readonly BodyParams Body;
        public readonly SimConfig Cfg;
        public readonly long EpochSteps, SpinUpSteps, CrossfadeSteps;
        private readonly Dictionary<long, Atmosphere> live = new Dictionary<long, Atmosphere>();
        public long StepsTaken;

        public EpochField(BodyParams body, SimConfig cfg, long epochSteps, long spinUpSteps, long crossfadeSteps)
        {
            Body = body; Cfg = cfg; EpochSteps = epochSteps; SpinUpSteps = spinUpSteps; CrossfadeSteps = crossfadeSteps;
        }

        // The epoch length in steps that keeps the worst-case catch-up within the worker budget:
        // epochSteps * stepCost + 2 * spinUp + 2 * crossfade <= budgetSeconds.
        public static long DeriveEpochSteps(double stepCostSeconds, double budgetSeconds, long spinUpSteps, long crossfadeSteps)
        {
            long total = (long)(budgetSeconds / stepCostSeconds);
            long e = total - 2 * spinUpSteps - 2 * crossfadeSteps;
            return e < 1 ? 1 : e;
        }

        public double EpochSeconds { get { return EpochSteps * Cfg.StepSeconds; } }

        public long EpochIndex(double ut)
        {
            return (long)DMath.Floor(ut / EpochSeconds);
        }

        private static ulong EpochSeed(ulong seed, long epoch)
        {
            ulong h = seed * 0x9E3779B97F4A7C15UL + (ulong)epoch * 0xBF58476D1CE4E5B9UL + 0x94D049BB133111EBUL;
            h ^= h >> 31; h *= 0xD6E8FEB86659FD93UL; h ^= h >> 32;
            return h;
        }

        // The trajectory of an epoch advanced to ut (a multiple of the step is reached by stepping; ut is rounded down to a step).
        public Atmosphere Trajectory(long epoch, double ut)
        {
            double start = epoch * EpochSeconds - SpinUpSteps * Cfg.StepSeconds;
            Atmosphere sim;
            if (!live.TryGetValue(epoch, out sim) || sim.Ut > ut + 1e-6)
            {
                sim = new Atmosphere(Body, Cfg);
                sim.Init(start, EpochSeed(Cfg.Seed, epoch));
                live[epoch] = sim;
            }
            while (sim.Ut + Cfg.StepSeconds <= ut + 1e-6) { sim.Step(); StepsTaken++; }
            return sim;
        }

        // The trajectories to blend at ut and the weight of the newer one (1 = only the epoch of ut).
        public void StateAt(double ut, out Atmosphere newer, out Atmosphere older, out double weight)
        {
            long e = EpochIndex(ut);
            double since = ut - e * EpochSeconds;
            double x = CrossfadeSteps * Cfg.StepSeconds;
            newer = Trajectory(e, ut);
            if (since >= x || x <= 0) { older = null; weight = 1.0; }
            else
            {
                older = Trajectory(e - 1, ut);
                double t = since / x;
                weight = t * t * (3.0 - 2.0 * t);
            }
            // drop trajectories that can no longer be asked for
            var drop = new List<long>();
            foreach (var kv in live) if (kv.Key < e - 1 || kv.Key > e) drop.Add(kv.Key);
            foreach (long k in drop) live.Remove(k);
        }

        // Blends one 2D field of the two trajectories.
        public static void Blend(double[] newer, double[] older, double weight, double[] target)
        {
            if (older == null || weight >= 1.0) { Array.Copy(newer, target, target.Length); return; }
            for (int i = 0; i < target.Length; i++) target[i] = weight * newer[i] + (1.0 - weight) * older[i];
        }

        // Bytes of state held while two trajectories run (prognostic arrays plus the diagnosed 3D fields).
        public long ResidentBytes()
        {
            long b = 0;
            foreach (var kv in live) b += kv.Value.ResidentBytes();
            return b;
        }
    }
}
