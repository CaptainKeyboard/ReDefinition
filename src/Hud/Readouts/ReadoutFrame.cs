using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Hud.Readouts
{
    // What the readouts read, taken once per refresh: the active vessel, its orbit
    // and body, its target, and the heavier sums, each worked out on first use and
    // kept for the refresh.
    // Vectors are in KSP's orbit frame ("zup": z towards the body's north, as
    // Orbit.getRelativePositionAtUT gives them); the world's is its xzy.
    internal sealed class ReadoutFrame
    {
        // The active vessel, its orbit, the body it goes round, its target (null for none), and the universal time now.
        internal readonly Vessel Vessel;
        internal readonly Orbit Orbit;
        internal readonly CelestialBody Body;
        internal readonly ITargetable Target;
        internal readonly double Now;

        // Takes the vessel as it is now; null for no vessel, which leaves the frame not Ready.
        internal ReadoutFrame(Vessel vessel)
        {
            Vessel = vessel;
            Orbit = vessel != null ? vessel.orbit : null;
            Body = vessel != null ? vessel.mainBody : null;
            Target = vessel != null ? vessel.targetObject : null;
            Now = Planetarium.GetUniversalTime();
        }

        // Whether there is a vessel with an orbit and a body to read.
        internal bool Ready
        {
            get { return Vessel != null && Orbit != null && Body != null; }
        }

        // Whether the vessel's orbit is closed.
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

        // NaN until worked out for the refresh.
        private double maxThrust = double.NaN;

        // What the engines lit now give at full throttle where the vessel is, in kN.
        // It is each engine's full fuel flow times its Isp at the pressure here times g0,
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
                    for (int m = 0; m < part.Modules.Count; m++)
                    {
                        ModuleEngines engine = part.Modules[m] as ModuleEngines;
                        if (engine == null || !engine.EngineIgnited || !engine.isOperational || engine.flameout) continue;
                        double isp = engine.atmosphereCurve != null ? engine.atmosphereCurve.Evaluate((float)pressure) : 0.0;
                        sum += engine.maxFuelFlow * isp * engine.g * engine.thrustPercentage * 0.01;
                    }
                return maxThrust = sum;
            }
        }

        // NaN until worked out for the refresh.
        private double engineThrust = double.NaN;

        // What the engines give now, in kN.
        internal double Thrust
        {
            get
            {
                if (!double.IsNaN(engineThrust)) return engineThrust;
                double sum = 0.0;
                foreach (Part part in Vessel.parts)
                    for (int m = 0; m < part.Modules.Count; m++)
                    {
                        ModuleEngines engine = part.Modules[m] as ModuleEngines;
                        if (engine != null && engine.EngineIgnited) sum += engine.finalThrust;
                    }
                return engineThrust = sum;
            }
        }

        // ----------------------------------------------------------- surface

        // The time to impact and whether it was worked out for the refresh.
        private double impact = double.NaN;
        private bool impactDone;

        // Seconds until the vessel's path meets the ground, while it falls (Impact.Seconds); NaN while it climbs or does
        // not meet it.
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

        // Whether the last moment to brake was worked out for the refresh, and its figures.
        private bool suicideDone;
        private double suicideDistance = double.NaN, suicideSeconds = double.NaN, suicideDeltaV = double.NaN;

        // The last moment to brake to a stop over the ground, falling at the
        // vertical speed now and gaining speed under gravity until then, at full
        // thrust (ReadoutMath.SuicideBurn). It gives how high over the ground to light, how
        // long until then, and the delta-v. The horizontal speed is not counted:
        // the fall alone, both in the coast and in the burn.
        internal void SuicideBurn(out double altitude, out double countdown, out double deltaV)
        {
            if (!suicideDone)
            {
                suicideDone = true;
                double mass = Vessel.totalMass;
                if (Vessel.verticalSpeed < 0.0 && mass > 0.0 && !Vessel.LandedOrSplashed)
                    ReadoutMath.SuicideBurn(Math.Max(0.0, Vessel.radarAltitude), -Vessel.verticalSpeed, MaxThrust / mass, Gravity,
                        out suicideDistance, out suicideSeconds, out suicideDeltaV);
            }
            altitude = suicideDistance;
            countdown = suicideSeconds;
            deltaV = suicideDeltaV;
        }

        // The slope under the vessel and whether it was looked for in the refresh.
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
        // plane (angle of attack) and the yaw plane (sideslip), in degrees. It takes the
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

        // Whether the hottest part was looked for in the refresh, its share of its limit, and its name.
        private bool hottestDone;
        private double hottest;
        private string hottestName;

        // The part nearest its limit: the higher of its inside's and its skin's
        // temperature against their maximum, as a share, and its name.
        internal double Hottest(out string name)
        {
            if (!hottestDone)
            {
                hottestDone = true;
                hottest = FindHottest(out hottestName);
            }
            name = hottestName;
            return hottest;
        }

        // The part nearest its temperature limit, by the higher share of its inside's and its skin's.
        private double FindHottest(out string name)
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

        // Both ways along the line where the planes cross, made once.
        private static readonly double[] Signs = { 1.0, -1.0 };

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
            foreach (double sign in Signs)
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

        // The target's orbit; null without a target, or one with no orbit.
        internal Orbit TargetOrbit
        {
            get { return Target != null ? Target.GetOrbit() : null; }
        }

        // Where the target stands in the world; zero without a target.
        internal Vector3d TargetPosition
        {
            get { return Target != null && Target.GetTransform() != null ? (Vector3d)Target.GetTransform().position : Vector3d.zero; }
        }

        // The vessel's velocity against the target's, in the world's frame.
        internal Vector3d RelativeVelocity
        {
            get { return Target != null ? Vessel.obt_velocity - Target.GetObtVelocity() : Vector3d.zero; }
        }

        // Whether the closest approach was worked out for the refresh, and how near and how soon it comes.
        private bool approachDone;
        private double approachDistance = double.NaN, approachSeconds = double.NaN;

        // The closest the vessel comes to its target within a turn of its orbit (at
        // most ten of KSP's days). Both orbits are followed at the same times, their
        // bodies' own paths included (Orbit.getTruePositionAtUT), in 720 steps. Then
        // the nearest step is narrowed down by thirds.
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

        // How far apart the vessel and the target are `after` seconds from now, their bodies' own paths included.
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

        // The normal of the orbit's plane at that time, unnormalised (zup).
        internal static Vector3d Normal(Orbit orbit, double at)
        {
            return Vector3d.Cross(orbit.getRelativePositionAtUT(at), orbit.getOrbitalVelocityAtUT(at));
        }

        // The speed against the target at the closest approach, in m/s: both
        // orbits' velocities at that time (Orbit.getOrbitalVelocityAtUT), where
        // they go round the same body; NaN otherwise.
        internal double ApproachSpeed
        {
            get
            {
                double distance, seconds;
                ClosestApproach(out distance, out seconds);
                Orbit target = TargetOrbit;
                if (double.IsNaN(seconds) || target == null || target.referenceBody != Orbit.referenceBody) return double.NaN;
                return (Orbit.getOrbitalVelocityAtUT(Now + seconds) - target.getOrbitalVelocityAtUT(Now + seconds)).magnitude;
            }
        }

        // ------------------------------------------------------------ docking

        // The RCS's delta-v once worked out, kept for the refresh.
        private bool rcsDone;
        private double rcsDeltaV;

        // What the RCS propellant on board gives the vessel, in m/s, by the rocket equation.
        // It uses the RCS thrusters' combined Isp in vacuum (thrust over the sum of
        // thrust over Isp, ModuleRCS: thrusterPower at thrustPercentage, atmosphereCurve
        // at 0) and the propellant they can burn. NaN without RCS.
        // Each propellant counts from the parts its thrusters draw it from, as
        // Part.GetConnectedResourceTotals finds them for its flow mode (the part
        // itself, its crossfeed set, or the whole vessel), each part once.
        // It counts in the mix each kind of thruster burns it in (StageTable.UsablePropellant).
        internal double RcsDeltaV
        {
            get
            {
                if (!rcsDone)
                {
                    rcsDone = true;
                    rcsDeltaV = FindRcsDeltaV();
                }
                return rcsDeltaV;
            }
        }

        // The RCS's delta-v as RcsDeltaV says; NaN without RCS.
        private double FindRcsDeltaV()
        {
            double thrust = 0.0, overIsp = 0.0;
            Dictionary<string, Dictionary<string, double>> mixes = new Dictionary<string, Dictionary<string, double>>();
            Dictionary<string, HashSet<Part>> sources = new Dictionary<string, HashSet<Part>>();
            foreach (Part part in Vessel.parts)
                for (int m = 0; m < part.Modules.Count; m++)
                {
                    ModuleRCS rcs = part.Modules[m] as ModuleRCS;
                    if (rcs == null || !rcs.moduleIsEnabled || !rcs.rcsEnabled) continue;
                    double isp = rcs.atmosphereCurve != null ? rcs.atmosphereCurve.Evaluate(0f) : 0.0;
                    double power = rcs.thrusterPower * rcs.thrustPercentage * 0.01;
                    if (isp <= 0.0 || power <= 0.0) continue;
                    thrust += power;
                    overIsp += power / isp;
                    if (rcs.propellants == null) continue;
                    ReDefinition.Engineer.EditorReadout.AddMix(mixes, rcs.propellants, power / isp);
                    foreach (Propellant propellant in rcs.propellants)
                    {
                        if (propellant == null) continue;
                        HashSet<Part> from;
                        if (!sources.TryGetValue(propellant.name, out from)) sources[propellant.name] = from = new HashSet<Part>();
                        ReDefinition.Engineer.EditorReadout.AddRcsSources(part, propellant.GetFlowMode(), part.crossfeedPartSet, Vessel.parts, from);
                    }
                }
            if (thrust <= 0.0 || overIsp <= 0.0) return double.NaN;
            Dictionary<string, double> held = new Dictionary<string, double>();
            foreach (KeyValuePair<string, HashSet<Part>> resource in sources)
            {
                double mass = 0.0;
                foreach (Part source in resource.Value)
                {
                    PartResource stored = source != null ? source.Resources.Get(resource.Key) : null;
                    if (stored != null && stored.info != null && stored.flowState) mass += stored.amount * stored.info.density;
                }
                held[resource.Key] = mass;
            }
            double propellantMass = ReDefinition.Engineer.StageTable.UsablePropellant(mixes, held);
            double total = Vessel.totalMass;
            if (total <= propellantMass || propellantMass <= 0.0) return 0.0;
            return thrust / overIsp * PhysicsGlobals.GravitationalAcceleration * Math.Log(total / (total - propellantMass));
        }

        // The target's docking port, where the target is one.
        internal ModuleDockingNode TargetPort
        {
            get { return Target as ModuleDockingNode; }
        }

        // Whether the own port was looked for in the refresh, and the one found.
        private bool ownPortDone;
        private ModuleDockingNode ownPort;

        // The vessel's own docking port that docks with the target port, for the
        // distance to it and the closing speed. It is the port the vessel controls from
        // ("Control from here", ModuleDockingNode.MakeReferenceTransform), where it
        // is free and fits the target. Else it is the free port nearest the target port
        // that fits it, the one KSP's magnet takes first as they close. It is null where
        // the target is no port or the vessel has none that fits. ReadoutMath.PortFits and
        // PortFree decide fit and free. The nose the navball marker and
        // PORT ALIGNMENT read stays the control point: that is what the player turns.
        internal ModuleDockingNode OwnPort
        {
            get
            {
                if (ownPortDone) return ownPort;
                ownPortDone = true;
                ModuleDockingNode target = TargetPort;
                if (target == null || target.nodeTransform == null) return null;
                Part controlled = Vessel.GetReferenceTransformPart();
                Vector3 to = target.nodeTransform.position;
                float best = float.MaxValue;
                foreach (Part part in Vessel.parts)
                    for (int m = 0; m < part.Modules.Count; m++)
                    {
                        ModuleDockingNode port = part.Modules[m] as ModuleDockingNode;
                        if (port == null || port == target || port.nodeTransform == null || !ReadoutMath.PortFree(port.state)
                            || !ReadoutMath.PortFits(port.nodeTypes, target.nodeTypes)) continue;
                        if (part == controlled && port.controlTransform == Vessel.ReferenceTransform) return ownPort = port;
                        float distance = (port.nodeTransform.position - to).sqrMagnitude;
                        if (distance >= best) continue;
                        best = distance;
                        ownPort = port;
                    }
                return ownPort;
            }
        }

        // Where the docking readouts measure from: the own port's face (OwnPort),
        // which meets the target port's at contact, else the control point.
        internal Vector3d DockingFrom
        {
            get
            {
                ModuleDockingNode port = OwnPort;
                return port != null ? (Vector3d)port.nodeTransform.position : (Vector3d)Vessel.ReferenceTransform.position;
            }
        }
    }
}
