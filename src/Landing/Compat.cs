using System;
using UnityEngine;

namespace ReDefinition.Landing
{
    // The small helpers MechJeb's simulation calls, from MechJeb 2 (OrbitExtensions, MuUtils, MathExtensions, CelestialBodyExtensions and
    // MechJebLib's Statics, GPL-3.0), reduced to what the simulation uses.
    internal static class Compat
    {
        private const double Tau = 2.0 * Math.PI;

        public static double Clamp360(double x)
        {
            x %= 360.0;
            x = x < 0.0 ? x + 360.0 : x;
            return x >= 360.0 ? 0.0 : x;
        }

        public static double Clamp180(double x)
        {
            x = Clamp360(x);
            return x > 180.0 ? x - 360.0 : x;
        }

        public static double Clamp2Pi(double x)
        {
            x %= Tau;
            x = x < 0.0 ? x + Tau : x;
            return x >= Tau ? 0.0 : x;
        }

        public static Vector3d ProjectOnPlane(this Vector3d vector, Vector3d planeNormal)
        {
            return vector - Vector3d.Project(vector, planeNormal);
        }

        public static double RealMaxAtmosphereAltitude(this CelestialBody body)
        {
            return !body.atmosphere ? 0.0 : body.atmosphereDepth;
        }

        public static Vector3d WorldOrbitalVelocityAtUT(this Orbit o, double ut)
        {
            return o.getOrbitalVelocityAtUT(ut).xzy;
        }

        public static Vector3d WorldBCIPositionAtUT(this Orbit o, double ut)
        {
            return o.getRelativePositionAtUT(ut).xzy;
        }

        public static double MeanAnomalyAtUT(this Orbit o, double ut)
        {
            double ret = (o.ObTAtEpoch + (ut - o.epoch)) * o.meanMotion;
            if (o.eccentricity < 1.0) ret = Clamp2Pi(ret);
            return ret;
        }

        // The next time at the periapsis: on a closed orbit within one period, on an open one any time, a past one too.
        public static double NextPeriapsisTime(this Orbit o, double ut)
        {
            double mean = o.MeanAnomalyAtUT(ut);
            return o.eccentricity < 1.0 ? ut + Clamp2Pi(-mean) / o.meanMotion : ut - mean / o.meanMotion;
        }

        // An orbit from a position and a velocity in the world's axes, with the argument of periapsis made right where the game's own leaves it
        // undefined by rounding.
        public static Orbit OrbitFromStateVectors(Vector3d position, Vector3d velocity, CelestialBody body, double ut)
        {
            var ret = new Orbit();
            ret.UpdateFromStateVectors((position - body.position).xzy, velocity.xzy, body, ut);
            if (double.IsNaN(ret.argumentOfPeriapsis))
            {
                Vector3d toAscendingNode = Quaternion.AngleAxis(-(float)ret.LAN, Planetarium.up) * Planetarium.right;
                Vector3d toPeriapsis = ret.eccVec.xzy;
                double cosine = Vector3d.Dot(toAscendingNode, toPeriapsis) / (toAscendingNode.magnitude * toPeriapsis.magnitude);
                if (cosine > 1.0) ret.argumentOfPeriapsis = 0.0;
                else if (cosine < -1.0) ret.argumentOfPeriapsis = 180.0;
                else ret.argumentOfPeriapsis = Math.Acos(cosine);
            }
            return ret;
        }

        public static bool PhysicsRunning()
        {
            return TimeWarp.WarpMode == TimeWarp.Modes.LOW || TimeWarp.CurrentRateIndex == 0;
        }
    }
}
