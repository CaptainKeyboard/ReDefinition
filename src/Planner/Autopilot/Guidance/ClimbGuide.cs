using System;
using UnityEngine;
using ReDefinition.Autopilot;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Orbits;

namespace ReDefinition.Autopilot.Guidance
{
    // What the navigation tells the player where there is no node to put in: how to fly the climb to an orbit. The values come from the
    // vessel's state in every second, from a gravity turn that is flat when the air is passed. The descent is told by LandingGuide. It does not
    // need MechJeb.
    internal static class ClimbGuide
    {
        // MechJeb's classic ascent path with its automatic values (MechJebModuleAscentSettings and MechJebModuleAscentClassicAutopilot, GPL-3.0). The
        // climb is straight up until the height or the speed of the turn's start, in air a twentieth of its depth, and 99 m/s. The pitch then
        // falls from 90 degrees to the horizon at the turn's end, along a curve with this exponent.
        private const double TurnShapeExponent = 0.4;
        private const double AutoTurnPercent = 0.05;
        private const double AutoTurnSpeedFactor = 18.5;
        // Where the turn ends: in air this share of its depth, without air this share of the orbit's height, and not above 30 km.
        private const double TurnEndShare = 0.85;
        private const double TurnEndWithoutAirLimit = 30000.0;
        // Without air the turn starts this high over the ground, in metres.
        private const double TurnStartOverGround = 25.0;

        // How long the ideal throttle is to take to fall from full thrust to none, in s: the player's limit. It falls evenly in time, and ClimbRamp works out
        // where on the fall the throttle is. MechJeb's own ascent throttles down over one second.
        internal const double RampSeconds = 20.0;

        // The burn that the fall covers, in m/s, with the engines giving `acceleration` at full thrust: the area under the fall of the throttle.
        internal static double RampBurn(double acceleration)
        {
            return 0.5 * acceleration * RampSeconds;
        }
        private const double LeastThrottle = 0.05;

        // The height the turn really started at: where the speed for it was reached under the height for it, as MechJeb remembers it.
        private static double actualTurnStart;
        private static Guid turnVessel;

        // The pitch to fly now, in degrees over the horizon, for a climb to an orbit at `aimAltitude`.
        private static double Pitch(Vessel vessel, double aimAltitude)
        {
            CelestialBody body = vessel.mainBody;
            if (turnVessel != vessel.id || vessel.LandedOrSplashed)
            {
                turnVessel = vessel.id;
                actualTurnStart = 0.0;
            }
            double start = body.atmosphere ? body.atmosphereDepth * AutoTurnPercent : vessel.terrainAltitude + TurnStartOverGround;
            double startSpeed = body.atmosphere ? AutoTurnSpeedFactor * AutoTurnSpeedFactor * AutoTurnSpeedFactor * 0.015625 : double.PositiveInfinity;
            double end = TurnEnd(body, aimAltitude);
            double height = vessel.altitude;
            actualTurnStart = Math.Min(actualTurnStart, start);
            if (height < start && vessel.srfSpeed < startSpeed)
            {
                actualTurnStart = Math.Max(actualTurnStart, height);
                return 90.0;
            }
            return PitchAtHeight(height, actualTurnStart, end);
        }

        // Where the turn ends, in metres over the sea: in air this share of its depth, without air this share of the orbit's height, and not above 30 km.
        private static double TurnEnd(CelestialBody body, double aimAltitude)
        {
            return body.atmosphere ? Math.Min(body.atmosphereDepth * TurnEndShare, aimAltitude) : Math.Min(TurnEndWithoutAirLimit, aimAltitude * TurnEndShare);
        }

        // The pitch of the turn at a height, in degrees over the horizon: 90 at the height the turn started, falling along the curve to none at its end.
        internal static double PitchAtHeight(double height, double turnStart, double end)
        {
            if (height > end || !(end > turnStart)) return 0.0;
            double fraction = Math.Pow(Math.Max(0.0, (height - turnStart) / (end - turnStart)), TurnShapeExponent);
            return Math.Max(0.01, Math.Min(89.99, 90.0 - fraction * 90.0));
        }

        // The direction to point along in the climb: up and over, as high as the pitch is, towards the heading.
        internal static Vector3d ClimbDirection(Vessel vessel, double aimAltitude)
        {
            CelestialBody body = vessel.mainBody;
            Vector3d up = (vessel.GetWorldPos3D() - body.position).normalized;
            Vector3d north = Vector3d.Exclude(up, body.RotationAxis).normalized;
            Vector3d east = Vector3d.Cross(up, north).normalized;
            double radians = Pitch(vessel, aimAltitude) * Math.PI / 180.0;
            return up * Math.Sin(radians) + east * Math.Cos(radians);
        }

        // The delta-v the climb still has to burn: the speed of a circular orbit at the height it aims for, less the speed the vessel has round the body.
        internal static double Remaining(Vessel vessel, double aimAltitude)
        {
            double circular = Math.Sqrt(vessel.mainBody.gravParameter / (vessel.mainBody.Radius + aimAltitude));
            return Math.Max(0.0, circular - vessel.obt_velocity.magnitude);
        }

        // The burn along `direction`, in m/s, that puts the apoapsis at `aimAltitude`: over zero where the apoapsis is too low, under it where it is too high.
        // It is found by halving the range of the burn, since the apoapsis rises with it.
        internal static double RaiseBurn(CelestialBody body, State at, V3 direction, double aimAltitude)
        {
            return RaiseBurn(body.gravParameter, body.Radius, at, direction, aimAltitude);
        }

        internal static double RaiseBurn(double mu, double radius, State at, V3 direction, double aimAltitude)
        {
            double target = radius + aimAltitude;
            double low = -at.V.Length, high = Math.Sqrt(2.0 * mu / at.R.Length);
            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (low + high);
                if (ApoapsisRadius(new State(at.R, at.V + direction * mid), mu) < target) low = mid;
                else high = mid;
            }
            return 0.5 * (low + high);
        }

        // The apoapsis radius of the orbit a state is on; infinite where the orbit is open.
        internal static double ApoapsisRadius(State state, double mu)
        {
            var conic = new Conic(state, mu);
            return conic.Closed ? conic.A * (1.0 + conic.E) : double.PositiveInfinity;
        }

        // How long the fall still lasts, as the last climb worked it out, for the log.
        internal static double SecondsLeft { get; private set; }

        // The throttle the even fall has now, 0 to 1: the time it still falls over, which flying the climb ahead on paper gives, over the time it takes to fall from full.
        private static double RampThrottle(Vessel vessel, State at, double aimAltitude)
        {
            CelestialBody body = vessel.mainBody;
            double thrust, flow;
            Engines.Output(vessel, out thrust, out flow);
            V3 axis = KspOrbits.ToV3(body.RotationAxis);
            double radius = body.Radius, end = TurnEnd(body, aimAltitude), start = actualTurnStart;
            Func<V3, V3> heading = position => ClimbRamp.Heading(position, PitchAtHeight(position.Length - radius, start, end), axis);
            double seconds = ClimbRamp.SecondsLeft(at, body.gravParameter, radius + aimAltitude, heading, thrust, flow, vessel.GetTotalMass(), RampSeconds);
            SecondsLeft = seconds;
            return Math.Min(1.0, seconds / RampSeconds);
        }

        // The climb to a circular orbit at `aimAltitude`: the pitch and heading to fly now, and the ideal throttle. The ideal is the lowest of three limits that change
        // smoothly. They are the air's pressure, a part's temperature (Heat), and the approach to the apoapsis aimed for. `burn` is the burn left, in m/s.
        internal static string Climb(Vessel vessel, double aimAltitude, out double ideal, out double burn, out bool done)
        {
            double pitch = Pitch(vessel, aimAltitude);
            double latitude = vessel.latitude * Math.PI / 180.0;
            double heading = 90.0;
            double inclination = 0.0;
            double ratio = Math.Cos(inclination * Math.PI / 180.0) / Math.Max(0.1, Math.Cos(latitude));
            if (ratio <= 1.0) heading = Math.Asin(ratio) * 180.0 / Math.PI;
            string reason = "";
            ideal = 0.0;
            // The burn is under zero where the apoapsis is beyond the aim: how much the speed is over.
            State at = KspOrbits.StateAt(vessel.orbit, Planetarium.GetUniversalTime());
            burn = at.R.Length > 0.0 ? RaiseBurn(vessel.mainBody, at, KspOrbits.ToV3(ClimbDirection(vessel, aimAltitude)).Unit, aimAltitude) : 0.0;
            bool high = vessel.orbit.ApA >= aimAltitude || burn <= GuideNode.LeastDeltaV;
            done = high;
            if (!high)
            {
                double allowed = Heat.Allowed(vessel, out reason);
                double ramp = RampThrottle(vessel, at, aimAltitude);
                // Not below the least throttle that MechJeb's ascent gives while it raises the apoapsis; it is cut when the burn is done.
                ramp = Math.Max(LeastThrottle, ramp);
                ideal = Math.Min(allowed, ramp);
                if (ramp < allowed) reason = "";
            }
            string engines = high
                ? "engines off, the apoapsis is high enough"
                : "throttle " + (ideal * 100.0).ToString("0") + " %" + (reason.Length > 0 && ideal < 1.0 ? " (" + reason + ")" : "") + " until the apoapsis is "
                  + (aimAltitude / 1000.0).ToString("0.#") + " km, now " + Math.Max(0.0, vessel.orbit.ApA / 1000.0).ToString("0.0") + " km";
            return "Climb: pitch " + pitch.ToString("0") + ", heading " + heading.ToString("0") + ", " + engines + ".";
        }
    }
}
