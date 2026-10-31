using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Autopilot.Guidance;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;
using ReDefinition.Landing;
using UnityEngine;

namespace ReDefinition.Autopilot.Background
{
    // The autopilot's route flown while the player is away from the vessel, without a simulation. The vessel stays a real vessel on its orbit, in
    // the space centre, the tracking station and in another flight. The plan the player had is kept as events, each at its time: the orbit the
    // vessel is on after a burn, and the share of its propellant that burn leaves. When the clock reaches an event the vessel's orbit is set to
    // that one and its tanks are emptied by that share, as KSP does for a vessel on rails. KSP itself carries the vessel from one body's sphere
    // of influence into the next. A vessel the player takes into flight has its events dropped: the autopilot flies on from where it is.
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.SPACECENTER, GameScenes.FLIGHT, GameScenes.TRACKSTATION)]
    public sealed class BackgroundRoutes : ScenarioModule
    {
        internal static BackgroundRoutes Instance;

        // One thing that happens at a time: the vessel is on this orbit from then, with this share of its propellant kept.
        private sealed class Step
        {
            public double UT;
            public string Body;
            public double Inclination, Eccentricity, SemiMajorAxis, Lan, ArgumentOfPeriapsis, MeanAnomaly, Epoch;
            public double Keep = 1.0;
            public string Why = "";
            // The last stop of the route the vessel has reached once the step is made.
            public int Reached = -1;
            // The step is the touchdown: the orbit is the descent, and GroundUT the time it meets the ground. The step is made a few physics steps
            // earlier, before the game finds the vessel under the ground and destroys it.
            public bool Land;
            public double GroundUT;
            public TouchdownSite Site;
            // False for a step that only lets parts go: the orbit is then the vessel's own.
            public bool Orbital = true;
            // The share each part keeps of its propellant, by its persistent id, where the burn is shared over the vessel's stages.
            public readonly Dictionary<uint, double> Keeps = new Dictionary<uint, double>();
            // The parts let go in the step, in groups that become vessels.
            public readonly List<Cut> Cuts = new List<Cut>();
        }

        // Parts let go of the vessel: by their persistent ids, with the name and the kind of the vessel they make.
        private sealed class Cut
        {
            public string Name = "";
            public VesselType Type = VesselType.Debris;
            public readonly List<uint> Parts = new List<uint>();
        }

        // What a burn costs the vessel: the stages' propellant where KSP's stages of it are known, else one tank for all.
        private sealed class Budget
        {
            public Tank Tank;
            public IBurnShare Stages;
            public string Name = "";

            public void Apply(Step step, double deltaV)
            {
                if (Stages == null)
                {
                    step.Keep = Tank.Burn(deltaV);
                    return;
                }
                var gone = new List<uint>();
                Stages.Burn(deltaV, step.Keeps, gone);
                if (gone.Count == 0) return;
                var cut = new Cut { Name = Name + " Debris", Type = VesselType.Debris };
                cut.Parts.AddRange(gone);
                step.Cuts.Add(cut);
            }
        }

        // The propellant left and the vessel's mass, as the burns of the plan use them up.
        private sealed class Tank
        {
            public double Propellant, Mass, Exhaust;

            // The share of the propellant kept after a burn of this delta-v, in m/s.
            public double Burn(double deltaV)
            {
                if (deltaV <= 0.0 || Exhaust <= 0.0 || Propellant <= 0.0) return 1.0;
                double used = Math.Min(Propellant, Mass * (1.0 - Math.Exp(-deltaV / Exhaust)));
                double keep = 1.0 - used / Propellant;
                Propellant -= used;
                Mass -= used;
                return keep;
            }
        }

        // How far over the ground the vessel is put, in m: the game sets it down from there when it is loaded.
        // The game's ground lies about 3 m over the height the terrain gives (measured on Minmus), and a vessel set down inside it is thrown out.
        // The vessel stands that high and is let down slowly, which is cheap.
        private const double Clearance = 5.0;
        // A probe tries other heights and the game's own positioning on the ground.
        internal static double ExtraClearance;
        internal static bool TrustHeight = true;
        // How long a vessel set down is let down, in seconds, and the vessels in it with the time they began.
        private const float SettleTime = 60f;
        private readonly Dictionary<Guid, float> settling = new Dictionary<Guid, float>();

        // A vessel's route: its steps in the order of their times, and the propellants its engines burn.
        private sealed class Entry
        {
            public Guid Vessel;
            public string Name = "";
            public readonly List<string> Propellants = new List<string>();
            public readonly List<Step> Steps = new List<Step>();
            // Set once the vessel was seen out of the flight: before that it is the vessel the route is handed over from, still loaded.
            public bool Left;
            // The route the steps were planned for (RoutePlan.Signature) and the last stop reached by the steps made so far.
            public string Key = "";
            public int Reached = -1;
            // How far the vessel reaches below its reference point when it stands upright, in m: the height its reference point is set over the ground.
            public double Lowest;
            // How far it reaches to the side of its reference point.
            public double Footprint;
            // Where the vessel stands once it is set down: latitude, longitude and the height of its reference point. Its orbit is kept to this until it is loaded.
            public bool Down;
            public double DownLatitude, DownLongitude, DownHeight;
            // Why the route cannot be flown in the background, where it cannot.
            public string Problem;
        }

        private readonly List<Entry> entries = new List<Entry>();
        // The routes handed over and not yet taken in by a scenario: the scenario of a scene is destroyed as the next scene is asked for.
        private static readonly List<Entry> Staged = new List<Entry>();

        public override void OnAwake()
        {
            Instance = this;
            GameEvents.onVesselWillDestroy.Add(OnVesselWillDestroy);
        }

        private void OnDestroy()
        {
            GameEvents.onVesselWillDestroy.Remove(OnVesselWillDestroy);
            if (Instance == this) Instance = null;
        }

        // A vessel that the game destroys while it has steps left: the time, and the step it did not get to.
        private void OnVesselWillDestroy(Vessel vessel)
        {
            Entry entry = entries.Find(e => e.Vessel == vessel.id);
            if (entry == null || entry.Steps.Count == 0) return;
            Debug.LogWarning(Log.Tag + " Route: " + entry.Name + " was destroyed at UT " + Planetarium.GetUniversalTime().ToString("0") + " with " + entry.Steps.Count
                             + " step(s) left, the next at UT " + entry.Steps[0].UT.ToString("0") + " (" + entry.Steps[0].Why + "), " + vessel.situation + " round " + vessel.mainBody.bodyName
                             + " at " + vessel.altitude.ToString("0") + " m.");
        }

        // Keeps the plan's orbits for the vessel as steps, where the autopilot is to go on without the player. The plan's first leg is the orbit the vessel
        // is on; each later leg starts with a burn. Null where it was kept; else why not.
        internal static string Hand(Vessel vessel, RoutePlan plan, IList<Destination> route = null, IVesselFigures figures = null)
        {
            bool climb = plan != null && plan.Steps.Count > 0 && plan.Steps[0].Kind == NodePlanner.StepKind.Ascent && route != null;
            if (plan == null || (plan.Legs.Count < 2 && !climb)) return "The plan has no burn to fly.";
            Entry entry = Build(vessel, plan, route, figures);
            if (entry.Problem != null) return entry.Problem;
            if (entry.Steps.Count == 0) return "The plan has no orbit to go on to.";
            Staged.RemoveAll(old => old.Vessel == vessel.id);
            Staged.Add(entry);
            EnsureScenario();
            if (Instance == null) return null;
            Instance.Adopt();
            // The game takes the scenarios as they are now into the scene that follows.
            if (HighLogic.CurrentGame != null) HighLogic.CurrentGame.scenarios = ScenarioRunner.GetUpdatedProtoModules();
            return null;
        }

        // Whether a route is kept for the vessel that is still to be flown.
        internal static bool Holds(Vessel vessel)
        {
            foreach (Entry entry in Staged)
                if (entry.Vessel == vessel.id && entry.Steps.Count > 0) return true;
            if (Instance != null)
                foreach (Entry entry in Instance.entries)
                    if (entry.Vessel == vessel.id && entry.Steps.Count > 0 && !entry.Left) return true;
            return false;
        }

        // The UT of the last step kept for the vessel, zero where there is none.
        internal static double LastStep(Vessel vessel)
        {
            double last = 0.0;
            foreach (Entry entry in Staged)
                if (entry.Vessel == vessel.id && entry.Steps.Count > 0) last = Math.Max(last, entry.Steps[entry.Steps.Count - 1].UT);
            if (Instance != null)
                foreach (Entry entry in Instance.entries)
                    if (entry.Vessel == vessel.id && entry.Steps.Count > 0) last = Math.Max(last, entry.Steps[entry.Steps.Count - 1].UT);
            return last;
        }

        // The vessel flies on by itself no longer: its route is dropped, where the player turned the autopilot off or went on flying.
        internal static void Cancel(Vessel vessel)
        {
            Staged.RemoveAll(old => old.Vessel == vessel.id);
            if (Instance != null) Instance.entries.RemoveAll(old => old.Vessel == vessel.id);
        }

        // A game made before the scenario was there gets it now, and the scenario runner keeps it for the scenes after this one. A game that has it,
        // with its module destroyed as the scene is left, takes the staged routes into the module of the next scene.
        private static void EnsureScenario()
        {
            if (Instance != null || ScenarioRunner.Instance == null || HighLogic.CurrentGame == null) return;
            if (HighLogic.CurrentGame.scenarios.Exists(module => module.moduleName == typeof(BackgroundRoutes).Name)) return;
            ScenarioModule added = ScenarioRunner.Instance.AddModule(typeof(BackgroundRoutes).Name);
            if (added == null) return;
            added.targetScenes = new List<GameScenes> { GameScenes.SPACECENTER, GameScenes.FLIGHT, GameScenes.TRACKSTATION };
            ScenarioRunner.GetUpdatedProtoModules().Add(new ProtoScenarioModule(added));
        }

        // The staged routes join this scenario's; a route handed over later for the same vessel is the newer.
        private void Adopt()
        {
            foreach (Entry staged in Staged)
            {
                entries.RemoveAll(old => old.Vessel == staged.Vessel);
                entries.Add(staged);
            }
            Staged.Clear();
        }

        private static Entry Build(Vessel vessel, RoutePlan plan, IList<Destination> route, IVesselFigures figures)
        {
            var entry = new Entry { Vessel = vessel.id, Name = vessel.vesselName, Key = plan.Signature ?? "" };
            entry.Lowest = LowestPoint(vessel, out entry.Footprint, null);
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
            var tank = new Tank { Mass = vessel.GetTotalMass(), Exhaust = exhaust };
            foreach (Part part in vessel.parts)
                foreach (PartResource resource in part.Resources)
                    if (entry.Propellants.Contains(resource.resourceName)) tank.Propellant += resource.amount * resource.info.density;
            // The exhaust speed that gives the vessel's delta-v from its propellant, where KSP has worked that out; else that of its best engine.
            double total = vessel.VesselDeltaV != null ? vessel.VesselDeltaV.TotalDeltaVVac : 0.0;
            if (total > 0.0 && tank.Propellant > 0.0 && tank.Mass > tank.Propellant) tank.Exhaust = total / Math.Log(tank.Mass / (tank.Mass - tank.Propellant));
            var budget = new Budget { Tank = tank, Name = vessel.vesselName };
            IBurnShare stages = figures != null ? figures.StagesOf(vessel) : null;
            if (stages != null && stages.Ready) budget.Stages = stages;
            // A launch from the ground ends in the orbit asked for, and the rest of the route is planned from there.
            if (plan.Steps.Count > 0 && plan.Steps[0].Kind == NodePlanner.StepKind.Ascent && route != null && figures != null && vessel.mainBody.pqsController != null)
            {
                PlannedStep climb = plan.Steps[0];
                double launch = figures.LaunchDeltaV(vessel.mainBody, climb.Altitude);
                var ascent = new Step { Why = "ascent, " + launch.ToString("0") + " m/s", Reached = entry.Reached };
                double began = stages != null ? stages.Elapsed : 0.0;
                budget.Apply(ascent, launch);
                if (stages != null && stages.Short > 0.0)
                {
                    entry.Problem = "The vessel does not have the delta-v for the launch: it is short of " + stages.Short.ToString("0") + " m/s.";
                    return entry;
                }
                // The time of the burn: the stages' own, and where they are not known the time the vessel needs at its thrust.
                double burnTime = stages != null && stages.Ready ? stages.Elapsed - began : 0.0;
                if (burnTime <= 0.0) burnTime = Math.Max(60.0, launch / Math.Max(1.0, (vessel.VesselDeltaV != null ? vessel.VesselDeltaV.TotalDeltaVVac : launch) / 600.0));
                double endUT;
                Orbit orbit = AscentOrbit.Make(vessel, climb.Altitude, burnTime, out endUT);
                ascent.UT = endUT;
                ascent.Body = vessel.mainBody.bodyName;
                ascent.Inclination = orbit.inclination;
                ascent.Eccentricity = orbit.eccentricity;
                ascent.SemiMajorAxis = orbit.semiMajorAxis;
                ascent.Lan = orbit.LAN;
                ascent.ArgumentOfPeriapsis = orbit.argumentOfPeriapsis;
                ascent.MeanAnomaly = orbit.meanAnomalyAtEpoch;
                ascent.Epoch = orbit.epoch;
                entry.Steps.Add(ascent);
                plan = RouteSimulation.Run(vessel, route, false, orbit, endUT);
            }
            var burnt2 = new HashSet<PlannedStep>();
            for (int i = 1; i < plan.Legs.Count; i++)
            {
                PlannedLeg leg = plan.Legs[i];
                if (leg.Orbit == null || leg.Orbit.referenceBody == null) continue;
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
                Step flown = Elements(leg.Orbit, leg.StartUT, deltaV.ToString("0") + " m/s", reached);
                budget.Apply(flown, deltaV);
                entry.Steps.Add(flown);
            }
            AddReleases(vessel, entry, plan);
            AddTouchdown(vessel, entry, plan, budget);
            if (stages != null && stages.Short > 0.0) Debug.LogWarning(Log.Tag + " Route: " + vessel.vesselName + " is short of " + stages.Short.ToString("0") + " m/s for the plan.");
            if (entry.Steps.Count > 0)
                Debug.Log(Log.Tag + " Route: " + vessel.vesselName + " flies on in the background: " + entry.Steps.Count + " step(s) up to UT " + entry.Steps[entry.Steps.Count - 1].UT.ToString("0") + ".");
            return entry;
        }

        // The time warp is slowed as a step comes near, so that the game never carries a vessel past the step in one frame.
        // A vessel that has gone through the ground in that frame is destroyed before the touchdown can set it down.
        private double slowedFor;

        private void LimitWarp()
        {
            TimeWarp warp = TimeWarp.fetch;
            if (warp == null || TimeWarp.WarpMode != TimeWarp.Modes.HIGH || TimeWarp.CurrentRateIndex == 0 || FlightGlobals.fetch == null || FlightGlobals.Vessels == null) return;
            double next = double.MaxValue;
            foreach (Entry entry in entries)
            {
                Vessel vessel = FlightGlobals.Vessels.Find(v => v.id == entry.Vessel);
                if (vessel != null && !vessel.loaded && entry.Steps.Count > 0) next = Math.Min(next, entry.Steps[0].UT);
            }
            double ahead = next - Planetarium.GetUniversalTime();
            if (ahead <= 0.0 || next == double.MaxValue) return;
            int index = TimeWarp.CurrentRateIndex;
            int slower = index;
            while (slower > 0 && warp.warpRates[slower] * Time.fixedDeltaTime * 2.0 > ahead) slower--;
            if (slower == index) return;
            TimeWarp.SetRate(slower, true, false);
            if (slowedFor == next) return;
            slowedFor = next;
            Debug.Log(Log.Tag + " Route: the time warp is slowed, " + ahead.ToString("0") + " s before the next step.");
        }

        // How far the vessel's solid parts reach below its reference point along its up axis, in m: the lowest corner of any collider's bounds.
        private static double LowestPoint(Vessel vessel, out double footprint, ICollection<uint> except)
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

        // A vessel set down is let down on the ground at the speed of a landing, whatever the height it was set down at.
        // Its fall is held to that speed until it rests, and a push out of the ground is taken out.
        private void FixedUpdate()
        {
            if (settling.Count == 0) return;
            var done = new List<Guid>();
            foreach (KeyValuePair<Guid, float> began in settling)
            {
                Vessel vessel = FlightGlobals.fetch != null ? FlightGlobals.Vessels.Find(v => v.id == began.Key) : null;
                if (vessel == null || !vessel.loaded || Time.time - began.Value > SettleTime)
                {
                    done.Add(began.Key);
                    continue;
                }
                if (vessel.packed) continue;
                double limit = LandingGuide.TouchdownSpeed, speed = vessel.verticalSpeed;
                if (speed < -limit) vessel.ChangeWorldVelocity(vessel.upAxis * (-limit - speed));
                else if (speed > limit) vessel.ChangeWorldVelocity(-vessel.upAxis * (speed - limit));
                // It does not slide or turn meanwhile: ground a few metres off, or the water, would tip a tall vessel over before it has settled.
                Vector3d sideways = vessel.srf_velocity - vessel.upAxis * Vector3d.Dot(vessel.srf_velocity, vessel.upAxis);
                vessel.ChangeWorldVelocity(-sideways);
                foreach (Part part in vessel.parts)
                    if (part.Rigidbody != null) part.Rigidbody.angularVelocity = Vector3.zero;
            }
            foreach (Guid id in done) settling.Remove(id);
        }

        // The orbit as a step from the UT on.
        private static Step Elements(Orbit orbit, double ut, string why, int reached)
        {
            return new Step
            {
                Reached = reached, UT = ut, Body = orbit.referenceBody.bodyName, Inclination = orbit.inclination, Eccentricity = orbit.eccentricity,
                SemiMajorAxis = orbit.semiMajorAxis, Lan = orbit.LAN, ArgumentOfPeriapsis = orbit.argumentOfPeriapsis, MeanAnomaly = orbit.meanAnomalyAtEpoch,
                Epoch = orbit.epoch, Why = why,
            };
        }

        // The sections the plan lets go at its stops are steps of their own, at the time of the state they are let go in.
        private static void AddReleases(Vessel vessel, Entry entry, RoutePlan plan)
        {
            foreach (PlannedStep planned in plan.Steps)
            {
                if (planned.Kind != NodePlanner.StepKind.Release || planned.Section == null) continue;
                Part separator = planned.Section.FindOn(vessel);
                if (separator == null) continue;
                var cut = new Cut { Name = vessel.vesselName + " " + planned.Section.Name, Type = VesselType.Probe };
                cut.Parts.AddRange(VesselDetach.SectionAway(vessel, separator));
                int reached = entry.Reached;
                foreach (KeyValuePair<double, int> met in plan.ReachedAt)
                    if (met.Key <= planned.At + 1.0) reached = Math.Max(reached, met.Value);
                var step = new Step { Orbital = false, UT = planned.At, Reached = reached, Why = "release of the " + planned.Section.Name.ToLowerInvariant() };
                step.Cuts.Add(cut);
                int place = entry.Steps.FindLastIndex(other => other.UT <= planned.At) + 1;
                entry.Steps.Insert(place, step);
            }
        }

        // A plan that ends in a landing is carried to the ground. From an orbit that stays above it, a burn at the orbit's start brings the periapsis
        // down to it. The vessel touches down where the orbit meets the ground. A body with air takes the vessel down on its parachutes.
        // An airless one costs the speed the vessel arrives with, the delta-v of a landing burn.
        private static void AddTouchdown(Vessel vessel, Entry entry, RoutePlan plan, Budget budget)
        {
            PlannedStep last = plan.Steps.Count > 0 ? plan.Steps[plan.Steps.Count - 1] : null;
            if (last == null || last.Kind != NodePlanner.StepKind.Landing || plan.Legs.Count == 0) return;
            PlannedLeg leg = plan.Legs[plan.Legs.Count - 1];
            Orbit orbit = leg.Orbit;
            CelestialBody body = orbit != null ? orbit.referenceBody : null;
            // What is left of the vessel by then is what stands on the ground: the parts let go on the way do not reach down.
            var let = new HashSet<uint>();
            foreach (Step before in entry.Steps)
                foreach (Cut cut in before.Cuts) foreach (uint part in cut.Parts) let.Add(part);
            if (let.Count > 0) entry.Lowest = LowestPoint(vessel, out entry.Footprint, let);
            if (body == null || body.pqsController == null || !body.hasSolidSurface) return;
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
                Step deorbit = Elements(descent, from, "deorbit, " + burn.ToString("0") + " m/s", entry.Steps.Count > 0 ? entry.Steps[entry.Steps.Count - 1].Reached : entry.Reached);
                budget.Apply(deorbit, burn);
                entry.Steps.Add(deorbit);
                orbit = descent;
            }
            double periapsis = orbit.NextPeriapsisTime(from);
            if (periapsis < from || orbit.getRelativePositionAtUT(from).magnitude <= body.Radius || orbit.getRelativePositionAtUT(periapsis).magnitude > body.Radius + 1.0) return;
            // The step is made when the orbit comes down to the highest ground of the body: from there the vessel can meet the ground at any moment, and
            // the game destroys it when it does. The place is where the descent meets the ground at the point it comes to, which the terrain there gives.
            double now = Planetarium.GetUniversalTime();
            double trigger = FallsTo(orbit, from, periapsis, body.Radius + Math.Max(0.0, body.pqsController.mapMaxHeight));
            double high = FallsTo(orbit, from, periapsis, body.Radius);
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
            TouchdownSite site = Touchdown.Find(body, latitude, longitude, entry.Lowest, entry.Footprint, Clearance + ExtraClearance);
            if (!site.Found) return;
            double arrival = body.atmosphere ? 0.0 : orbit.getOrbitalSpeedAtDistance(body.Radius);
            Step touch = Elements(orbit, trigger, "landing, " + arrival.ToString("0") + " m/s", last.Goal);
            budget.Apply(touch, arrival);
            touch.Land = true;
            touch.GroundUT = high;
            touch.Site = site;
            entry.Steps.Add(touch);
        }

        // The time between two moments at which the orbit's radius falls to the given one; the radius only falls there.
        private static double FallsTo(Orbit orbit, double from, double to, double radius)
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

        // Every frame: the steps whose time has come are made, in order, for each vessel that is not loaded.
        private void Update()
        {
            if (Staged.Count > 0) Adopt();
            LimitWarp();
            if (entries.Count == 0 || FlightGlobals.fetch == null || FlightGlobals.Vessels == null || FlightGlobals.Vessels.Count == 0 || HighLogic.CurrentGame == null) return;
            double now = Planetarium.GetUniversalTime();
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Entry entry = entries[i];
                Vessel vessel = FlightGlobals.Vessels.Find(v => v.id == entry.Vessel);
                if (vessel == null || vessel.protoVessel == null)
                {
                    entries.RemoveAt(i);
                    continue;
                }
                if (!vessel.loaded) entry.Left = true;
                // In the player's hands: the autopilot flies it from where it is.
                if (entry.Down && (!vessel.loaded || vessel.packed)) StandOrbit(vessel, entry);
                if (vessel.loaded && entry.Left && !(entry.Down && vessel.packed))
                {
                    Debug.Log(Log.Tag + " Route: " + entry.Name + " is loaded; the background route ends here, with " + (entry.Reached + 1) + " stop(s) reached.");
                    if (entry.Down) settling[vessel.id] = Time.time;
                    RouteState state = RouteState.Of(vessel);
                    if (state != null && entry.Reached >= 0)
                        state.Progress = new RouteProgress { Key = entry.Key, Reached = Math.Max(entry.Reached, state.Progress.Key == entry.Key ? state.Progress.Reached : -1) };
                    entries.RemoveAt(i);
                    continue;
                }
                while (!vessel.loaded && entry.Steps.Count > 0 && entry.Steps[0].UT <= now)
                {
                    if (Make(vessel, entry, entry.Steps[0])) entry.Reached = entry.Steps[0].Reached;
                    entry.Steps.RemoveAt(0);
                }
            }
        }

        // The vessel on the orbit of the step from its time, with the propellant it leaves.
        private static bool Make(Vessel vessel, Entry entry, Step step)
        {
            if (!step.Orbital)
            {
                Let(vessel, entry, step);
                Debug.Log(Log.Tag + " Route: " + entry.Name + " made " + step.Why + " at UT " + step.UT.ToString("0") + ".");
                return true;
            }
            CelestialBody body = FlightGlobals.Bodies.Find(b => b.bodyName == step.Body);
            if (body == null) return false;
            var orbit = new Orbit(step.Inclination, step.Eccentricity, step.SemiMajorAxis, step.Lan, step.ArgumentOfPeriapsis, step.MeanAnomaly, step.Epoch, body);
            if (step.Land) return Touch(vessel, entry, step, body, orbit);
            vessel.orbitDriver.orbit.UpdateFromOrbitAtUT(orbit, step.UT, body);
            vessel.orbitDriver.UpdateOrbit();
            if (vessel.situation == Vessel.Situations.PRELAUNCH || vessel.LandedOrSplashed || vessel.situation == Vessel.Situations.FLYING || vessel.situation == Vessel.Situations.SUB_ORBITAL)
                InOrbit(vessel);
            vessel.protoVessel.orbitSnapShot = new OrbitSnapshot(vessel.orbitDriver.orbit);
            Let(vessel, entry, step);
            Debug.Log(Log.Tag + " Route: " + entry.Name + " flew its burn of " + step.Why + " at UT " + step.UT.ToString("0") + " and is now round " + step.Body
                      + " (made " + (Planetarium.GetUniversalTime() - step.UT).ToString("0.00", CultureInfo.InvariantCulture) + " s late).");
            return true;
        }

        // A vessel that was on the ground or in the air is in an orbit now: the vessel and its saved copy say so, and the orbit is flown by the game again.
        private static void InOrbit(Vessel vessel)
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
        private static void Let(Vessel vessel, Entry entry, Step step)
        {
            foreach (ProtoPartSnapshot part in vessel.protoVessel.protoPartSnapshots)
            {
                double keep;
                if (!step.Keeps.TryGetValue(part.persistentId, out keep)) keep = step.Keep;
                if (keep >= 1.0) continue;
                foreach (ProtoPartResourceSnapshot resource in part.resources)
                    if (entry.Propellants.Contains(resource.resourceName)) resource.amount *= keep;
            }
            foreach (Cut cut in step.Cuts)
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
        private static void StandOrbit(Vessel vessel, Entry entry)
        {
            CelestialBody body = vessel.orbitDriver.orbit.referenceBody;
            Vector3d surface = body.GetWorldSurfacePosition(entry.DownLatitude, entry.DownLongitude, entry.DownHeight) - body.position;
            vessel.orbitDriver.orbit.UpdateFromStateVectors(surface.xzy, Vector3d.Cross(body.angularVelocity, surface).xzy, body, Planetarium.GetUniversalTime());
        }

        // The vessel set down where the descent met the ground, or on the water: the place from the orbit and the turn of the body since, the height
        // from the body's terrain. The game sets it on the ground when it is loaded, as it does a vessel that was saved on the ground.
        private static bool Touch(Vessel vessel, Entry entry, Step step, CelestialBody body, Orbit descent)
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
            vessel.PQSminLevel = TrustHeight ? body.pqsController.minLevel : 0;
            vessel.PQSmaxLevel = TrustHeight ? body.pqsController.maxLevel : 0;
            vessel.skipGroundPositioning = TrustHeight;
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
            proto.PQSminLevel = TrustHeight ? body.pqsController.minLevel : 0;
            proto.PQSmaxLevel = TrustHeight ? body.pqsController.maxLevel : 0;
            proto.skipGroundPositioning = TrustHeight;
            proto.vesselSpawning = true;
            proto.landedAt = "";
            proto.displaylandedAt = "";
            proto.orbitSnapShot = new OrbitSnapshot(vessel.orbitDriver.orbit);
            Debug.Log(Log.Tag + " Route: " + entry.Name + " set down on " + body.bodyName + " at " + latitude.ToString("0.00", CultureInfo.InvariantCulture) + ", "
                      + longitude.ToString("0.00", CultureInfo.InvariantCulture) + (water ? " on the water" : ", " + ground.ToString("0") + " m up") + ", reference point at " + height.ToString("0.0", CultureInfo.InvariantCulture) + " m, " + entry.Lowest.ToString("0.0", CultureInfo.InvariantCulture) + " m over the lowest part, the ground "
                      + site.Tilt.ToString("0.0", CultureInfo.InvariantCulture) + " degrees steep, uneven by " + site.Score.ToString("0.0", CultureInfo.InvariantCulture) + " degrees, " + site.Shift.ToString("0") + " m from where the descent met it.");
            return true;
        }

        public override void OnLoad(ConfigNode node)
        {
            entries.Clear();
            foreach (ConfigNode route in node.GetNodes("ROUTE"))
            {
                var entry = new Entry { Name = route.GetValue("name") ?? "", Key = route.GetValue("key") ?? "", Lowest = Read(route, "lowest"), Footprint = Read(route, "footprint"), Reached = (int)Read(route, "reached", -1.0) };
                try
                {
                    entry.Vessel = new Guid(route.GetValue("vessel"));
                }
                catch (Exception)
                {
                    continue;
                }
                entry.Down = route.HasValue("downHeight");
                entry.DownLatitude = Read(route, "downLatitude");
                entry.DownLongitude = Read(route, "downLongitude");
                entry.DownHeight = Read(route, "downHeight");
                foreach (string name in route.GetValues("propellant")) entry.Propellants.Add(name);
                foreach (ConfigNode node2 in route.GetNodes("STEP"))
                {
                    var step = new Step { Body = node2.GetValue("body") ?? "", Why = node2.GetValue("why") ?? "" };
                    step.UT = Read(node2, "ut");
                    step.Inclination = Read(node2, "inc");
                    step.Eccentricity = Read(node2, "ecc");
                    step.SemiMajorAxis = Read(node2, "sma");
                    step.Lan = Read(node2, "lan");
                    step.ArgumentOfPeriapsis = Read(node2, "arg");
                    step.MeanAnomaly = Read(node2, "mna");
                    step.Epoch = Read(node2, "epoch");
                    step.Keep = Read(node2, "keep", 1.0);
                    step.Reached = (int)Read(node2, "reached", -1.0);
                    step.Orbital = !node2.HasValue("letgo");
                    foreach (string kept in node2.GetValues("kept"))
                    {
                        string[] pieces = kept.Split(' ');
                        uint id;
                        double share;
                        if (pieces.Length == 2 && uint.TryParse(pieces[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out id)
                            && double.TryParse(pieces[1], NumberStyles.Float, CultureInfo.InvariantCulture, out share)) step.Keeps[id] = share;
                    }
                    foreach (ConfigNode cutNode in node2.GetNodes("CUT"))
                    {
                        var cut = new Cut { Name = cutNode.GetValue("name") ?? "" };
                        try { cut.Type = (VesselType)Enum.Parse(typeof(VesselType), cutNode.GetValue("type") ?? "Debris"); } catch (Exception) { cut.Type = VesselType.Debris; }
                        foreach (string part in cutNode.GetValues("part"))
                        {
                            uint id;
                            if (uint.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) cut.Parts.Add(id);
                        }
                        step.Cuts.Add(cut);
                    }
                    step.Land = node2.HasValue("land");
                    step.GroundUT = Read(node2, "ground");
                    if (step.Land)
                    {
                        step.Site = new TouchdownSite
                        {
                            Latitude = Read(node2, "siteLatitude"), Longitude = Read(node2, "siteLongitude"), Height = Read(node2, "siteHeight"), Ground = Read(node2, "siteGround"),
                            Tilt = Read(node2, "siteTilt"), Score = Read(node2, "siteScore"), Shift = Read(node2, "siteShift"), Found = true,
                            Water = node2.GetValue("siteWater") == "True", Rotation = KSPUtil.ParseQuaternion(node2.GetValue("siteRotation") ?? "0,0,0,1"),
                        };
                    }
                    entry.Steps.Add(step);
                }
                entry.Left = true;
                entries.Add(entry);
            }
            Adopt();
        }

        public override void OnSave(ConfigNode node)
        {
            foreach (Entry entry in entries)
            {
                ConfigNode route = node.AddNode("ROUTE");
                route.AddValue("vessel", entry.Vessel.ToString());
                route.AddValue("name", entry.Name);
                route.AddValue("key", entry.Key);
                Write(route, "lowest", entry.Lowest);
                Write(route, "footprint", entry.Footprint);
                if (entry.Down)
                {
                    Write(route, "downLatitude", entry.DownLatitude);
                    Write(route, "downLongitude", entry.DownLongitude);
                    Write(route, "downHeight", entry.DownHeight);
                }
                route.AddValue("reached", entry.Reached);
                foreach (string name in entry.Propellants) route.AddValue("propellant", name);
                foreach (Step step in entry.Steps)
                {
                    ConfigNode node2 = route.AddNode("STEP");
                    node2.AddValue("body", step.Body ?? "");
                    node2.AddValue("why", step.Why);
                    Write(node2, "ut", step.UT);
                    Write(node2, "inc", step.Inclination);
                    Write(node2, "ecc", step.Eccentricity);
                    Write(node2, "sma", step.SemiMajorAxis);
                    Write(node2, "lan", step.Lan);
                    Write(node2, "arg", step.ArgumentOfPeriapsis);
                    Write(node2, "mna", step.MeanAnomaly);
                    Write(node2, "epoch", step.Epoch);
                    Write(node2, "keep", step.Keep);
                    node2.AddValue("reached", step.Reached);
                    if (!step.Orbital) node2.AddValue("letgo", true);
                    foreach (KeyValuePair<uint, double> kept in step.Keeps)
                        node2.AddValue("kept", kept.Key.ToString(CultureInfo.InvariantCulture) + " " + kept.Value.ToString("R", CultureInfo.InvariantCulture));
                    foreach (Cut cut in step.Cuts)
                    {
                        ConfigNode cutNode = node2.AddNode("CUT");
                        cutNode.AddValue("name", cut.Name);
                        cutNode.AddValue("type", cut.Type.ToString());
                        foreach (uint part in cut.Parts) cutNode.AddValue("part", part.ToString(CultureInfo.InvariantCulture));
                    }
                    if (step.Land)
                    {
                        node2.AddValue("land", true);
                        Write(node2, "ground", step.GroundUT);
                        Write(node2, "siteLatitude", step.Site.Latitude);
                        Write(node2, "siteLongitude", step.Site.Longitude);
                        Write(node2, "siteHeight", step.Site.Height);
                        Write(node2, "siteGround", step.Site.Ground);
                        Write(node2, "siteTilt", step.Site.Tilt);
                        Write(node2, "siteScore", step.Site.Score);
                        Write(node2, "siteShift", step.Site.Shift);
                        node2.AddValue("siteWater", step.Site.Water);
                        node2.AddValue("siteRotation", KSPUtil.WriteQuaternion(step.Site.Rotation));
                    }
                }
            }
        }

        private static double Read(ConfigNode node, string name, double fallback = 0.0)
        {
            double value;
            return double.TryParse(node.GetValue(name), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private static void Write(ConfigNode node, string name, double value)
        {
            node.AddValue(name, value.ToString("R", CultureInfo.InvariantCulture));
        }
    }
}
