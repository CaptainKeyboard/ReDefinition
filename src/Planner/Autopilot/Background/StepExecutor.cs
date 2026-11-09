using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Planner.Autopilot.Flight;
using ReDefinition.Planner.Autopilot.Guidance;
using ReDefinition.Planner.Autopilot.Planning;
using ReDefinition.Core;
using ReDefinition.Planner.Landing;
using ReDefinition.Planner.Orbits;
using UnityEngine;

namespace ReDefinition.Planner.Autopilot.Background
{
    // What a step does to a vessel that is not loaded: the orbit it is on, the propellant it keeps, the parts it lets go, the ground it is set down on.
    internal static class StepExecutor
    {
        // The vessel on the orbit of the step from its time, with the propellant it leaves.
        internal static bool Make(Vessel vessel, RouteEntry entry, BackgroundStep step)
        {
            if (step.Kind == StepKind.Release)
            {
                Let(vessel, entry, step);
                Debug.Log(Log.Tag + " Route: " + entry.Name + " made " + step.Why + " at UT " + step.UT.ToString("0") + ".");
                return true;
            }
            CelestialBody body = FlightGlobals.Bodies.Find(b => b.bodyName == step.Body);
            if (body == null) return false;
            var orbit = new Orbit(step.Inclination, step.Eccentricity, step.SemiMajorAxis, step.Lan, step.ArgumentOfPeriapsis, step.MeanAnomaly, step.Epoch, body);
            if (step.Kind == StepKind.Touchdown) return Touch(vessel, entry, step, body, orbit);
            vessel.orbitDriver.orbit.UpdateFromOrbitAtUT(orbit, step.UT, body);
            vessel.orbitDriver.UpdateOrbit();
            if (vessel.situation == Vessel.Situations.PRELAUNCH || vessel.LandedOrSplashed || vessel.situation == Vessel.Situations.FLYING || vessel.situation == Vessel.Situations.SUB_ORBITAL)
            {
                // A vessel that stood on the ground has left it: its orbit is its own again.
                entry.Down = false;
                entry.Wet = false;
                entry.Surveyed = false;
                InOrbit(vessel);
            }
            vessel.protoVessel.orbitSnapShot = new OrbitSnapshot(vessel.orbitDriver.orbit);
            Let(vessel, entry, step);
            Debug.Log(Log.Tag + " Route: " + entry.Name + " flew its burn of " + step.Why + " at UT " + step.UT.ToString("0") + " and is now round " + step.Body
                      + " (made " + (Planetarium.GetUniversalTime() - step.UT).ToString("0.00", CultureInfo.InvariantCulture) + " s late).");
            return true;
        }

        // A vessel that was on the ground or in the air is in an orbit now: the vessel and its saved copy say so, and the orbit is flown by the game again.
        internal static void InOrbit(Vessel vessel)
        {
            vessel.situation = Vessel.Situations.ORBITING;
            vessel.Landed = false;
            vessel.Splashed = false;
            vessel.skipGroundPositioning = false;
            vessel.vesselSpawning = false;
            vessel.orbitDriver.updateMode = OrbitDriver.UpdateMode.UPDATE;
            ProtoVessel proto = vessel.protoVessel;
            proto.situation = Vessel.Situations.ORBITING;
            proto.landed = false;
            proto.splashed = false;
            proto.skipGroundPositioning = false;
            proto.vesselSpawning = false;
            proto.landedAt = "";
            proto.displaylandedAt = "";
        }

        // What a step does to the vessel besides its orbit: the propellant its tanks keep after the burn, and the parts let go.
        internal static void Let(Vessel vessel, RouteEntry entry, BackgroundStep step)
        {
            foreach (ProtoPartSnapshot part in vessel.protoVessel.protoPartSnapshots)
            {
                double keep;
                if (!step.Keeps.TryGetValue(part.persistentId, out keep)) keep = step.Keep;
                if (keep >= 1.0) continue;
                foreach (ProtoPartResourceSnapshot resource in part.resources)
                    if (entry.Propellants.Contains(resource.resourceName)) resource.amount *= keep;
            }
            foreach (PartsCut cut in step.Cuts)
            {
                try
                {
                    if (VesselDetach.Apply(vessel, cut.Parts, cut.Name, cut.Type))
                        Debug.Log(Log.Tag + " Route: " + entry.Name + " let go " + cut.Parts.Count + " part(s) as " + cut.Name + ".");
                    else
                        Debug.LogWarning(Log.Tag + " Route: " + entry.Name + " could not let go " + cut.Name + ".");
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Log.Tag + " Route: " + entry.Name + " could not let go " + cut.Name + ": " + e);
                }
            }
        }

        // The orbit of a vessel that stands on the ground: the point it is fixed to, moving with the body. The game takes the vessel's speed from this orbit
        // as it unpacks the vessel, so the orbit is made at the moment, as it is for a vessel the game keeps on the ground itself.
        internal static void StandOrbit(Vessel vessel, RouteEntry entry)
        {
            CelestialBody body = vessel.orbitDriver.orbit.referenceBody;
            Vector3d surface = body.GetWorldSurfacePosition(entry.DownLatitude, entry.DownLongitude, entry.DownHeight) - body.position;
            vessel.orbitDriver.orbit.UpdateFromStateVectors(surface.xzy, Vector3d.Cross(body.angularVelocity, surface).xzy, body, Planetarium.GetUniversalTime());
        }

        // The vessel set down where the descent met the ground, or on the water: the place from the orbit and the turn of the body since, the height
        // from the body's terrain. The game sets it on the ground when it is loaded, as it does a vessel that was saved on the ground.
        internal static bool Touch(Vessel vessel, RouteEntry entry, BackgroundStep step, CelestialBody body, Orbit descent)
        {
            // The parts let go at the touchdown fall on their own: they go before the vessel is set on the ground.
            Let(vessel, entry, step);
            double now = Planetarium.GetUniversalTime();
            TouchdownSite site = step.Site;
            double ground = site.Ground, height = site.Height;
            bool water = site.Water;
            double latitude = site.Latitude, longitude = site.Longitude;
            // In the world's axes, with the body's turn: the vessel's position, its speed with the ground, and the way up.
            Vector3d surface = body.GetWorldSurfacePosition(latitude, longitude, height) - body.position;
            entry.Down = true;
            entry.Wet = water;
            entry.DownLatitude = latitude;
            entry.DownLongitude = longitude;
            entry.DownHeight = height;
            Quaternion rotation = site.Rotation;
            vessel.orbitDriver.orbit.UpdateFromStateVectors(surface.xzy, Vector3d.Cross(body.angularVelocity, surface).xzy, body, now);
            vessel.orbitDriver.updateMode = OrbitDriver.UpdateMode.IDLE;
            Vessel.Situations situation = water ? Vessel.Situations.SPLASHED : Vessel.Situations.LANDED;
            // The vessel and its saved copy say the same, and nothing says the ground has been found: the game looks for it when the vessel is loaded.
            vessel.situation = situation;
            vessel.Landed = !water;
            vessel.Splashed = water;
            vessel.latitude = latitude;
            vessel.longitude = longitude;
            vessel.altitude = height;
            vessel.srfRelRotation = rotation;
            vessel.heightFromTerrain = -1f;
            vessel.PQSminLevel = RouteTuning.TrustHeight ? body.pqsController.minLevel : 0;
            vessel.PQSmaxLevel = RouteTuning.TrustHeight ? body.pqsController.maxLevel : 0;
            vessel.skipGroundPositioning = RouteTuning.TrustHeight;
            vessel.vesselSpawning = true;
            vessel.landedAt = "";
            vessel.displaylandedAt = "";
            ProtoVessel proto = vessel.protoVessel;
            proto.situation = situation;
            proto.landed = !water;
            proto.splashed = water;
            proto.latitude = latitude;
            proto.longitude = longitude;
            proto.altitude = height;
            proto.rotation = rotation;
            proto.height = -1f;
            proto.normal = Vector3.up;
            proto.PQSminLevel = RouteTuning.TrustHeight ? body.pqsController.minLevel : 0;
            proto.PQSmaxLevel = RouteTuning.TrustHeight ? body.pqsController.maxLevel : 0;
            proto.skipGroundPositioning = RouteTuning.TrustHeight;
            proto.vesselSpawning = true;
            proto.landedAt = "";
            proto.displaylandedAt = "";
            proto.orbitSnapShot = new OrbitSnapshot(vessel.orbitDriver.orbit);
            Debug.Log(Log.Tag + " Route: " + entry.Name + " set down on " + body.bodyName + " at " + latitude.ToString("0.00", CultureInfo.InvariantCulture) + ", "
                      + longitude.ToString("0.00", CultureInfo.InvariantCulture) + (water ? " on the water" : ", " + ground.ToString("0") + " m up") + ", reference point at " + height.ToString("0.0", CultureInfo.InvariantCulture) + " m, " + entry.Lowest.ToString("0.0", CultureInfo.InvariantCulture) + " m over the lowest part, the ground "
                      + site.Tilt.ToString("0.0", CultureInfo.InvariantCulture) + " degrees steep, uneven by " + site.Score.ToString("0.0", CultureInfo.InvariantCulture) + " degrees, " + site.Shift.ToString("0") + " m from where the descent met it.");
            return true;
        }
    }
}
