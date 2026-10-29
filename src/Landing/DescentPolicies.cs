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
            double altitude = pos.magnitude - terrainRadius;
            return 0.9 * Math.Sqrt(2 * (thrust - g) * altitude);
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
}
