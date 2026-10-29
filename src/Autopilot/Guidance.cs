using System;
using System.Collections.Generic;
using ReDefinition.Orbits;
using UnityEngine;

namespace ReDefinition.Autopilot
{
    // The descent's speed limit and throttle follow MechJeb's landing (MechJebModuleLandingAutopilot: GravityTurnDescentSpeedPolicy and
    // DecelerationBurn, GPL-3.0), adapted to read the vessel's state without MechJeb. What the autonavigation tells the player where there is no node to put in: how to fly the climb to an orbit and the descent to the
    // ground. The values come from the vessel's state in every second, the climb from a gravity turn that is flat when the air is passed, and
    // the descent from the distance the engines need to stop the vessel. It does not need MechJeb.
    internal static class Guidance
    {
        // Where the pitch reaches the horizon, as a part of the air's depth; for a body without air, a fixed height in metres.
        private const double TurnEndInAir = 0.55;
        private const double TurnEndWithoutAir = 12000.0;
        // The height over which the climb goes straight up, and the speed it needs for the turn to begin.
        private const double VerticalHeight = 250.0;
        private const double TurnSpeed = 60.0;
        // The stopping distance is kept this many times clear of the ground: gravity bends the way down while the vessel brakes, and a burn
        // started early costs little. MechJeb's landing, which set a vessel down softly in the same orbit, began about here.
        private const double BrakingMargin = 2.0;

        // The climb to a circular orbit at `aimAltitude`: the pitch and heading to fly now, and what the engines do.
        internal static string Climb(Vessel vessel, double aimAltitude)
        {
            CelestialBody body = vessel.mainBody;
            double height = vessel.altitude;
            double turnEnd = body.atmosphere ? TurnEndInAir * body.atmosphereDepth : TurnEndWithoutAir;
            double pitch = 90.0;
            if (height > VerticalHeight && vessel.srfSpeed > TurnSpeed)
                pitch = 90.0 * (1.0 - Math.Sqrt(Math.Min(1.0, (height - VerticalHeight) / (turnEnd - VerticalHeight))));
            double latitude = vessel.latitude * Math.PI / 180.0;
            double heading = 90.0;
            double inclination = 0.0;
            double ratio = Math.Cos(inclination * Math.PI / 180.0) / Math.Max(0.1, Math.Cos(latitude));
            if (ratio <= 1.0) heading = Math.Asin(ratio) * 180.0 / Math.PI;
            string engines = vessel.orbit.ApA < aimAltitude
                ? "full throttle until the apoapsis is " + (aimAltitude / 1000.0).ToString("0.#") + " km, now " + Math.Max(0.0, vessel.orbit.ApA / 1000.0).ToString("0.0") + " km"
                : "engines off, the apoapsis is high enough";
            return "Climb: pitch " + pitch.ToString("0") + ", heading " + heading.ToString("0") + ", " + engines + ".";
        }

        // The factor on the limit that MechJeb keeps, and how fast the throttle follows the limit (its constant, in seconds).
        private const double LimitFactor = 0.95;
        private const double FollowTime = 0.3;
        // The slowest speed the guidance asks for near the ground, so that the vessel comes down and does not hang.
        private const double TouchdownSpeed = 1.5;
        // How long the highest ground the vessel passed over counts as the ground it has to clear, in seconds.
        private const float GroundMemory = 30f;

        private static readonly Queue<KeyValuePair<float, double>> ground = new Queue<KeyValuePair<float, double>>();
        private static Vessel groundOf;

        // The radius of the ground the descent has to clear: the highest terrain under the vessel in the last half minute, so that the limit does
        // not jump with every hill. MechJeb takes the height of the landing site it predicts, which this does not know.
        private static double GroundRadius(Vessel vessel)
        {
            if (groundOf != vessel)
            {
                ground.Clear();
                groundOf = vessel;
            }
            float now = Time.realtimeSinceStartup;
            ground.Enqueue(new KeyValuePair<float, double>(now, Math.Max(0.0, vessel.terrainAltitude)));
            while (ground.Count > 0 && now - ground.Peek().Key > GroundMemory) ground.Dequeue();
            double highest = 0.0;
            foreach (KeyValuePair<float, double> sample in ground) highest = Math.Max(highest, sample.Value);
            return vessel.mainBody.Radius + highest;
        }

        // The radius of the highest ground on the last stretch of the path to where the orbit meets the ground, with the body turning under
        // it. It is the height of the landing site, which MechJeb's prediction gives its descent. Zero where the orbit does not meet the ground.
        private static double ImpactRadius(Vessel vessel)
        {
            CelestialBody body = vessel.mainBody;
            double now = Planetarium.GetUniversalTime();
            State start = KspOrbits.StateAt(vessel.orbit, now);
            var recent = new Queue<double>();
            for (double t = 0.0; t < Math.Min(vessel.orbit.period, 5400.0); t += 3.0)
            {
                State there = Kepler.Propagate(start, t, body.gravParameter);
                Vector3d world = new Vector3d(there.R.X, there.R.Y, there.R.Z) + body.position;
                double lat = body.GetLatitude(world);
                double lon = body.GetLongitude(world) - 360.0 * t / body.rotationPeriod;
                double terrain = Math.Max(0.0, body.TerrainAltitude(lat, lon));
                recent.Enqueue(terrain);
                if (recent.Count > 10) recent.Dequeue();
                if (there.R.Length - body.Radius > terrain) continue;
                double highest = 0.0;
                foreach (double sample in recent) highest = Math.Max(highest, sample);
                return body.Radius + highest;
            }
            return 0.0;
        }

        // The fastest speed, along the velocity, from which braking at full thrust against gravity still stops above the ground. It is found by
        // halving, with the way down worked out in ten steps of a burn that turns with the velocity (MechJeb's gravity turn).
        private static double MaxAllowedSpeed(Vector3d position, Vector3d velocity, double terrainRadius, double gravity, double thrust)
        {
            double fall = position.magnitude - terrainRadius;
            double low = 0.0, high = 1.1 * velocity.magnitude;
            while (high - low > 0.1)
            {
                double test = 0.5 * (high + low);
                if (GravityTurnFall(position, test * velocity.normalized, gravity, thrust) < fall) low = test;
                else high = test;
            }
            return LimitFactor * 0.5 * (high + low);
        }

        private static double GravityTurnFall(Vector3d x, Vector3d v, double gravity, double thrust)
        {
            double startRadius = x.magnitude;
            const int Steps = 10;
            for (int i = 0; i < Steps; i++)
            {
                Vector3d pull = -gravity * x.normalized;
                Vector3d push = -thrust * v.normalized;
                double dt = 1.0 / (Steps - i) * (v.magnitude / thrust);
                Vector3d newV = v + dt * (push + pull);
                x += dt * (v + newV) / 2;
                v = newV;
            }
            double endRadius = x.magnitude - v.sqrMagnitude / (2 * (thrust - gravity));
            return startRadius - endRadius;
        }

        // The speed limit for the height the vessel is at, and the throttle that follows it: full where the speed is well over the limit, none
        // where it is under. False where the engines are too weak to brake against gravity.
        internal static bool Limit(Vessel vessel, out double limit, out double throttle, out double height)
        {
            CelestialBody body = vessel.mainBody;
            Vector3d position = vessel.GetWorldPos3D() - body.position;
            Vector3d velocity = vessel.srf_velocity;
            double radiusSquared = position.sqrMagnitude;
            double gravity = body.gravParameter / radiusSquared;
            double thrust = Engines.Acceleration(vessel);
            double terrain = Math.Max(GroundRadius(vessel), ImpactRadius(vessel));
            height = position.magnitude - terrain;
            limit = 0.0;
            throttle = 0.0;
            if (thrust <= gravity || velocity.magnitude < 0.5) return thrust > gravity;
            limit = Math.Max(TouchdownSpeed, MaxAllowedSpeed(position, velocity, terrain, gravity, thrust));
            double vertical = Math.Abs(Vector3d.Dot(velocity.normalized, position.normalized));
            double controlled = velocity.magnitude * Math.Sign(Vector3d.Dot(velocity, position));
            double wanted = (-limit - controlled) / FollowTime;
            double least = -gravity * vertical;
            double most = thrust - gravity * vertical;
            throttle = most - least > 0.0 ? Math.Max(0.0, Math.Min(1.0, (wanted - least) / (most - least))) : 0.0;
            return true;
        }

        // The descent to the ground: the speed limit for the way that is left, and the throttle that follows it.
        internal static string Descent(Vessel vessel)
        {
            double speed = vessel.srfSpeed;
            double limit, throttle, height;
            double over = Math.Max(0.0, vessel.altitude - Math.Max(0.0, vessel.terrainAltitude));
            if (over < 30.0 && speed < 5.0) return "Descent: nearly down, touch down gently.";
            if (!Engines.Running(vessel) && speed > 20.0) return "Descent: no engine is lit, fire the next stage.";
            if (!Limit(vessel, out limit, out throttle, out height)) return "Descent: the engines are too weak to brake here.";
            string where = speed.ToString("0") + " m/s, limit " + limit.ToString("0") + " m/s, " + (over / 1000.0).ToString("0.0") + " km up";
            if (throttle < 0.02) return "Descent: point retrograde, no thrust yet, " + where + ".";
            return "Descent: retrograde, throttle " + (throttle * 100.0).ToString("0") + " %, " + where + ".";
        }
    }
}
