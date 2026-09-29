using System;
using UnityEngine;

namespace ReDefinition.Flight.Readouts
{
    // What the readouts read, taken once per refresh: the active vessel, its orbit
    // and body, its target, and the heavier sums, each worked out on first use.
    // Vectors are in KSP's orbit frame ("zup": z towards the body's north, as
    // Orbit.getRelativePositionAtUT gives them); the world's is its xzy.
    internal sealed class ReadoutFrame
    {
        internal readonly Vessel Vessel;
        internal readonly Orbit Orbit;
        internal readonly CelestialBody Body;
        internal readonly ITargetable Target;
        internal readonly double Now;

        internal ReadoutFrame(Vessel vessel)
        {
            Vessel = vessel;
            Orbit = vessel != null ? vessel.orbit : null;
            Body = vessel != null ? vessel.mainBody : null;
            Target = vessel != null ? vessel.targetObject : null;
            Now = Planetarium.GetUniversalTime();
        }

        internal bool Ready
        {
            get { return Vessel != null && Orbit != null && Body != null; }
        }

        internal bool Elliptic
        {
            get { return Orbit != null && Orbit.eccentricity < 1.0; }
        }

        // ------------------------------------------------------------ gravity

        // Gravity where the vessel is, in m/s^2.
        internal double Gravity
        {
            get
            {
                double r = (Vessel.CoMD - Body.position).magnitude;
                return r > 0.0 ? Body.gravParameter / (r * r) : 0.0;
            }
        }

        // ---------------------------------------------------------- engines

        private double maxThrust = double.NaN;

        // What the engines lit now give at full throttle where the vessel is, in kN:
        // each engine's full fuel flow times its Isp at the pressure here times g0,
        // at its thrust limit (ModuleEngines: maxFuelFlow, atmosphereCurve,
        // thrustPercentage), as KSP works an engine's thrust out.
        internal double MaxThrust
        {
            get
            {
                if (!double.IsNaN(maxThrust)) return maxThrust;
                double pressure = Vessel.staticPressurekPa * PhysicsGlobals.KpaToAtmospheres;
                double sum = 0.0;
                foreach (Part part in Vessel.parts)
                    foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
                    {
                        if (engine == null || !engine.EngineIgnited || !engine.isOperational || engine.flameout) continue;
                        double isp = engine.atmosphereCurve != null ? engine.atmosphereCurve.Evaluate((float)pressure) : 0.0;
                        sum += engine.maxFuelFlow * isp * engine.g * engine.thrustPercentage * 0.01;
                    }
                return maxThrust = sum;
            }
        }

        // What the engines give now, in kN.
        internal double Thrust
        {
            get
            {
                double sum = 0.0;
                foreach (Part part in Vessel.parts)
                    foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
                        if (engine != null && engine.EngineIgnited) sum += engine.finalThrust;
                return sum;
            }
        }

        // ----------------------------------------------------------- surface

        private double impact = double.NaN;
        private bool impactDone;

        internal double ImpactSeconds
        {
            get
            {
                if (!impactDone)
                {
                    impactDone = true;
                    impact = Vessel.verticalSpeed < 0.0 ? Impact.Seconds(Vessel) : double.NaN;
                }
                return impact;
            }
        }

        private bool suicideDone;
        private double suicideDistance = double.NaN, suicideSeconds = double.NaN, suicideDeltaV = double.NaN;

        // The last moment to brake to a stop over the ground, at the surface speed
        // now and full thrust (ReadoutMath.SuicideBurn): how high over the ground to
        // light, how long until then at the vertical speed now, and the delta-v.
        internal void SuicideBurn(out double altitude, out double countdown, out double deltaV)
        {
            if (!suicideDone)
            {
                suicideDone = true;
                double mass = Vessel.totalMass;
                if (Vessel.verticalSpeed < 0.0 && mass > 0.0 && !Vessel.LandedOrSplashed)
                {
                    double seconds;
                    ReadoutMath.SuicideBurn(Vessel.srfSpeed, MaxThrust / mass, Gravity, out suicideDistance, out seconds, out suicideDeltaV);
                    suicideSeconds = double.IsNaN(suicideDistance) ? double.NaN
                        : (Vessel.radarAltitude - suicideDistance) / -Vessel.verticalSpeed;
                }
            }
            altitude = suicideDistance;
            countdown = suicideSeconds;
            deltaV = suicideDeltaV;
        }

        private double slope = double.NaN;
        private bool slopeDone;

        // The ground's slope under the vessel, in degrees: the normal of KSP's
        // terrain collider (layer 15, local scenery) where a ray straight down meets
        // it, against the local vertical; NaN beyond 5 km.
        internal double Slope
        {
            get
            {
                if (slopeDone) return slope;
                slopeDone = true;
                Vector3 up = (Vessel.CoM - Body.transform.position).normalized;
                RaycastHit hit;
                if (Physics.Raycast(Vessel.CoM, -up, out hit, 5000f, 1 << 15))
                    slope = Vector3.Angle(hit.normal, up);
                return slope;
            }
        }

        // ------------------------------------------------------------- aero

        // The angle between the nose and the flight through the air, in the pitch
        // plane (angle of attack) and the yaw plane (sideslip), in degrees: the
        // surface velocity against the control point's up (the nose), forward (the
        // belly) and right.
        internal void AeroAngles(out double attack, out double sideslip)
        {
            Vector3 velocity = Vessel.srf_velocity;
            Transform reference = Vessel.ReferenceTransform;
            if (velocity.sqrMagnitude < 1.0 || reference == null)
            {
                attack = sideslip = double.NaN;
                return;
            }
            float along = Vector3.Dot(velocity, reference.up);
            attack = Math.Atan2(Vector3.Dot(velocity, reference.forward), along) * 180.0 / Math.PI;
            sideslip = Math.Atan2(Vector3.Dot(velocity, reference.right), along) * 180.0 / Math.PI;
        }

        // ------------------------------------------------------------- heat

        // The part nearest its limit: the higher of its inside's and its skin's
        // temperature against their maximum, as a share, and its name.
        internal double Hottest(out string name)
        {
            double worst = 0.0;
            name = "";
            foreach (Part part in Vessel.parts)
            {
                double share = Math.Max(part.maxTemp > 0.0 ? part.temperature / part.maxTemp : 0.0,
                                        part.skinMaxTemp > 0.0 ? part.skinTemperature / part.skinMaxTemp : 0.0);
                if (share <= worst) continue;
                worst = share;
                name = part.partInfo != null ? part.partInfo.title : part.name;
            }
            return worst;
        }

        // ------------------------------------------------------------- nodes

        // How long until the vessel crosses the plane with normal `normal` (zup)
        // going to its normal's side (ascending) or away from it; NaN where the
        // orbit does not cross it in a turn.
        internal double TimeToNode(Orbit orbit, Vector3d normal, bool ascending)
        {
            if (orbit == null || orbit.eccentricity >= 1.0) return double.NaN;
            Vector3d own = Vector3d.Cross(orbit.getRelativePositionAtUT(Now), orbit.getOrbitalVelocityAtUT(Now));
            Vector3d line = Vector3d.Cross(normal, own);
            if (line.sqrMagnitude < 1e-12) return double.NaN;
            double best = double.NaN;
            foreach (double sign in new[] { 1.0, -1.0 })
            {
                double anomaly = orbit.GetTrueAnomalyOfZupVector(line * sign);
                double at = orbit.GetUTforTrueAnomaly(anomaly, orbit.period);
                while (at < Now) at += orbit.period;
                double side = Vector3d.Dot(normal, orbit.getRelativePositionAtUT(at + 1.0));
                if (side > 0.0 == ascending && (double.IsNaN(best) || at - Now < best)) best = at - Now;
            }
            return best;
        }

        // ------------------------------------------------------------ target

        internal Orbit TargetOrbit
        {
            get { return Target != null ? Target.GetOrbit() : null; }
        }

        internal Vector3d TargetPosition
        {
            get { return Target != null && Target.GetTransform() != null ? (Vector3d)Target.GetTransform().position : Vector3d.zero; }
        }

        // The vessel's velocity against the target's, in the world's frame.
        internal Vector3d RelativeVelocity
        {
            get { return Target != null ? Vessel.obt_velocity - Target.GetObtVelocity() : Vector3d.zero; }
        }

        private bool approachDone;
        private double approachDistance = double.NaN, approachSeconds = double.NaN;

        // The closest the vessel comes to its target within a turn of its orbit (at
        // most ten of KSP's days): both orbits followed at the same times, their
        // bodies' own paths included (Orbit.getTruePositionAtUT), in 720 steps, then
        // the nearest step narrowed down by thirds.
        internal void ClosestApproach(out double distance, out double seconds)
        {
            if (!approachDone)
            {
                approachDone = true;
                Orbit target = TargetOrbit;
                if (target != null)
                {
                    double span = Elliptic ? Orbit.period : Math.Max(0.0, Orbit.timeToPe * 2.0);
                    span = Math.Min(span, 10.0 * 21600.0);
                    if (span > 0.0)
                    {
                        const int steps = 720;
                        double bestAt = 0.0, best = double.MaxValue;
                        for (int i = 0; i <= steps; i++)
                        {
                            double at = span * i / steps;
                            double gap = Gap(target, at);
                            if (gap < best)
                            {
                                best = gap;
                                bestAt = at;
                            }
                        }
                        double low = Math.Max(0.0, bestAt - span / steps), high = Math.Min(span, bestAt + span / steps);
                        for (int i = 0; i < 40; i++)
                        {
                            double a = low + (high - low) / 3.0, b = high - (high - low) / 3.0;
                            if (Gap(target, a) < Gap(target, b)) high = b;
                            else low = a;
                        }
                        approachSeconds = (low + high) / 2.0;
                        approachDistance = Gap(target, approachSeconds);
                    }
                }
            }
            distance = approachDistance;
            seconds = approachSeconds;
        }

        private double Gap(Orbit target, double after)
        {
            return (Orbit.getTruePositionAtUT(Now + after) - target.getTruePositionAtUT(Now + after)).magnitude;
        }

        // The orbit that goes round the same body as the target's: the vessel's own,
        // or, where the target goes round the body the vessel's body goes round (a
        // planet from another planet's orbit), that body's.
        internal Orbit Mutual(out Orbit target)
        {
            target = TargetOrbit;
            if (target == null) return null;
            if (Orbit.referenceBody == target.referenceBody) return Orbit;
            if (Body.orbit != null && Body.orbit.referenceBody == target.referenceBody) return Body.orbit;
            return null;
        }

        // The angle from `own` to `target` round the body, in the way `own` goes,
        // from 0 to 360.
        internal double Phase(Orbit own, Orbit target)
        {
            Vector3d position = own.getRelativePositionAtUT(Now);
            Vector3d normal = Vector3d.Cross(position, own.getOrbitalVelocityAtUT(Now));
            Vector3d other = target.getRelativePositionAtUT(Now);
            other -= normal.normalized * Vector3d.Dot(other, normal.normalized);
            double angle = Math.Atan2(Vector3d.Dot(normal.normalized, Vector3d.Cross(position, other)), Vector3d.Dot(position, other));
            return ReadoutMath.Wrap(angle * 180.0 / Math.PI);
        }

        internal static Vector3d Normal(Orbit orbit, double at)
        {
            return Vector3d.Cross(orbit.getRelativePositionAtUT(at), orbit.getOrbitalVelocityAtUT(at));
        }

        // ------------------------------------------------------------ docking

        // The target's docking port, where the target is one.
        internal ModuleDockingNode TargetPort
        {
            get { return Target as ModuleDockingNode; }
        }
    }
}
