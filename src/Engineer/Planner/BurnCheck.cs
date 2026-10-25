using System;
using System.Collections.Generic;

namespace ReDefinition.Engineer
{
    // A stage's part of a step as the burns take it: its delta-v, the mass it pushes
    // as it starts and ends that part, in tonnes, and its thrust in vacuum, in kN.
    internal sealed class BurnStage
    {
        // The stage's number; its delta-v for this part, in m/s; its masses as it starts and ends it; its thrust in vacuum.
        public int Stage;
        public double DeltaV;
        public double Mass;
        public double EndMass;
        public double Thrust;

        // The time the stage burns for its part.
        public double Seconds
        {
            get { return BurnCheck.Seconds(this, Mass, DeltaV); }
        }
    }

    // One burn of a step in a circular orbit: how long it takes, against how long the
    // orbit takes.
    internal sealed class Burn
    {
        // The burn of the leg; how long it takes, and how long one round of its orbit takes, in seconds.
        public LegLine Line;
        public double Seconds;
        public double Period;
        // The stages had less than the burn, or a stage burning for it has no thrust:
        // Seconds is the part they cover.
        public bool Short;

        // The share of the orbit the vessel goes round while it burns.
        public double Share
        {
            get { return Period > 0.0 ? Seconds / Period : 0.0; }
        }

        // Of the burn's delta-v, what goes astray: burnt in one direction across the
        // arc of the orbit it spans, centred on the point it is meant for, only the
        // average of the cosine over that arc pushes where the burn should,
        // sin(a/2) / (a/2) for the arc a.
        public double Loss
        {
            get
            {
                double half = Math.PI * Share;
                if (half <= 1e-9) return 0.0;
                return half >= Math.PI ? 1.0 : 1.0 - Math.Sin(half) / half;
            }
        }
    }

    // How long a step's burns in orbit take with the stages that fly them
    // (docs/development/planner.md). The stages burn in their order, each at its
    // thrust in vacuum and its exhaust speed, the delta-v it gives over the log of
    // its start and end mass; the burns take the step's delta-v in their order, the
    // margin left over. Free of Unity, tested.
    internal static class BurnCheck
    {
        // Above this share of a burn's delta-v lost it warns, above the second it is
        // too long: about 11 % and 17 % of the orbit.
        internal const double WarnLoss = 0.02;
        internal const double MaxLoss = 0.05;

        // The leg's burns in a circular orbit, each with how long the stages take for it, burning on from where the burn
        // before left them; burns not in such an orbit only use up the stages' delta-v.
        internal static List<Burn> Burns(IList<PlannerBody> bodies, Leg leg, IList<BurnStage> stages)
        {
            List<Burn> burns = new List<Burn>();
            int at = 0;
            double used = 0.0;
            double mass = stages.Count > 0 ? stages[0].Mass : 0.0;
            foreach (LegLine line in leg.Lines)
            {
                double left = line.DeltaV;
                double seconds = 0.0;
                bool thrustless = false;
                while (left > 1e-6 && at < stages.Count)
                {
                    BurnStage stage = stages[at];
                    double take = Math.Min(left, stage.DeltaV - used);
                    if (take > 1e-9)
                    {
                        thrustless |= stage.Thrust <= 0.0;
                        seconds += Seconds(stage, mass, take);
                        mass = MassAfter(stage, mass, take);
                        used += take;
                        left -= take;
                    }
                    if (stage.DeltaV - used <= 1e-6)
                    {
                        at++;
                        used = 0.0;
                        if (at < stages.Count) mass = stages[at].Mass;
                    }
                }
                if (line.Body < 0 || line.Body >= bodies.Count || line.Radius <= 0.0) continue;
                PlannerBody body = bodies[line.Body];
                burns.Add(new Burn
                {
                    Line = line,
                    Seconds = seconds,
                    Period = body.Mu > 0.0 ? 2.0 * Math.PI * Math.Sqrt(Math.Pow(line.Radius, 3.0) / body.Mu) : 0.0,
                    Short = left > 1e-6 || thrustless,
                });
            }
            return burns;
        }

        // The time `deltaV` takes from `mass` on: the propellant it burns at the
        // stage's flow, thrust over exhaust speed.
        internal static double Seconds(BurnStage stage, double mass, double deltaV)
        {
            double exhaust = Exhaust(stage);
            if (exhaust <= 0.0 || stage.Thrust <= 0.0 || deltaV <= 0.0) return 0.0;
            // Tonnes and kN: kN / (m/s) is tonnes per second.
            return mass * (1.0 - Math.Exp(-deltaV / exhaust)) * exhaust / stage.Thrust;
        }

        // The mass after the stage has burnt `deltaV` from `mass` on (Tsiolkovsky).
        private static double MassAfter(BurnStage stage, double mass, double deltaV)
        {
            double exhaust = Exhaust(stage);
            return exhaust > 0.0 ? mass * Math.Exp(-deltaV / exhaust) : mass;
        }

        // The stage's exhaust speed, in m/s; 0 where it gives no delta-v.
        internal static double Exhaust(BurnStage stage)
        {
            if (stage.DeltaV <= 0.0 || stage.EndMass <= 0.0 || stage.Mass <= stage.EndMass) return 0.0;
            return stage.DeltaV / Math.Log(stage.Mass / stage.EndMass);
        }
    }
}
