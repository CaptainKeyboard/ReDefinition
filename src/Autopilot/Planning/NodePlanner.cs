using System;
using ReDefinition.Orbits;
using UnityEngine;

namespace ReDefinition.Autopilot.Planning
{
    // The burns of a leg worked out from the vessel's orbit and put in the flight plan as nodes, each checked against KSP's own patched conics.
    internal static partial class NodePlanner
    {
        // The deorbit burn goes on until the periapsis is this part of the body's radius under the ground, with air or without (MechJeb's untargeted
        // landing, UntargetedDeorbit: a tenth).
        private const double UntargetedPeriapsis = 0.1;

        // The least time between now and a burn, in seconds: the time MechJeb's rendezvous planner puts its first burn ahead (MechJebModuleRendezvousGuidance,
        // leadTime).
        internal const double LeadTime = 30.0;

        // What a step of a plan is: a burn as a node, a launch into an orbit, or a descent to the ground.
        internal enum StepKind
        {
            Burn,
            Ascent,
            Landing,
            Aerobrake,
            Release,
            Rendezvous,
        }

        // What a plan came to: a burn at a time, and why, or the reason there is none. Ok with a time of zero is a plan that needs no burn.
        internal sealed class Result
        {
            public bool Ok;
            public StepKind Kind;
            // The altitude of the orbit an ascent ends in, in metres.
            public double Altitude;
            // True where the route is flown to its last stop, and there is nothing left to plan.
            public bool Arrived;
            public string Why;
            public double UT;
            public V3 DeltaV;

            public static Result Failed(string why)
            {
                return new Result { Why = why };
            }

            public static Result Ascent(double altitude, string why)
            {
                return new Result { Ok = true, Kind = StepKind.Ascent, Altitude = altitude, Why = why };
            }

            public static Result Landing(string why)
            {
                return new Result { Ok = true, Kind = StepKind.Landing, Why = why };
            }

            // The part to separate, for a Release step.
            public SectionRelease Section;
            // A correction of the course: how far the periapsis was from the one aimed at before the burn, in metres.
            public bool Correction;
            public double Error;

            // The vessel to meet, and the docking port to dock with where there is one (its part's persistent id), for a Rendezvous step.
            public System.Guid Target;
            public uint Port;

            // Whether the step ends with a docking, at the port above or, where it is zero, at a free one of the target's.
            public bool Dock;

            public static Result Rendezvous(System.Guid target, uint port, bool dock, string why)
            {
                return new Result { Ok = true, Kind = StepKind.Rendezvous, Target = target, Port = port, Dock = dock, Why = why };
            }

            public static Result Release(SectionRelease section, string why)
            {
                return new Result { Ok = true, Kind = StepKind.Release, Section = section, Why = why };
            }

            public static Result Aerobrake(string why)
            {
                return new Result { Ok = true, Kind = StepKind.Aerobrake, Why = why };
            }

            public static Result Done(string why)
            {
                return new Result { Ok = true, Arrived = true, Why = why };
            }
        }

        // How far an apsis may lie from the altitude of an orbit and still count as on it, in metres: a tenth of the altitude, and two kilometres
        // at least.
        internal static double Near(double altitude)
        {
            return Math.Max(2000.0, 0.1 * altitude);
        }
    }
}
