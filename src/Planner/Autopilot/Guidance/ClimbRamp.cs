using System;
using ReDefinition.Planner.Orbits;

namespace ReDefinition.Planner.Autopilot.Guidance
{
    // The end of a climb to an orbit: the throttle falls evenly in time to none, and the apoapsis reaches the height aimed for at the moment it is none.
    // How long the throttle still has to fall is worked out by flying the rest of the climb ahead on paper. The engines push along the heading of the
    // climb with the throttle that falls, the fuel they burn makes the vessel lighter, and the orbit carries it on. The time over which the apoapsis
    // comes to the height aimed for is the one searched. The throttle asked for now is that time over the whole time of the fall. It is asked again in
    // every frame, so that the fall is even and ends at the aim.
    internal static class ClimbRamp
    {
        // The steps of the flight ahead, in s, and how often the interval of the time is halved: the time is found to within a hundredth of a second.
        private const double StepSeconds = 1.0;
        private const int Halvings = 11;

        // The direction the thrust points in at a position, in the world's axes: over the horizon by the pitch, towards the east, which is the way the body turns.
        internal static V3 Heading(V3 position, double pitchDegrees, V3 axis)
        {
            V3 up = position.Unit;
            V3 north = (axis - up * V3.Dot(axis, up)).Unit;
            V3 east = V3.Cross(up, north);
            double radians = pitchDegrees * Math.PI / 180.0;
            return up * Math.Sin(radians) + east * Math.Cos(radians);
        }

        // The apoapsis radius reached when the throttle falls evenly from where it is to none over `seconds`, while it would fall from full to none over
        // `fall`. The force of the engines at full throttle is given in kN, the mass they burn at full throttle in t/s, and the vessel's mass in t.
        // The heading gives the direction of the thrust at a position. Each step is half a coast, a push, and half a coast again.
        internal static double ApoapsisAfter(State from, double mu, Func<V3, V3> heading, double thrust, double flow, double mass, double seconds, double fall)
        {
            State state = from;
            double weight = mass;
            int steps = Math.Max(1, (int)Math.Ceiling(seconds / StepSeconds));
            double dt = seconds / steps;
            for (int i = 0; i < steps; i++)
            {
                double throttle = Math.Max(0.0, (seconds - (i + 0.5) * dt) / fall);
                state = Kepler.Propagate(state, 0.5 * dt, mu);
                double push = weight > 0.0 ? thrust * throttle / weight * dt : 0.0;
                state = new State(state.R, state.V + heading(state.R) * push);
                state = Kepler.Propagate(state, 0.5 * dt, mu);
                weight -= flow * throttle * dt;
            }
            return ClimbGuide.ApoapsisRadius(state, mu);
        }

        // How much longer the throttle falls, in s, for the apoapsis to reach `aimRadius`: the whole `fall` where even a fall from full throttle does not reach it,
        // and nothing where the apoapsis is high enough.
        internal static double SecondsLeft(State from, double mu, double aimRadius, Func<V3, V3> heading, double thrust, double flow, double mass, double fall)
        {
            if (!(thrust > 0.0) || !(mass > 0.0)) return fall;
            if (ApoapsisAfter(from, mu, heading, thrust, flow, mass, fall, fall) < aimRadius) return fall;
            if (ClimbGuide.ApoapsisRadius(from, mu) >= aimRadius) return 0.0;
            double low = 0.0, high = fall;
            for (int i = 0; i < Halvings; i++)
            {
                double mid = 0.5 * (low + high);
                if (ApoapsisAfter(from, mu, heading, thrust, flow, mass, mid, fall) < aimRadius) low = mid;
                else high = mid;
            }
            return 0.5 * (low + high);
        }
    }
}
