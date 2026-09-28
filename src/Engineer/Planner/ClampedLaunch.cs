using System;
using System.Collections.Generic;

namespace ReDefinition.Engineer
{
    // Stages fired while the launch clamps still hold the vessel, and the stage that
    // releases them, as they burn in flight: together (docs/development/planner.md).
    // KSP's delta-v simulation burns every stage after the one before; engines lit
    // on the pad would burn their tanks alone before the boosters that fire with the
    // clamps. Here the held stages (the core) and the releasing stage (the boosters)
    // lift off together, at their summed thrust and flow, until one of them is
    // empty; the other burns on alone, after the boosters are dropped where the core
    // stays on longer. Free of Unity, tested.
    internal static class ClampedLaunch
    {
        // `rows` in firing order, the highest number first. The rows as they burn:
        // the lift-off, numbered as the last held stage, and what burns on after it,
        // numbered as the releasing stage, so that the order by number stays the order
        // they burn in; `labels` names them ("S4+3", then "S4" or "S3").
        internal static List<StageRow> Parallel(List<StageRow> rows, IList<PartFacts> parts, Dictionary<int, string> labels)
        {
            int release = int.MinValue;
            foreach (PartFacts part in parts)
                if (part.Clamp) release = Math.Max(release, part.ActivatesAt);
            if (release < 0) return rows;
            StageRow freed = rows.Find(row => row.Number == release);
            List<StageRow> held = rows.FindAll(row => row.Number > release && row.DeltaVVacuum > 0.5 && row.ThrustVacuum > 0.0);
            if (freed == null || held.Count == 0 || freed.DeltaVVacuum <= 0.5 || freed.ThrustVacuum <= 0.0) return rows;
            // Only a vessel that drops nothing on the pad, from the first held stage to
            // the release: otherwise the clamps are staged elsewhere than at lift-off.
            int padFrom = rows.IndexOf(held[0]), padTo = rows.IndexOf(freed);
            for (int i = padFrom; i < padTo; i++)
                if (Math.Abs(rows[i].EndMass - rows[i + 1].StartMass) > Math.Max(0.01, rows[i].EndMass * 1e-3)) return rows;
            // Only where the core's tanks are dry as KSP starts the release stage;
            // otherwise its engines burn on in that stage's own figures.
            foreach (PartFacts part in parts)
            {
                double[] after;
                if (!part.StageFuel.TryGetValue(release, out after) || after[0] - after[1] <= 1e-6) continue;
                foreach (StageRow row in held)
                {
                    double[] before;
                    if (part.StageFuel.TryGetValue(row.Number, out before) && before[0] - before[1] > 1e-6) return rows;
                }
            }

            // The core: every engine lit on the pad burns in the last held stage.
            StageRow core = held[held.Count - 1];
            double coreFuel = 0.0;
            foreach (StageRow row in held) coreFuel += row.StartMass - row.EndMass;
            double boosterFuel = freed.StartMass - freed.EndMass;
            double coreExhaust = Exhaust(core), boosterExhaust = Exhaust(freed);
            if (coreExhaust <= 0.0 || boosterExhaust <= 0.0 || coreFuel <= 0.0 || boosterFuel <= 0.0) return rows;
            double coreFlow = core.ThrustVacuum / coreExhaust, boosterFlow = freed.ThrustVacuum / boosterExhaust;
            double coreTime = coreFuel / coreFlow, boosterTime = boosterFuel / boosterFlow;

            // Together, until the first runs dry.
            double together = Math.Min(coreTime, boosterTime);
            double start = held[0].StartMass;
            double end = start - (coreFlow + boosterFlow) * together;
            double exhaust = (core.ThrustVacuum + freed.ThrustVacuum) / (coreFlow + boosterFlow);
            StageRow first = core.Copy();
            first.StartMass = start;
            first.EndMass = end;
            first.DeltaVVacuum = first.DeltaV = exhaust * Math.Log(start / end);
            first.BurnTime = together;
            first.Isp = exhaust / StageTable.StandardGravity;
            first.ThrustVacuum = core.ThrustVacuum + freed.ThrustVacuum;
            first.Thrust = core.Thrust + freed.Thrust;
            first.ThrustAtSurface = new Dictionary<string, double>(core.ThrustAtSurface);
            foreach (KeyValuePair<string, double> body in freed.ThrustAtSurface)
            {
                double thrust;
                first.ThrustAtSurface.TryGetValue(body.Key, out thrust);
                first.ThrustAtSurface[body.Key] = thrust + body.Value;
            }
            first.ThrustTorque = core.ThrustTorque + freed.ThrustTorque;
            first.GimbalTorque = core.GimbalTorque + freed.GimbalTorque;
            first.ThrustArm = first.Thrust > 0.0 ? (core.ThrustArm * core.Thrust + freed.ThrustArm * freed.Thrust) / first.Thrust : 0.0;
            first.Number = core.Number;

            // What burns on: the core, after the boosters are dropped where the core
            // stays on longer than they do; or the boosters.
            bool coreGoesOn = coreTime > boosterTime;
            StageRow source = coreGoesOn ? core : freed;
            double left = coreGoesOn ? coreFuel - coreFlow * together : boosterFuel - boosterFlow * together;
            double dropped = 0.0;
            if (coreGoesOn && Decouples(parts, release, true) < Decouples(parts, release, false))
            {
                int at = rows.IndexOf(freed);
                if (at + 1 < rows.Count) dropped = Math.Max(0.0, freed.EndMass - rows[at + 1].StartMass);
            }
            List<StageRow> result = new List<StageRow>();
            int place = rows.IndexOf(held[0]);
            foreach (StageRow row in rows)
            {
                if (rows.IndexOf(row) == place)
                {
                    result.Add(first);
                    if (left > 1e-6)
                    {
                        StageRow second = source.Copy();
                        double from = end - dropped;
                        double sourceExhaust = coreGoesOn ? coreExhaust : boosterExhaust;
                        second.Number = freed.Number;
                        second.StartMass = from;
                        second.EndMass = from - left;
                        second.DeltaVVacuum = second.DeltaV = sourceExhaust * Math.Log(from / (from - left));
                        second.BurnTime = left / (coreGoesOn ? coreFlow : boosterFlow);
                        result.Add(second);
                    }
                }
                // The held stages, the release, and the stages on the pad between them
                // that burn nothing.
                if (held.Contains(row) || row == freed || (row.Number > release && row.Number < held[0].Number)) continue;
                result.Add(row);
            }
            labels[core.Number] = "S" + core.Number + "+" + freed.Number;
            labels[freed.Number] = "S" + (coreGoesOn ? core.Number : freed.Number);
            return result;
        }

        // The highest stage that decouples the engines lit on the pad (`core`) or with
        // the clamps' release.
        private static int Decouples(IList<PartFacts> parts, int release, bool core)
        {
            int stage = int.MinValue;
            foreach (PartFacts part in parts)
                if (part.Engine && (core ? part.ActivatesAt > release : part.ActivatesAt == release))
                    stage = Math.Max(stage, part.DecoupleStage);
            return stage;
        }

        // A stage's exhaust speed in vacuum, from its delta-v and its masses.
        private static double Exhaust(StageRow row)
        {
            if (row.DeltaVVacuum <= 0.0 || row.EndMass <= 0.0 || row.StartMass <= row.EndMass) return 0.0;
            return row.DeltaVVacuum / Math.Log(row.StartMass / row.EndMass);
        }
    }
}
