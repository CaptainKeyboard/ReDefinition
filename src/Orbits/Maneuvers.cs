using System;

namespace ReDefinition.Orbits
{
    // A burn: the seconds after the state it was worked out from, and the change of velocity at that place, in the inertial frame.
    internal struct Burn
    {
        public readonly double Delay;
        public readonly V3 DeltaV;

        public Burn(double delay, V3 deltaV)
        {
            Delay = delay;
            DeltaV = deltaV;
        }

        public double Magnitude
        {
            get { return DeltaV.Length; }
        }
    }

    // The burns within one orbit: to change the orbit's shape at an apsis and its plane at a node.
    internal static class Maneuvers
    {
        // A burn at the apsis the body reaches next that puts the apsis opposite to it at `newRadius`: a tangential burn. With the new radius the
        // apsis's own, it makes the orbit circular. False on an open orbit.
        internal static bool ChangeOpposite(State state, double mu, bool atApoapsis, double newRadius, out Burn burn)
        {
            burn = new Burn();
            var orbit = new Conic(state, mu);
            if (!orbit.Closed) return false;
            double delay = atApoapsis ? orbit.TimeToApoapsis : orbit.TimeToPeriapsis;
            State there = Kepler.Propagate(state, delay, mu);
            double radius = there.R.Length;
            double semiMajor = 0.5 * (radius + newRadius);
            double speed = Math.Sqrt(mu * (2.0 / radius - 1.0 / semiMajor));
            V3 along = there.V.Unit;
            burn = new Burn(delay, along * (speed - there.V.Length));
            return true;
        }

        // Circularises at the apoapsis or the periapsis, whichever is asked.
        internal static bool Circularize(State state, double mu, bool atApoapsis, out Burn burn)
        {
            var orbit = new Conic(state, mu);
            double radius = orbit.Closed ? orbit.RadiusAt(atApoapsis ? Math.PI : 0.0) : 0.0;
            return ChangeOpposite(state, mu, atApoapsis, radius, out burn);
        }

        // The burn at the nearer node of the orbit's plane with another plane, `targetNormal` its normal, that turns the orbit into that plane: the
        // velocity turned about the line of nodes, its speed kept. False where the planes are the same.
        internal static bool MatchPlane(State state, double mu, V3 targetNormal, out Burn burn)
        {
            burn = new Burn();
            var orbit = new Conic(state, mu);
            V3 normal = orbit.H.Unit;
            V3 target = targetNormal.Unit;
            double tilt = V3.Angle(normal, target);
            V3 line = V3.Cross(normal, target);
            if (tilt < 1e-9 || line.Length < 1e-12) return false;
            V3 node = line.Unit;
            double ascending = orbit.TimeToTrueAnomaly(orbit.TrueAnomalyOf(node));
            double descending = orbit.TimeToTrueAnomaly(orbit.TrueAnomalyOf(-node));
            bool useAscending = double.IsNaN(descending) || (!double.IsNaN(ascending) && ascending <= descending);
            double delay = useAscending ? ascending : descending;
            if (double.IsNaN(delay)) return false;
            State there = Kepler.Propagate(state, delay, mu);
            V3 axis = useAscending ? node : -node;
            V3 turned = there.V.Rotated(axis, tilt);
            V3 other = there.V.Rotated(axis, -tilt);
            V3 chosen = V3.Dot(V3.Cross(there.R, turned), target) >= V3.Dot(V3.Cross(there.R, other), target) ? turned : other;
            burn = new Burn(delay, chosen - there.V);
            return true;
        }

        // The three directions of KSP's maneuver node frame at a state: radial out, normal (the orbit's own, north for an eastward orbit) and
        // prograde. Unity's world is left-handed, so there the cross product of position and velocity points the other way: `leftHanded`.
        private static void NodeAxes(State at, bool leftHanded, out V3 radial, out V3 normal, out V3 prograde)
        {
            prograde = at.V.Unit;
            V3 out0 = at.R.Unit;
            radial = (out0 - prograde * V3.Dot(out0, prograde)).Unit;
            normal = V3.Cross(at.R, at.V).Unit * (leftHanded ? -1.0 : 1.0);
        }

        // A change of velocity in KSP's maneuver node frame: radial out, normal, prograde, at a state.
        internal static V3 ToNodeFrame(State at, V3 deltaV, bool leftHanded = false)
        {
            V3 radial, normal, prograde;
            NodeAxes(at, leftHanded, out radial, out normal, out prograde);
            return new V3(V3.Dot(deltaV, radial), V3.Dot(deltaV, normal), V3.Dot(deltaV, prograde));
        }

        // The other way: a change of velocity from the node frame to the inertial one.
        internal static V3 FromNodeFrame(State at, V3 nodeDeltaV, bool leftHanded = false)
        {
            V3 radial, normal, prograde;
            NodeAxes(at, leftHanded, out radial, out normal, out prograde);
            return radial * nodeDeltaV.X + normal * nodeDeltaV.Y + prograde * nodeDeltaV.Z;
        }
    }
}
