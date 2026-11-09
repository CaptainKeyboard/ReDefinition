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
    // Where and when a vessel is set down: the ground under the descent, the place that is flattest, and the time the step is made so that no time warp can
    // carry the vessel into the ground first.
    internal static class TouchdownPlanner
    {
        // How far over the ground the vessel is put, in m: the game sets it down from there when it is loaded.
        // The game's ground lies about 3 m over the height the terrain gives (measured on Minmus), and a vessel set down inside it is thrown out.
        // The vessel stands that high and is let down slowly, which is cheap.
        private const double Clearance = 5.0;

        // How far the vessel's solid parts reach below its reference point along its up axis, in m: the lowest corner of any collider's bounds.
        internal static double LowestPoint(Vessel vessel, out double footprint, ICollection<uint> except)
        {
            double lowest = 0.0;
            Vector3 low = Vector3.zero, high = Vector3.zero;
            foreach (Part part in vessel.parts)
            {
                if (except != null && except.Contains(part.persistentId)) continue;
                foreach (Collider collider in part.GetComponentsInChildren<Collider>())
                {
                    if (collider.isTrigger || !collider.enabled) continue;
                    Bounds bounds = collider.bounds;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        Vector3 point = bounds.center + Vector3.Scale(bounds.extents, new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f));
                        Vector3 local = vessel.vesselTransform.InverseTransformPoint(point);
                        lowest = Math.Min(lowest, local.y);
                        low = Vector3.Min(low, local);
                        high = Vector3.Max(high, local);
                    }
                }
            }
            Debug.Log(Log.Tag + " Route: " + vessel.vesselName + " reaches from " + low.ToString("0.0") + " to " + high.ToString("0.0") + " in its own axes, up is " + (Vector3)vessel.vesselTransform.up
                      + " against the way up " + (Vector3)vessel.upAxis + ".");
            footprint = Math.Max(Math.Max(-low.x, high.x), Math.Max(-low.z, high.z));
            return -lowest;
        }

        // A plan that ends in a landing is carried to the ground. From an orbit that stays above it, a burn at the orbit's start brings the periapsis
        // down to it. The vessel touches down where the orbit meets the ground. A body with air takes the vessel down on its parachutes.
        // An airless one costs the speed the vessel arrives with, the delta-v of a landing burn.
        internal static BackgroundStep Plan(Vessel vessel, RouteEntry entry, RoutePlan plan, FuelBudget budget, IList<Destination> route, IVesselFigures figures)
        {
            PlannedStep last = plan.Steps.Count > 0 ? plan.Steps[plan.Steps.Count - 1] : null;
            if (last == null || last.Kind != NodePlanner.StepKind.Landing || plan.Legs.Count == 0) return null;
            PlannedLeg leg = plan.Legs[plan.Legs.Count - 1];
            Orbit orbit = leg.Orbit;
            CelestialBody body = orbit != null ? orbit.referenceBody : null;
            // What is left of the vessel by then is what stands on the ground: the parts let go on the way do not reach down.
            var let = new HashSet<uint>();
            foreach (BackgroundStep before in entry.Steps)
                foreach (PartsCut cut in before.Cuts) foreach (uint part in cut.Parts) let.Add(part);
            if (let.Count > 0) entry.Lowest = LowestPoint(vessel, out entry.Footprint, let);
            // A landing in the air of a body is made on parachutes, and the route says so (a direct entry or a pass first); without them the vessel would not live.
            bool air = body != null && body.atmosphere && last.Goal >= 0 && route != null && last.Goal < route.Count && (route[last.Goal].Direct || route[last.Goal].Aerobrake);
            if (air && !vessel.parts.Exists(p => !let.Contains(p.persistentId) && figures.IsParachute(p)))
            {
                entry.Problem = "The route lands in the air of " + body.bodyName + ", and the vessel has no parachutes left by then.";
                return null;
            }
            if (body == null || body.pqsController == null || !body.hasSolidSurface) return null;
            double from = leg.StartUT;
            // The ground is met at the periapsis or before it; a periapsis within a metre of the ground is that.
            if (orbit.PeR > body.Radius + 1.0)
            {
                Vector3d at = orbit.getRelativePositionAtUT(from);
                Vector3d speed = orbit.getOrbitalVelocityAtUT(from);
                // The speed that reaches the ground on the far side of the orbit.
                double wanted = Math.Sqrt(body.gravParameter * (2.0 / at.magnitude - 2.0 / (at.magnitude + body.Radius)));
                var descent = new Orbit();
                descent.UpdateFromStateVectors(at, speed.normalized * wanted, body, from);
                double burn = Math.Abs(speed.magnitude - wanted);
                BackgroundStep deorbit = RouteCompiler.Elements(descent, from, "deorbit, " + burn.ToString("0") + " m/s", entry.Steps.Count > 0 ? entry.Steps[entry.Steps.Count - 1].Reached : entry.Reached);
                budget.Apply(deorbit, burn);
                entry.Steps.Add(deorbit);
                orbit = descent;
            }
            double periapsis = orbit.NextPeriapsisTime(from);
            if (periapsis < from || orbit.getRelativePositionAtUT(from).magnitude <= body.Radius || orbit.getRelativePositionAtUT(periapsis).magnitude > body.Radius + 1.0) return null;
            // The step is made when the orbit comes down to the highest ground of the body: from there the vessel can meet the ground at any moment, and
            // the game destroys it when it does. The place is where the descent meets the ground at the point it comes to, which the terrain there gives.
            double now = Planetarium.GetUniversalTime();
            double high = FallsTo(orbit, from, periapsis, body.Radius);
            // The step is made before the game looks for the ground under the vessel, and early enough that the fastest time warp cannot carry the vessel
            // there in a frame. The touchdown does not depend on the descent, so the vessel is set down early rather than lost.
            double trigger = Math.Max(from, Math.Min(FallsTo(orbit, from, periapsis, body.Radius + GroundWatchHeight(body)), high - 10.0) - WarpReach());
            for (int round = 0; round < 4; round++)
            {
                double roundLatitude, roundLongitude, roundAltitude;
                body.GetLatLonAlt(body.position + orbit.getRelativePositionAtUT(high).xzy, out roundLatitude, out roundLongitude, out roundAltitude);
                roundLongitude += 360.0 * body.rotPeriodRecip * (now - high);
                high = FallsTo(orbit, from, periapsis, body.Radius + Math.Max(0.0, body.TerrainAltitude(roundLatitude, roundLongitude, true)));
            }
            // The place is found now, while the vessel and the route are at hand; the body has turned from the time of the touchdown to now.
            double latitude, longitude, altitude;
            body.GetLatLonAlt(body.position + orbit.getRelativePositionAtUT(high).xzy, out latitude, out longitude, out altitude);
            if (body.rotates) longitude += 360.0 * body.rotPeriodRecip * (now - high);
            longitude = longitude - 360.0 * Math.Floor((longitude + 180.0) / 360.0);
            if (RouteTuning.ScanSteepest) Debug.Log(Log.Tag + " Route: steepest ground within 5 km of " + latitude.ToString("0.0000", CultureInfo.InvariantCulture) + "," + longitude.ToString("0.0000", CultureInfo.InvariantCulture)
                                     + ": " + Touchdown.Steepest(body, latitude, longitude, entry.Footprint));
            if (RouteTuning.FixedSite)
            {
                latitude = RouteTuning.SiteLatitude;
                longitude = RouteTuning.SiteLongitude;
            }
            TouchdownSite site = Touchdown.Find(body, latitude, longitude, entry.Lowest, entry.Footprint, Clearance + RouteTuning.ExtraClearance, RouteTuning.SiteRadius >= 0.0 ? RouteTuning.SiteRadius : 1000.0);
            if (!site.Found) return null;
            // The landing costs what the body takes: nothing on parachutes where the air is thick, the thin air's rest, a landing burn on the way to the ground
            // where there is none; the speed it arrives with in the air or without it is the planner's estimate.
            double arrival = body.atmosphere ? figures.LandingDeltaV(body, air) : orbit.getOrbitalSpeedAtDistance(body.Radius);
            BackgroundStep touch = RouteCompiler.Elements(orbit, trigger, "landing, " + arrival.ToString("0") + " m/s", last.Goal);
            budget.Apply(touch, arrival);
            touch.Kind = StepKind.Touchdown;
            touch.GroundUT = high;
            touch.Site = site;
            entry.Steps.Add(touch);
            return touch;
        }

        // How far the largest frame at the fastest time warp carries the game's clock, in seconds: the physics step the game allows per frame, and one more.
        internal static double WarpReach()
        {
            TimeWarp warp = TimeWarp.fetch;
            double rate = warp != null && warp.warpRates != null && warp.warpRates.Length > 0 ? warp.warpRates[warp.warpRates.Length - 1] : 100000.0;
            return rate * (Time.maximumDeltaTime + Time.fixedDeltaTime);
        }

        // The height over sea level below which the game can find an unloaded vessel in the ground. It is the greatest of the highest ground, the altitude
        // the game starts to know the ground under a vessel at, and the top of the air. A mountain on the way meets a descent before the site does.
        internal static double GroundWatchHeight(CelestialBody body)
        {
            double mountains = body.pqsController != null ? Math.Max(0.0, body.pqsController.radiusMax - body.pqsController.radius) : 0.0;
            double watched = Math.Min(body.inverseRotThresholdAltitude, body.minOrbitalDistance - body.Radius);
            return Math.Max(Math.Max(mountains, watched), body.atmosphere ? body.atmosphereDepth : 0.0);
        }

        // The time between two moments at which the orbit's radius falls to the given one; the radius only falls there.
        internal static double FallsTo(Orbit orbit, double from, double to, double radius)
        {
            double low = from, high = to;
            for (int i = 0; i < 60; i++)
            {
                double middle = 0.5 * (low + high);
                if (orbit.getRelativePositionAtUT(middle).magnitude > radius) low = middle;
                else high = middle;
            }
            return high;
        }
    }
}
