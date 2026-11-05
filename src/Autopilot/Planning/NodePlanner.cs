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

        // What a burn is for, for the flight's count of them.
        internal enum BurnPurpose
        {
            Other,
            // The burn that leaves the orbit for another body: an ejection from a planet or a moon's transfer.
            Transfer,
            // A burn that brings the periapsis at the target body to the aim after the transfer.
            Correction,
            // The burn at the target body's periapsis that makes the orbit round it.
            Capture,
            // A change of the orbit round the body the vessel is at: circularizing, lifting the periapsis, the two burns to an altitude.
            Orbit,
            // The burn that brings the vessel down to the ground or into the air.
            Deorbit,
        }

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
            // The stop of the route the step leads to, by its index in the route; -1 where the step belongs to none.
            public int Goal = -1;
            // Whether the vessel was at the stop before the one the step leads to, so that the burns to it are all still to come.
            public bool FromStop;
            // The altitude of the orbit an ascent ends in, in metres.
            public double Altitude;
            // True where the route is flown to its last stop, and there is nothing left to plan.
            public bool Arrived;
            public string Why;
            public double UT;
            public V3 DeltaV;
            // What the burn is for.
            public BurnPurpose Purpose;
            // Whether the plan is still being worked out in the background: neither a plan nor a failure, and to be asked again in a moment.
            public bool Pending;

            public static Result Failed(string why)
            {
                return new Result { Why = why };
            }

            public static Result Searching(string why)
            {
                return new Result { Pending = true, Why = why };
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
