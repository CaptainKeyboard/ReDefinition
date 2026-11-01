using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ReDefinition.Autopilot.Flight;
using ReDefinition.Autopilot.Guidance;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;
using ReDefinition.Landing;
using ReDefinition.Orbits;
using UnityEngine;

namespace ReDefinition.Autopilot.Background
{
    // A plan as the steps a vessel flies on its own: the orbits after the burns, the sections let go, the touchdown, and what each step costs.
    internal static class RouteCompiler
    {
        internal static RouteEntry Build(Vessel vessel, RoutePlan plan, IList<Destination> route, IVesselFigures figures)
        {
            var entry = new RouteEntry { Vessel = vessel.id, Name = vessel.vesselName, Key = plan.Signature ?? "", HandedUT = Planetarium.GetUniversalTime() };
            entry.Lowest = TouchdownPlanner.LowestPoint(vessel, out entry.Footprint, null);
            RouteState own = RouteState.Of(vessel);
            if (own != null && own.Progress.Key == entry.Key) entry.Reached = own.Progress.Reached;
            // What the engines burn: the propellants with a mass. Electric charge and intake air are not used up by a burn.
            double exhaust = 0.0;
            foreach (Part part in vessel.parts)
                foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
                {
                    foreach (Propellant burnt in engine.propellants)
                        if (burnt.resourceDef != null && burnt.resourceDef.density > 0.0 && !entry.Propellants.Contains(burnt.name)) entry.Propellants.Add(burnt.name);
                    exhaust = Math.Max(exhaust, engine.atmosphereCurve.Evaluate(0f) * 9.80665);
                }
            var tank = new FuelTank { Mass = vessel.GetTotalMass(), Exhaust = exhaust };
            foreach (Part part in vessel.parts)
                foreach (PartResource resource in part.Resources)
                    if (entry.Propellants.Contains(resource.resourceName)) tank.Propellant += resource.amount * resource.info.density;
            // The exhaust speed that gives the vessel's delta-v from its propellant, where KSP has worked that out; else that of its best engine.
            double total = vessel.VesselDeltaV != null ? vessel.VesselDeltaV.TotalDeltaVVac : 0.0;
            if (total > 0.0 && tank.Propellant > 0.0 && tank.Mass > tank.Propellant) tank.Exhaust = total / Math.Log(tank.Mass / (tank.Mass - tank.Propellant));
            var budget = new FuelBudget { FuelTank = tank, Name = vessel.vesselName };
            IBurnShare stages = figures != null ? figures.StagesOf(vessel) : null;
            if (stages != null && stages.Ready) budget.Stages = stages;
            // A launch from the ground ends in the orbit asked for, and the rest of the route is planned from there.
            if (plan.Steps.Count > 0 && plan.Steps[0].Kind == NodePlanner.StepKind.Ascent && route != null && figures != null && vessel.mainBody.pqsController != null)
            {
                Orbit orbit;
                double endUT;
                Vector3d site = vessel.GetWorldPos3D() - vessel.mainBody.position;
                if (!Launch(vessel, entry, budget, stages, figures, vessel.mainBody, plan.Steps[0].Altitude, site, Planetarium.GetUniversalTime(), entry.Reached, out orbit, out endUT, 0.0, true)) return entry;
                plan = RouteSimulation.Run(vessel, route, new PlanOptions { AirPasses = true, From = orbit, FromUT = endUT });
            }
            // The route in pieces: the orbits up to a landing, and, where the route goes on from the ground, the launch from it and the pieces after.
            for (int round = 0; round < 6; round++)
            {
                RouteStep touch = AddSegment(vessel, entry, plan, budget, stages, route, figures);
                if (entry.Problem != null || touch == null) break;
                PlannedStep landing = plan.Steps[plan.Steps.Count - 1];
                if (route == null || figures == null || landing.Goal < 0 || landing.Goal + 1 >= route.Count) break;
                if (route[landing.Goal].Releases != null && route[landing.Goal].Releases.Count > 0)
                {
                    Debug.Log(Log.Tag + " Route: " + vessel.vesselName + " lets go a section on the ground; the route is flown to the landing here and goes on when the vessel is loaded.");
                    break;
                }
                // From the ground the next stop is the orbit round the body, or the way to another: the launch is to the stop's own altitude round this body.
                CelestialBody body = FlightGlobals.Bodies.Find(b => b.bodyName == touch.Body);
                Destination next = route[landing.Goal + 1];
                double altitude = next.Body == body ? next.Altitude : NextStepPlanner.LowOrbit(body);
                Orbit orbit;
                double endUT;
                Vector3d site = body.GetWorldSurfacePosition(touch.Site.Latitude, touch.Site.Longitude, touch.Site.Height) - body.position;
                if (!Launch(vessel, entry, budget, stages, figures, body, altitude, site, Planetarium.GetUniversalTime(), landing.Goal, out orbit, out endUT, touch.GroundUT + 1.0, false)) break;
                plan = RouteSimulation.Run(vessel, route, new PlanOptions { AirPasses = true, From = orbit, FromUT = endUT, ReachedAtStart = landing.Goal });
            }
            if (stages != null && stages.Short > 0.0) Debug.LogWarning(Log.Tag + " Route: " + vessel.vesselName + " is short of " + stages.Short.ToString("0") + " m/s for the plan.");
            if (entry.Steps.Count > 0)
                Debug.Log(Log.Tag + " Route: " + vessel.vesselName + " flies on in the background: " + entry.Steps.Count + " step(s) up to UT " + entry.Steps[entry.Steps.Count - 1].UT.ToString("0") + ".");
            SortByTime(entry);
            return entry;
        }

        // The steps are made in the order of their times, the head of the list first: a step of the same time stays where it was.
        internal static void SortByTime(RouteEntry entry)
        {
            List<RouteStep> ordered = entry.Steps.OrderBy(step => step.UT).ToList();
            entry.Steps.Clear();
            entry.Steps.AddRange(ordered);
        }

        // A launch from the ground as a step: the burn, and the orbit it ends in. False where the stages cannot give the delta-v, with the reason in the entry.
        internal static bool Launch(Vessel vessel, RouteEntry entry, FuelBudget budget, IBurnShare stages, IVesselFigures figures, CelestialBody body, double altitude, Vector3d site, double now,
                                   int reached, out Orbit orbit, out double endUT, double launchUT, bool required)
        {
            orbit = null;
            endUT = 0.0;
            double launch = figures.LaunchDeltaV(body, altitude);
            var ascent = new RouteStep { Why = "ascent, " + launch.ToString("0") + " m/s", Reached = reached };
            double began = stages != null ? stages.Elapsed : 0.0;
            double shortBefore = stages != null ? stages.Short : 0.0;
            budget.Apply(ascent, launch);
            if (stages != null && stages.Short > shortBefore)
            {
                // A launch from the start is refused; one from the ground after a landing is left to the flight when the vessel is loaded.
                string shortOf = "The vessel does not have the delta-v for the launch: it is short of " + (stages.Short - shortBefore).ToString("0") + " m/s.";
                if (required) entry.Problem = shortOf;
                else Debug.Log(Log.Tag + " Route: " + vessel.vesselName + ": " + shortOf + " The route is flown to the landing.");
                return false;
            }
            // The time of the burn: the stages' own, and where they are not known the time the vessel needs at its thrust.
            double burnTime = stages != null && stages.Ready ? stages.Elapsed - began : 0.0;
            if (burnTime <= 0.0) burnTime = Math.Max(60.0, launch / Math.Max(1.0, (vessel.VesselDeltaV != null ? vessel.VesselDeltaV.TotalDeltaVVac : launch) / 600.0));
            orbit = AscentOrbit.Make(body, site, now, launchUT > 0.0 ? launchUT : now, altitude, burnTime, out endUT);
            ascent.UT = endUT;
            ascent.Body = body.bodyName;
            ascent.Inclination = orbit.inclination;
            ascent.Eccentricity = orbit.eccentricity;
            ascent.SemiMajorAxis = orbit.semiMajorAxis;
            ascent.Lan = orbit.LAN;
            ascent.ArgumentOfPeriapsis = orbit.argumentOfPeriapsis;
            ascent.MeanAnomaly = orbit.meanAnomalyAtEpoch;
            ascent.Epoch = orbit.epoch;
            entry.Steps.Add(ascent);
            return true;
        }

        // A meeting with a vessel in an orbit as a step. The cost is the cheapest transfer to where the target is on arrival. The vessel is then in the
        // target's orbit, a little behind it, at rest to it. A docking is not made without the player: the step ends at the meeting.
        internal static void AddMeeting(Vessel vessel, RouteEntry entry, RoutePlan plan, FuelBudget budget, PlannedStep end)
        {
            // A plan with no burn has no leg of its own: the meeting is made from the orbit the vessel is on.
            Orbit ours = plan.Legs.Count > 0 ? plan.Legs[plan.Legs.Count - 1].Orbit : vessel.orbit;
            double startsAt = plan.Legs.Count > 0 ? plan.Legs[plan.Legs.Count - 1].StartUT : Planetarium.GetUniversalTime();
            CelestialBody body = ours != null ? ours.referenceBody : null;
            Vessel target = FlightGlobals.Vessels.Find(v => v.id == end.Target);
            if (body == null || target == null || target.orbit == null || target.orbit.referenceBody != body || target.orbit.eccentricity >= 1.0 || target.LandedOrSplashed)
            {
                entry.Problem = "The vessel to meet is not in an orbit round " + (body != null ? body.bodyName : "the body") + ".";
                return;
            }
            double start = Math.Max(plan.EndUT, startsAt);
            double ourPeriod = ours.eccentricity < 1.0 ? ours.period : target.orbit.period;
            double hohmann = Math.PI * Math.Sqrt(Math.Pow(0.5 * (ours.semiMajorAxis + target.orbit.semiMajorAxis), 3.0) / body.gravParameter);
            // MechJeb's rendezvous waits up to the orbits it is set to for the phasing.
            double wait = (MechJebDriver.RendezvousOrbits(vessel) + 1.0) * Math.Max(ourPeriod, target.orbit.period);
            Transfer best;
            if (!Transfers.Cheapest(KspOrbits.StateAt(ours, start), KspOrbits.StateAt(target.orbit, start), body.gravParameter, wait, 0.5 * hohmann, Math.Max(1.5 * hohmann, 1.05 * Math.Max(ourPeriod, target.orbit.period)), true, out best))
            {
                entry.Problem = "No way to " + target.vesselName + " was found in " + MechJebDriver.RendezvousOrbits(vessel).ToString("0") + " orbits.";
                return;
            }
            double arrival = start + best.Delay + best.FlightTime;
            // In the orbit of the target, the distance MechJeb's rendezvous ends at behind it.
            Orbit shape = target.orbit;
            double behind = MechJebDriver.RendezvousDistance(vessel) * shape.getRelativePositionAtUT(arrival).magnitude / (shape.semiMajorAxis * shape.semiMajorAxis * Math.Sqrt(1.0 - shape.eccentricity * shape.eccentricity));
            var meeting = new Orbit(target.orbit.inclination, target.orbit.eccentricity, target.orbit.semiMajorAxis, target.orbit.LAN, target.orbit.argumentOfPeriapsis,
                                    target.orbit.meanAnomalyAtEpoch - behind, target.orbit.epoch, body);
            double cost = best.Total(true);
            int reached = entry.Steps.Count > 0 ? entry.Steps[entry.Steps.Count - 1].Reached : entry.Reached;
            // A docking is still to be made; a meeting alone is the stop.
            RouteStep step = Elements(meeting, arrival, "meeting with " + target.vesselName + ", " + cost.ToString("0") + " m/s", end.Dock ? reached : Math.Max(reached, end.Goal));
            budget.Apply(step, cost);
            entry.Steps.Add(step);
        }

        // The orbits of a plan as steps, with the releases in them, and the landing it ends in: the step of the touchdown, or null where it ends otherwise.
        internal static RouteStep AddSegment(Vessel vessel, RouteEntry entry, RoutePlan plan, FuelBudget budget, IBurnShare stages, IList<Destination> route, IVesselFigures figures)
        {
            if (plan.AirProblem != null)
            {
                entry.Problem = plan.AirProblem;
                return null;
            }
            var burnt2 = new HashSet<PlannedStep>();
            List<Letting> lettings = PlanReleases(vessel, plan);
            int letNext = 0;
            for (int i = 1; i < plan.Legs.Count; i++)
            {
                PlannedLeg leg = plan.Legs[i];
                if (leg.Orbit == null || leg.Orbit.referenceBody == null) continue;
                // A section let go before this burn does not burn with the stages after it.
                while (letNext < lettings.Count && lettings[letNext].At < leg.StartUT - 1e-3)
                {
                    if (budget.Stages != null) budget.Stages.Release(lettings[letNext].PartsCut.Parts);
                    letNext++;
                }
                // The burns that start this leg: the plan's burn steps at its start time, each one used up for one leg.
                double deltaV = 0.0;
                foreach (PlannedStep step in plan.Steps)
                    if (step.Kind == NodePlanner.StepKind.Burn && Math.Abs(step.UT - leg.StartUT) < 1.0 && burnt2.Add(step)) deltaV += step.DeltaV.Length;
                if (deltaV <= 0.0 && i > 1 && leg.StartUT <= plan.Legs[i - 1].StartUT + 1.0) continue;
                // The stops reached by the time of this orbit: those of the last state the plan met by its start, so that a stop is not counted before
                // the vessel is at it.
                int reached = entry.Reached;
                foreach (KeyValuePair<double, int> met in plan.ReachedAt)
                    if (met.Key <= leg.StartUT + 1.0) reached = Math.Max(reached, met.Value);
                RouteStep flown = Elements(leg.Orbit, leg.StartUT, leg.Air ? "pass through the air" : deltaV.ToString("0") + " m/s", reached);
                budget.Apply(flown, deltaV);
                entry.Steps.Add(flown);
            }
            AddReleases(entry, plan, lettings);
            PlannedStep end = plan.Steps.Count > 0 ? plan.Steps[plan.Steps.Count - 1] : null;
            // A pass through the air that the plan did not fly is not made without the player: the vessel would be left where the plan stops.
            if (end != null && end.Kind == NodePlanner.StepKind.Aerobrake && !end.Flown)
            {
                entry.Problem = "The route ends in a pass through the air to slow down, which is flown with the vessel loaded.";
                return null;
            }
            if (end != null && end.Kind == NodePlanner.StepKind.Rendezvous)
            {
                AddMeeting(vessel, entry, plan, budget, end);
                return null;
            }
            return TouchdownPlanner.Plan(vessel, entry, plan, budget, route, figures);
        }

        // The orbit as a step from the UT on.
        internal static RouteStep Elements(Orbit orbit, double ut, string why, int reached)
        {
            return new RouteStep
            {
                Reached = reached, UT = ut, Body = orbit.referenceBody.bodyName, Inclination = orbit.inclination, Eccentricity = orbit.eccentricity,
                SemiMajorAxis = orbit.semiMajorAxis, Lan = orbit.LAN, ArgumentOfPeriapsis = orbit.argumentOfPeriapsis, MeanAnomaly = orbit.meanAnomalyAtEpoch,
                Epoch = orbit.epoch, Why = why,
            };
        }

        // A section let go: when, and the parts that go.
        internal sealed class Letting
        {
            public double At;
            public PartsCut PartsCut;
            public PlannedStep Planned;
        }

        // The sections the plan lets go at its stops, in the order of their times, with the parts that go: the side of the separating part that the flight is
        // not made on, without the parts that went before.
        internal static List<Letting> PlanReleases(Vessel vessel, RoutePlan plan)
        {
            var found = new List<Letting>();
            var gone = new HashSet<uint>();
            foreach (PlannedStep planned in plan.Steps)
            {
                if (planned.Kind != NodePlanner.StepKind.Release || planned.Section == null) continue;
                Part separator = planned.Section.FindOn(vessel);
                if (separator == null) continue;
                var cut = new PartsCut { Name = vessel.vesselName + " " + planned.Section.Name, Type = VesselType.Probe };
                cut.Parts.AddRange(VesselDetach.SectionAway(vessel, separator, gone));
                foreach (uint part in cut.Parts) gone.Add(part);
                found.Add(new Letting { At = planned.At, PartsCut = cut, Planned = planned });
            }
            found.Sort((a, b) => a.At.CompareTo(b.At));
            return found;
        }

        // The sections the plan lets go are steps of their own, at the time of the state they are let go in.
        internal static void AddReleases(RouteEntry entry, RoutePlan plan, List<Letting> lettings)
        {
            foreach (Letting letting in lettings)
            {
                int reached = entry.Reached;
                foreach (KeyValuePair<double, int> met in plan.ReachedAt)
                    if (met.Key <= letting.At + 1.0) reached = Math.Max(reached, met.Value);
                var step = new RouteStep { Kind = StepKind.Release, UT = letting.At, Reached = reached, Why = "release of the " + letting.Planned.Section.Name.ToLowerInvariant() };
                step.Cuts.Add(letting.PartsCut);
                int place = entry.Steps.FindLastIndex(other => other.UT <= letting.At + 1e-3) + 1;
                entry.Steps.Insert(place, step);
            }
        }
    }
}
