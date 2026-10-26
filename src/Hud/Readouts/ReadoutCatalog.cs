using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ReDefinition.Hud.Readouts
{
    // One value the flight panel can show (HUD, docs/player/flight-panel.md): its
    // key, which profiles name; its title in capitals, as KSP's maneuver panel
    // titles its fields; the group it is listed under; its value, and a second one
    // where it has two, such as a height and the time until it; and when it applies
    // (a target for the rendezvous values, a docking port for the docking ones).
    internal sealed class Readout
    {
        // As the class's comment names them: the key, the title, the group, the value, the second value (null for none),
        // and when it applies (null for always).
        internal string Key;
        internal string Title;
        internal string Group;
        internal Func<ReadoutFrame, string> Value;
        internal Func<ReadoutFrame, string> Second;
        internal Func<ReadoutFrame, bool> Applies;

        // Whether it shows two values.
        internal bool Paired
        {
            get { return Second != null; }
        }
    }

    // Every value the flight panel can show, grouped as players look for them.
    internal static class ReadoutCatalog
    {
        // The names of the groups the readouts are listed under.
        internal const string Orbit = "Orbit";
        internal const string Surface = "Surface";
        internal const string Vessel = "Vessel";
        internal const string Air = "Air";
        internal const string Rendezvous = "Rendezvous";
        internal const string Docking = "Docking";

        // The groups in the order they are listed.
        internal static readonly string[] Groups = { Orbit, Surface, Vessel, Air, Rendezvous, Docking };

        // Every number in the invariant culture.
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        // Every readout, in the order of their groups.
        internal static readonly List<Readout> All = new List<Readout>
        {
            // -------------------------------------------------------------- orbit
            new Readout
            {
                Key = "apoapsis", Title = "APOAPSIS", Group = Orbit,
                Value = f => ReadoutMath.Distance(f.Orbit.ApA),
                Second = f => f.Elliptic ? ReadoutMath.Countdown(f.Orbit.timeToAp) : "-",
            },
            new Readout
            {
                Key = "periapsis", Title = "PERIAPSIS", Group = Orbit,
                Value = f => ReadoutMath.Distance(f.Orbit.PeA),
                Second = f => ReadoutMath.Countdown(f.Orbit.timeToPe),
            },
            new Readout { Key = "period", Title = "PERIOD", Group = Orbit, Value = f => f.Elliptic ? ReadoutMath.Duration(f.Orbit.period) : "-" },
            new Readout { Key = "inclination", Title = "INCLINATION", Group = Orbit, Value = f => ReadoutMath.Angle(f.Orbit.inclination) },
            new Readout
            {
                Key = "eccentricity", Title = "ECCENTRICITY", Group = Orbit,
                Value = f => f.Orbit.eccentricity.ToString("0.0000", Invariant),
            },
            new Readout { Key = "semiMajorAxis", Title = "SEMI-MAJOR AXIS", Group = Orbit, Value = f => ReadoutMath.Distance(f.Orbit.semiMajorAxis) },
            new Readout { Key = "orbitalSpeed", Title = "ORBITAL SPEED", Group = Orbit, Value = f => ReadoutMath.Speed(f.Orbit.orbitalSpeed) },
            new Readout { Key = "ascendingNodeLongitude", Title = "LAN", Group = Orbit, Value = f => ReadoutMath.Angle(f.Orbit.LAN) },
            new Readout
            {
                Key = "argumentOfPeriapsis", Title = "ARG. OF PERIAPSIS", Group = Orbit,
                Value = f => ReadoutMath.Angle(f.Orbit.argumentOfPeriapsis),
            },
            new Readout
            {
                Key = "equatorialNodes", Title = "EQUATOR AN / DN", Group = Orbit,
                Value = f => ReadoutMath.Countdown(f.TimeToNode(f.Orbit, new Vector3d(0, 0, 1), true)),
                Second = f => ReadoutMath.Countdown(f.TimeToNode(f.Orbit, new Vector3d(0, 0, 1), false)),
            },

            // ------------------------------------------------------------ surface
            new Readout { Key = "altitudeGround", Title = "ALTITUDE (GROUND)", Group = Surface, Value = f => ReadoutMath.Distance(f.Vessel.radarAltitude) },
            new Readout { Key = "altitudeSea", Title = "ALTITUDE (SEA)", Group = Surface, Value = f => ReadoutMath.Distance(f.Vessel.altitude) },
            new Readout { Key = "verticalSpeed", Title = "VERTICAL SPEED", Group = Surface, Value = f => ReadoutMath.Speed(f.Vessel.verticalSpeed) },
            new Readout { Key = "horizontalSpeed", Title = "HORIZONTAL SPEED", Group = Surface, Value = f => ReadoutMath.Speed(f.Vessel.horizontalSrfSpeed) },
            new Readout
            {
                Key = "impact", Title = "TIME TO IMPACT", Group = Surface,
                Value = f => double.IsNaN(f.ImpactSeconds) ? "-" : ReadoutMath.Duration(f.ImpactSeconds),
            },
            new Readout
            {
                Key = "suicideBurn", Title = "SUICIDE BURN", Group = Surface,
                Value = f =>
                {
                    double altitude, countdown, deltaV;
                    f.SuicideBurn(out altitude, out countdown, out deltaV);
                    return double.IsNaN(altitude) ? "-" : ReadoutMath.Distance(altitude);
                },
                Second = f =>
                {
                    double altitude, countdown, deltaV;
                    f.SuicideBurn(out altitude, out countdown, out deltaV);
                    return double.IsNaN(countdown) ? "-" : ReadoutMath.Countdown(countdown);
                },
            },
            new Readout
            {
                Key = "suicideDeltaV", Title = "SUICIDE BURN ΔV", Group = Surface,
                Value = f =>
                {
                    double altitude, countdown, deltaV;
                    f.SuicideBurn(out altitude, out countdown, out deltaV);
                    return double.IsNaN(deltaV) ? "-" : ReadoutMath.Speed(deltaV);
                },
            },
            new Readout { Key = "slope", Title = "SLOPE", Group = Surface, Value = f => ReadoutMath.Angle(f.Slope) },
            new Readout
            {
                Key = "glideSlope", Title = "GLIDE SLOPE", Group = Air,
                Value = f => f.Vessel.horizontalSrfSpeed < 1.0 ? "-"
                    : ReadoutMath.Angle(Math.Atan2(f.Vessel.verticalSpeed, f.Vessel.horizontalSrfSpeed) * 180.0 / Math.PI),
            },
            new Readout
            {
                Key = "biome", Title = "BIOME", Group = Surface,
                Value = f => ScienceUtil.GetExperimentBiomeLocalized(f.Body, f.Vessel.latitude, f.Vessel.longitude),
            },
            new Readout { Key = "situation", Title = "SITUATION", Group = Surface, Value = f => global::Vessel.GetSituationString(f.Vessel) },
            new Readout
            {
                Key = "coordinates", Title = "LATITUDE / LONGITUDE", Group = Surface,
                Value = f => Math.Abs(f.Vessel.latitude).ToString("0.000", Invariant) + "° " + (f.Vessel.latitude >= 0.0 ? "N" : "S"),
                Second = f =>
                {
                    double longitude = ((f.Vessel.longitude % 360.0) + 540.0) % 360.0 - 180.0;
                    return Math.Abs(longitude).ToString("0.000", Invariant) + "° " + (longitude >= 0.0 ? "E" : "W");
                },
            },

            // ------------------------------------------------------------- vessel
            new Readout
            {
                Key = "twr", Title = "TWR NOW / FULL", Group = Vessel,
                Value = f => Ratio(f.Thrust, f),
                Second = f => Ratio(f.MaxThrust, f),
            },
            new Readout
            {
                Key = "stageDeltaV", Title = "STAGE ΔV", Group = Vessel,
                Value = f =>
                {
                    DeltaVStageInfo stage = f.Vessel.VesselDeltaV != null ? f.Vessel.VesselDeltaV.GetStage(f.Vessel.currentStage) : null;
                    return stage != null ? ReadoutMath.Speed(stage.deltaVActual) : "-";
                },
            },
            new Readout
            {
                Key = "totalDeltaV", Title = "TOTAL ΔV", Group = Vessel,
                Value = f => f.Vessel.VesselDeltaV != null ? ReadoutMath.Speed(f.Vessel.VesselDeltaV.TotalDeltaVActual) : "-",
            },
            new Readout { Key = "mass", Title = "MASS", Group = Vessel, Value = f => f.Vessel.totalMass.ToString("0.00", Invariant) + " t" },
            new Readout
            {
                Key = "hottestPart", Title = "HOTTEST PART", Group = Vessel,
                Value = f =>
                {
                    string name;
                    return (f.Hottest(out name) * 100.0).ToString("0", Invariant) + " %";
                },
                Second = f =>
                {
                    string name;
                    f.Hottest(out name);
                    return name;
                },
            },
            new Readout { Key = "gForce", Title = "G-FORCE", Group = Vessel, Value = f => f.Vessel.geeForce.ToString("0.00", Invariant) + " g" },

            // ---------------------------------------------------------------- air
            new Readout
            {
                Key = "dynamicPressure", Title = "DYNAMIC PRESSURE", Group = Air,
                Value = f => f.Vessel.dynamicPressurekPa.ToString(f.Vessel.dynamicPressurekPa < 10.0 ? "0.00" : "0.0", Invariant) + " kPa",
            },
            new Readout { Key = "mach", Title = "MACH", Group = Air, Value = f => f.Vessel.mach.ToString("0.00", Invariant) },
            new Readout
            {
                Key = "angleOfAttack", Title = "ANGLE OF ATTACK", Group = Air,
                Value = f =>
                {
                    double attack, sideslip;
                    f.AeroAngles(out attack, out sideslip);
                    return ReadoutMath.Angle(attack);
                },
            },
            new Readout
            {
                Key = "sideslip", Title = "SIDESLIP", Group = Air,
                Value = f =>
                {
                    double attack, sideslip;
                    f.AeroAngles(out attack, out sideslip);
                    return ReadoutMath.Angle(sideslip);
                },
            },
            new Readout
            {
                Key = "staticPressure", Title = "AIR PRESSURE", Group = Air,
                Value = f => f.Vessel.staticPressurekPa.ToString("0.00", Invariant) + " kPa",
            },

            // --------------------------------------------------------- rendezvous
            new Readout
            {
                Key = "targetDistance", Title = "TARGET DISTANCE", Group = Rendezvous, Applies = HasTarget,
                Value = f => ReadoutMath.Distance((f.TargetPosition - (Vector3d)f.Vessel.CoM).magnitude),
            },
            new Readout
            {
                Key = "relativeSpeed", Title = "RELATIVE SPEED", Group = Rendezvous, Applies = HasTarget,
                Value = f => ReadoutMath.Speed(f.RelativeVelocity.magnitude),
            },
            new Readout
            {
                Key = "closestApproach", Title = "CLOSEST APPROACH", Group = Rendezvous, Applies = HasTarget,
                Value = f =>
                {
                    double distance, seconds;
                    f.ClosestApproach(out distance, out seconds);
                    return ReadoutMath.Distance(distance);
                },
                Second = f =>
                {
                    double distance, seconds;
                    f.ClosestApproach(out distance, out seconds);
                    return ReadoutMath.Countdown(seconds);
                },
            },
            new Readout
            {
                Key = "approachSpeed", Title = "SPEED AT APPROACH", Group = Rendezvous, Applies = HasTarget,
                Value = f => ReadoutMath.Speed(f.ApproachSpeed),
            },
            new Readout
            {
                Key = "phaseAngle", Title = "PHASE ANGLE", Group = Rendezvous, Applies = HasTarget,
                Value = f =>
                {
                    Orbit target;
                    Orbit own = f.Mutual(out target);
                    return own != null ? ReadoutMath.Angle(f.Phase(own, target)) : "-";
                },
            },
            new Readout
            {
                Key = "transferWindow", Title = "TRANSFER ANGLE", Group = Rendezvous, Applies = HasTarget,
                Value = f =>
                {
                    Orbit target;
                    Orbit own = f.Mutual(out target);
                    return own != null ? ReadoutMath.Angle(ReadoutMath.TransferAngle(own.semiMajorAxis, target.semiMajorAxis)) : "-";
                },
                Second = f =>
                {
                    Orbit target;
                    Orbit own = f.Mutual(out target);
                    if (own == null || own.eccentricity >= 1.0 || target.eccentricity >= 1.0) return "-";
                    double wanted = ReadoutMath.TransferAngle(own.semiMajorAxis, target.semiMajorAxis);
                    return ReadoutMath.Countdown(ReadoutMath.TimeToPhase(f.Phase(own, target), wanted, 360.0 / own.period, 360.0 / target.period));
                },
            },
            new Readout
            {
                Key = "relativeInclination", Title = "REL. INCLINATION", Group = Rendezvous, Applies = HasTarget,
                Value = f =>
                {
                    Orbit target;
                    Orbit own = f.Mutual(out target);
                    if (own == null) return "-";
                    return ReadoutMath.Angle(Vector3d.Angle(ReadoutFrame.Normal(own, f.Now), ReadoutFrame.Normal(target, f.Now)));
                },
            },
            new Readout
            {
                Key = "relativeNodes", Title = "REL. AN / DN", Group = Rendezvous, Applies = HasTarget,
                Value = f => RelativeNode(f, true),
                Second = f => RelativeNode(f, false),
            },

            // ------------------------------------------------------------ docking
            new Readout
            {
                Key = "portDistance", Title = "PORT DISTANCE", Group = Docking, Applies = HasPort,
                // From the own port's face (ReadoutFrame.OwnPort): about 0 at contact.
                Value = f => ReadoutMath.Distance(((Vector3d)f.TargetPort.nodeTransform.position - f.DockingFrom).magnitude),
            },
            new Readout
            {
                Key = "closingSpeed", Title = "CLOSING / SIDEWAYS", Group = Docking, Applies = HasPort,
                Value = f => ReadoutMath.Speed(-Vector3d.Dot(f.RelativeVelocity, (Vector3d)f.TargetPort.nodeTransform.forward)),
                Second = f =>
                {
                    Vector3d along = f.TargetPort.nodeTransform.forward;
                    Vector3d relative = f.RelativeVelocity;
                    return ReadoutMath.Speed((relative - along * Vector3d.Dot(relative, along)).magnitude);
                },
            },
            new Readout
            {
                Key = "rcsDeltaV", Title = "RCS ΔV", Group = Docking,
                Value = f => ReadoutMath.Speed(f.RcsDeltaV),
            },
            new Readout
            {
                Key = "portAlignment", Title = "PORT ALIGNMENT", Group = Docking, Applies = HasPort,
                Value = f => ReadoutMath.Angle(Vector3.Angle(f.Vessel.ReferenceTransform.up, -f.TargetPort.nodeTransform.forward)),
            },
        };

        // Whether the vessel has a target with an orbit.
        private static bool HasTarget(ReadoutFrame f)
        {
            return f.Target != null && f.TargetOrbit != null;
        }

        // Whether the target is a docking port and the vessel has a control point.
        private static bool HasPort(ReadoutFrame f)
        {
            return f.TargetPort != null && f.TargetPort.nodeTransform != null && f.Vessel.ReferenceTransform != null;
        }

        // `thrust` against the vessel's weight where it is, to two places; "-" where it weighs nothing.
        private static string Ratio(double thrust, ReadoutFrame f)
        {
            double weight = f.Vessel.totalMass * f.Gravity;
            return weight > 0.0 ? (thrust / weight).ToString("0.00", Invariant) : "-";
        }

        // The countdown to the vessel's ascending or descending node with the target's plane, from the orbit round the
        // target's body (Mutual); "-" where there is none.
        private static string RelativeNode(ReadoutFrame f, bool ascending)
        {
            Orbit target;
            Orbit own = f.Mutual(out target);
            if (own == null) return "-";
            return ReadoutMath.Countdown(f.TimeToNode(own, ReadoutFrame.Normal(target, f.Now), ascending));
        }

        // The readout with that key; null where there is none.
        internal static Readout Find(string key)
        {
            return All.Find(readout => readout.Key == key);
        }
    }
}
