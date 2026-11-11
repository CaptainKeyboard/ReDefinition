using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // A plain triple of doubles for the wind in a frame: the wind is rotated into the world in double precision every frame and never
    // goes through float before the final hand-over to the aerodynamics.
    internal struct Vec3
    {
        internal double X, Y, Z;

        internal Vec3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        internal double Length { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }

        internal double HorizontalLength { get { return Math.Sqrt(X * X + Y * Y); } }

        public static Vec3 operator +(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }

        public static Vec3 operator -(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }

        public static Vec3 operator *(Vec3 a, double s) { return new Vec3(a.X * s, a.Y * s, a.Z * s); }

        internal static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }

        internal bool IsFinite
        {
            get { return !(double.IsNaN(X) || double.IsInfinity(X) || double.IsNaN(Y) || double.IsInfinity(Y) || double.IsNaN(Z) || double.IsInfinity(Z)); }
        }
    }

    // The small pure functions of the wind that acts on vehicles (package D31): the frame rotation, the height and density profile, the
    // speed cap, the slew limit, the ramp after a vessel unpacks, the cut-off at extreme spin and the acceleration clamp. No UnityEngine
    // objects, any thread, nothing allocated.
    internal static class WindMath
    {
        // The air density in kg/m3 below which the wind fades out and above which it is full (sea level of Kerbin is 1.225). [VERIFY the
        // numbers in a flight: the fade is meant to take the jet-stream winds out of the thin air.]
        internal const double DensityFadeFrom = 0.004, DensityFull = 0.08;
        // The seconds a vessel's wind ramps up after it unpacks or loads.
        internal const double RampSeconds = 3.0;
        // The angular speed in rad/s above which the wind is off (the vessel tumbles; a force on it would feed the spin).
        internal const double MaxAngularSpeed = 6.0;
        // The level (0..1) between which the speed cap goes from maxWind to the violent wind.
        internal const double ViolentFromLevel = 0.4, ViolentFullLevel = 0.8;

        // 0 below a, 1 above b, smooth in between.
        internal static double SmoothStep(double a, double b, double x)
        {
            if (b <= a) return x >= b ? 1.0 : 0.0;
            double t = (x - a) / (b - a);
            t = t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
            return t * t * (3.0 - 2.0 * t);
        }

        // The share of the wind at a height above the ground: groundFactor on the ground, rising smoothly to 1 at fullHeightM. An unknown
        // height (negative or NaN) counts as high.
        internal static double HeightProfile(double heightAboveGroundM, double groundFactor, double fullHeightM)
        {
            if (double.IsNaN(heightAboveGroundM) || heightAboveGroundM < 0.0) return 1.0;
            double g = groundFactor < 0.0 ? 0.0 : groundFactor > 1.0 ? 1.0 : groundFactor;
            return g + (1.0 - g) * SmoothStep(0.0, fullHeightM, heightAboveGroundM);
        }

        // The share of the wind in air of that density: 0 in vacuum, full in the dense air.
        internal static double DensityProfile(double densityKgM3)
        {
            if (double.IsNaN(densityKgM3) || densityKgM3 <= 0.0) return 0.0;
            return SmoothStep(DensityFadeFrom, DensityFull, densityKgM3);
        }

        // The largest horizontal wind speed a vehicle feels: maxWind in ordinary weather, rising to the violent wind in the worst.
        internal static double SpeedCap(double maxWindMs, double violentWindMs, double level01)
        {
            double top = violentWindMs > maxWindMs ? violentWindMs : maxWindMs;
            return maxWindMs + (top - maxWindMs) * SmoothStep(ViolentFromLevel, ViolentFullLevel, level01);
        }

        // The wind rotated from the east-north-up frame of the place into the world, from the world's unit vectors of east, north and up.
        internal static Vec3 EnuToWorld(double east, double north, double up, Vec3 eastWorld, Vec3 northWorld, Vec3 upWorld)
        {
            return new Vec3(
                east * eastWorld.X + north * northWorld.X + up * upWorld.X,
                east * eastWorld.Y + north * northWorld.Y + up * upWorld.Y,
                east * eastWorld.Z + north * northWorld.Z + up * upWorld.Z);
        }

        // The slew rate in force: the body's node value, never above the shared limit of WindNoise.LimitWind (CONTRACT.md section 3: every
        // consumer that writes a wind speed holds to it). The vector slew changes the speed by at most the vector's step, so this keeps the
        // speed within what LimitWind allows.
        internal static double EffectiveSlew(double nodeMaxSlewMsPerS)
        {
            double shared = WindNoise.MaxWindSlewMsPerS;
            return nodeMaxSlewMsPerS < shared ? nodeMaxSlewMsPerS : shared;
        }

        // The wind one step on: it moves towards the target by at most maxSlewMsPerS * dt (as a vector, so the direction cannot jump either).
        internal static Vec3 Slew(Vec3 previous, Vec3 target, double maxSlewMsPerS, double dt)
        {
            Vec3 d = target - previous;
            double len = d.Length;
            double step = (maxSlewMsPerS < 0.0 ? 0.0 : maxSlewMsPerS) * (dt < 0.0 ? 0.0 : dt);
            if (len <= step || len < 1e-12) return target;
            return previous + d * (step / len);
        }

        // The share of the wind this long after the vessel unpacked: 0, rising linearly to 1 over RampSeconds.
        internal static double Ramp(double secondsSinceUnpack)
        {
            if (double.IsNaN(secondsSinceUnpack) || secondsSinceUnpack <= 0.0) return 0.0;
            return secondsSinceUnpack >= RampSeconds ? 1.0 : secondsSinceUnpack / RampSeconds;
        }

        // True where the vessel spins too fast for the wind to act.
        internal static bool SpinTooFast(double angularSpeedRadS)
        {
            return double.IsNaN(angularSpeedRadS) || angularSpeedRadS > MaxAngularSpeed;
        }

        // The extra acceleration in m/s2 that a wind of w makes against the drag of a vehicle moving at v through air of that density, where
        // dragPerMass is the vessel's drag area over its mass (m2 per tonne, in the units of KSP's kN and tonnes) as the last tick measured
        // it: the drag force is k |u| u with u the air-relative velocity, k = 0.5 rho dragPerMass / 1000 per tonne, so the extra is
        // k | |v - w| (v - w) - |v| v |. An estimate for the clamp, not a force.
        internal static double ExtraAcceleration(double densityKgM3, double dragPerMass, Vec3 v, Vec3 w)
        {
            Vec3 u = v - w;
            double lu = u.Length, lv = v.Length;
            Vec3 diff = u * lu - v * lv;
            return 0.5 * densityKgM3 * dragPerMass * 0.001 * diff.Length;
        }

        // The share s of a wind (0..1) that keeps the extra acceleration at or under maxAccel: found by bisection, 1 where the whole wind
        // fits. The wind is scaled, not cut off, so the direction is kept.
        internal static double ClampShare(double densityKgM3, double dragPerMass, Vec3 v, Vec3 w, double maxAccel)
        {
            if (!(maxAccel > 0.0)) return 0.0;
            if (ExtraAcceleration(densityKgM3, dragPerMass, v, w) <= maxAccel) return 1.0;
            double lo = 0.0, hi = 1.0;
            for (int i = 0; i < 12; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (ExtraAcceleration(densityKgM3, dragPerMass, v, w * mid) <= maxAccel) lo = mid; else hi = mid;
            }
            return lo;
        }
    }
}
