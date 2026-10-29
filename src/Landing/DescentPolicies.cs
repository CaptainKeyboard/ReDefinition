using System;
using UnityEngine;

namespace ReDefinition.Landing
{
    // The limit of a descent that is straight down: the speed from which the engines still stop the vessel in the height that is left (MechJeb's
    // SafeDescentSpeedPolicy).
    internal sealed class SafeDescentSpeedPolicy : IDescentSpeedPolicy
    {
        private readonly double terrainRadius;
        private readonly double g;
        private readonly double thrust;

        public SafeDescentSpeedPolicy(double terrainRadius, double g, double thrust)
        {
            this.terrainRadius = terrainRadius;
            this.g = g;
            this.thrust = thrust;
        }

        public double MaxAllowedSpeed(Vector3d pos, Vector3d vel)
        {
            return Limit(pos.magnitude - terrainRadius, thrust, g);
        }

        // The limit `altitude` metres over the ground the brake ends on. It is the speed from which engines of `thrust` m/s2 against a
        // gravity of `g` still stop the vessel in the height that is left, taking the whole speed as downward.
        internal static double Limit(double altitude, double thrust, double g)
        {
            return 0.9 * Math.Sqrt(Math.Max(0.0, 2 * (thrust - g) * altitude));
        }
    }

    // The limit where the air does the braking: no limit above the ground, and under it the speed the thrust can take off in the time left
    // (MechJeb's PoweredCoastDescentSpeedPolicy).
    internal sealed class PoweredCoastDescentSpeedPolicy : IDescentSpeedPolicy
    {
        private readonly float terrainRadius;
        private readonly float g;
        private readonly float thrust;

        public PoweredCoastDescentSpeedPolicy(double terrainRadius, double g, double thrust)
        {
            this.terrainRadius = (float)terrainRadius;
            this.g = (float)g;
            this.thrust = (float)thrust;
        }

        public double MaxAllowedSpeed(Vector3d pos, Vector3d vel)
        {
            if (terrainRadius < pos.magnitude) return double.MaxValue;
            double vSpeed = Vector3d.Dot(vel, pos.normalized);
            double toF = (vSpeed + Math.Sqrt(vSpeed * vSpeed + 2 * g * (pos.magnitude - terrainRadius))) / g;
            return 0.8 * (thrust - g) * toF;
        }
    }

    // The limit of a descent that brakes along the velocity: the fastest speed from which braking at full thrust against gravity still stops
    // above the ground. It is found by halving, with the way down worked out in ten steps of a burn that turns with the velocity (MechJeb's
    // GravityTurnDescentSpeedPolicy).
    internal sealed class GravityTurnDescentSpeedPolicy : IDescentSpeedPolicy
    {
        private readonly double terrainRadius;
        private readonly double g;
        private readonly double thrust;

        public GravityTurnDescentSpeedPolicy(double terrainRadius, double g, double thrust)
        {
            this.terrainRadius = terrainRadius;
            this.g = g;
            this.thrust = thrust;
        }

        public double MaxAllowedSpeed(Vector3d pos, Vector3d vel)
        {
            double maxFallDistance = pos.magnitude - terrainRadius;
            double lowerBound = 0;
            double upperBound = 1.1 * vel.magnitude;
            while (upperBound - lowerBound > 0.1)
            {
                double test = (upperBound + lowerBound) / 2;
                if (GravityTurnFallDistance(pos, test * vel.normalized) < maxFallDistance) lowerBound = test;
                else upperBound = test;
            }
            return 0.95 * ((upperBound + lowerBound) / 2);
        }

        private double GravityTurnFallDistance(Vector3d x, Vector3d v)
        {
            double startRadius = x.magnitude;
            const int Steps = 10;
            for (int i = 0; i < Steps; i++)
            {
                Vector3d gVec = -g * x.normalized;
                Vector3d thrustVec = -thrust * v.normalized;
                double dt = 1.0 / (Steps - i) * (v.magnitude / thrust);
                Vector3d newV = v + dt * (thrustVec + gVec);
                x += dt * (v + newV) / 2;
                v = newV;
            }
            double endRadius = x.magnitude;
            endRadius -= v.sqrMagnitude / (2 * (thrust - g));
            return startRadius - endRadius;
        }
    }
}
