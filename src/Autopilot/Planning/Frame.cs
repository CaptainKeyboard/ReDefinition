using UnityEngine;

namespace ReDefinition.Autopilot.Planning
{
    // The state a step of the plan starts from: the vessel as it is, or, once steps were planned ahead, the vessel as it will be at the end of
    // them. The orbit and the body are read from the vessel's flight plan at that time, where the nodes of the earlier steps stand, so that the
    // steps chain without the vessel having burned.
    internal sealed class Frame
    {
        // A little after a node, where the patch that follows it has begun.
        internal const double AfterNode = 0.001;

        internal readonly Vessel Vessel;
        // The UT the state is at: now for the vessel as it is.
        internal readonly double Now;
        // Whether the state lies in the plan ahead of the vessel rather than being the vessel's own.
        internal readonly bool Ahead;

        internal Frame(Vessel vessel)
        {
            Vessel = vessel;
            Now = Planetarium.GetUniversalTime();
        }

        private Frame(Vessel vessel, double ut)
        {
            Vessel = vessel;
            Now = ut;
            Ahead = true;
        }

        // The state at `ut` on the flight plan, after the nodes that come before it.
        internal Frame At(double ut)
        {
            return new Frame(Vessel, ut);
        }

        // The orbit the vessel is on in this state: its own, or the patch of the flight plan that covers the time.
        internal Orbit Orbit
        {
            get { return Ahead ? KspOrbits.OrbitAt(Vessel, Now + AfterNode) : Vessel.orbit; }
        }

        internal CelestialBody Body
        {
            get { return Ahead ? Orbit.referenceBody : Vessel.mainBody; }
        }

        internal PatchedConicSolver Solver
        {
            get { return Vessel.patchedConicSolver; }
        }

        // The situation: the vessel's own, or in the plan ahead an orbit round the body, or an escape from it.
        internal Vessel.Situations Situation
        {
            get { return !Ahead ? Vessel.situation : Orbit.eccentricity < 1.0 ? Vessel.Situations.ORBITING : Vessel.Situations.ESCAPING; }
        }

        // Whether the vessel stands on the ground, or is on the pad; never in the plan ahead, which starts in the air.
        internal bool OnGround
        {
            get { return !Ahead && (Vessel.LandedOrSplashed || Vessel.situation == Vessel.Situations.PRELAUNCH); }
        }
    }
}
