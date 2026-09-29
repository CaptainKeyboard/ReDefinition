using System.Collections.Generic;
using System;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // What the planner takes of the vessel in the editor, read from KSP's own
    // delta-v simulation (ShipConstruct.vesselDeltaV, VesselDeltaV,
    // DeltaVStageInfo; decompiled) for the situation its delta-v app holds
    // (DeltaVGlobals.DeltaVAppValues: body, sea level, altitude or vacuum). KSP
    // shows the same numbers in its staging stack and its delta-v app, so the
    // two never disagree. Worked out here beside it: every part as the planner
    // takes it, and each stage's thrust at the surface of each body with air.
    internal sealed class EditorReadout
    {
        // In KSP's order, stage 0 first.
        public List<StageRow> Stages = new List<StageRow>();
        // Every part as the planner finds the lander and the capsule by it
        // (VesselShape).
        public List<PartFacts> PartFacts = new List<PartFacts>();
        // The parts as the planner's own simulation takes them (StageSim), and per
        // resource its density in tonnes per unit; null where no engine breathes air
        // and KSP's stages are the planner's.
        public List<SimPart> Sim;
        public Dictionary<string, double> Density;

        public double WetMass;
        // The resources in it, without crew (ShipConstruct.GetShipMass).
        public double FuelMass;
        // The electric charge the vessel holds as it starts, in units.
        public double ElectricCharge;
        public bool HasShip;

        // Read again: the stock simulation runs by itself as the vessel changes.
        internal static EditorReadout Read()
        {
            EditorReadout readout = new EditorReadout();
            ShipConstruct ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
            if (ship == null || ship.parts == null || ship.parts.Count == 0) return readout;

            readout.HasShip = true;
            float dryMass, fuelMass;
            // With the crew and their inventory, as KSP's mass limit counts them
            // (EngineersReport; decompiled).
            readout.WetMass = ship.GetShipMass(out dryMass, out fuelMass, ShipConstruction.ShipManifest);
            readout.FuelMass = fuelMass;
            foreach (Part part in ship.parts)
                if (part != null && part.Resources.Contains("ElectricCharge")) readout.ElectricCharge += part.Resources["ElectricCharge"].amount;

            VesselDeltaV deltaV = ship.vesselDeltaV;
            List<DeltaVStageInfo> infos = deltaV != null ? deltaV.OperatingStageInfo : null;
            if (infos == null || infos.Count == 0) return readout;

            List<DeltaVPartInfo> partInfos = deltaV.PartInfo;
            List<StageRow> rows = new List<StageRow>();
            foreach (DeltaVStageInfo info in infos)
                if (info != null) rows.Add(RowOf(info));
            rows.Sort((a, b) => a.Number.CompareTo(b.Number));
            readout.Stages = rows;
            readout.PartFacts = FactsOf(ship.parts, partInfos);
            HashSet<string> air = AirResources(ship.parts);
            if (BreathesAir(ship.parts, air))
            {
                readout.Density = new Dictionary<string, double>();
                readout.Sim = SimOf(ship.parts, readout.PartFacts, air, readout.Density, TopStart(readout.Stages));
            }
            return readout;
        }

        // Every part as the planner finds the lander and the capsule by it: its place
        // in the part tree, what it is, and its masses from KSP's simulation
        // (DeltaVPartInfo.decoupleStage, stageFuelMass). Landing legs are wheels of
        // the LEG type (ModuleWheelBase.wheelType); a docking port separates by its
        // ModuleDockingNode, a radial decoupler by ModuleAnchoredDecoupler, any other
        // decoupler by ModuleDecouple.
        private static List<PartFacts> FactsOf(List<Part> parts, List<DeltaVPartInfo> partInfos)
        {
            Dictionary<Part, DeltaVPartInfo> infos = new Dictionary<Part, DeltaVPartInfo>();
            if (partInfos != null)
                foreach (DeltaVPartInfo info in partInfos)
                    if (info != null && info.part != null) infos[info.part] = info;
            Dictionary<Part, int> index = new Dictionary<Part, int>();
            for (int i = 0; i < parts.Count; i++)
                if (parts[i] != null) index[parts[i]] = i;
            // The propellants any RCS thruster on the vessel burns.
            HashSet<string> rcsPropellants = new HashSet<string>();
            foreach (Part part in parts)
                if (part != null)
                    foreach (ModuleRCS rcs in part.FindModulesImplementing<ModuleRCS>())
                        if (rcs != null && rcs.propellants != null)
                            foreach (Propellant propellant in rcs.propellants) rcsPropellants.Add(propellant.name);

            List<PartFacts> facts = new List<PartFacts>();
            foreach (Part part in parts)
            {
                if (part == null)
                {
                    facts.Add(new PartFacts());
                    continue;
                }
                bool legs = false;
                foreach (ModuleWheelBase wheel in part.FindModulesImplementing<ModuleWheelBase>())
                    if (wheel != null && wheel.wheelType == WheelType.LEG) legs = true;
                int parent;
                DeltaVPartInfo info;
                infos.TryGetValue(part, out info);
                double dry = part.mass;
                PartFacts fact = new PartFacts
                {
                    Parent = part.parent != null && index.TryGetValue(part.parent, out parent) ? parent : -1,
                    DecoupleStage = info != null ? info.decoupleStage : -1,
                    ActivatesAt = part.inverseStage,
                    Command = part.HasModuleImplementing<ModuleCommand>(),
                    Engine = HasThrust(part),
                    Legs = legs,
                    // RealChute's parachutes are a module of their own.
                    Parachute = part.HasModuleImplementing<ModuleParachute>() || part.Modules.Contains("RealChuteModule"),
                    HeatShield = part.HasModuleImplementing<ModuleAblator>(),
                    Lift = part.HasModuleImplementing<ModuleLiftingSurface>() && !part.HasModuleImplementing<ModuleCommand>()
                           && !part.HasModuleImplementing<ModuleAblator>(),
                    Wheels = part.FindModulesImplementing<ModuleWheelBase>().Exists(wheel => wheel != null && wheel.wheelType != WheelType.LEG),
                    Clamp = part.HasModuleImplementing<LaunchClamp>(),
                    Crew = part.CrewCapacity,
                    Separator = part.HasModuleImplementing<ModuleDockingNode>() ? Separation.DockingPort
                        : part.HasModuleImplementing<ModuleAnchoredDecoupler>() ? Separation.Anchored
                        : part.HasModuleImplementing<ModuleDecouple>() ? Separation.Decoupler : Separation.None,
                    Id = part.persistentId,
                    Title = part.partInfo != null ? part.partInfo.title : part.name,
                    DryMass = dry,
                    Mass = dry + part.GetResourceMass(),
                    Tag = part,
                };
                foreach (ModuleRCS rcs in part.FindModulesImplementing<ModuleRCS>())
                {
                    if (rcs == null || !rcs.moduleIsEnabled) continue;
                    double isp = rcs.atmosphereCurve != null ? rcs.atmosphereCurve.Evaluate(0f) : 0.0;
                    if (isp <= 0.0) continue;
                    // As ModuleRCS applies it per nozzle (decompiled).
                    double power = rcs.thrusterPower * rcs.thrustPercentage * 0.01;
                    fact.RcsThrust += power;
                    fact.RcsThrustOverIsp += power / isp;
                    AddMix(fact.RcsMix, rcs.propellants, power / isp);
                }
                for (int i = 0; i < part.Resources.Count; i++)
                {
                    PartResource resource = part.Resources[i];
                    if (resource == null || resource.info == null || !resource.flowState
                        || !rcsPropellants.Contains(resource.resourceName)) continue;
                    double held;
                    fact.RcsResources.TryGetValue(resource.resourceName, out held);
                    fact.RcsResources[resource.resourceName] = held + resource.amount * resource.info.density;
                }
                if (info != null && info.stageFuelMass != null)
                    for (int i = 0; i < info.stageFuelMass.Count; i++)
                    {
                        DeltaVPartInfo.PartStageFuelMass stage = info.stageFuelMass.At(i);
                        if (stage != null) fact.StageFuel[info.stageFuelMass.KeyAt(i)] = new double[] { stage.startMass, stage.endMass };
                    }
                facts.Add(fact);
            }
            return facts;
        }

        // ------------------------------------------------------ own simulation

        // The resources engines take in from the air: IntakeAir, as KSP's simulation
        // knows it (DeltaVGlobals.PropellantsToIgnore), and whatever the vessel's
        // intakes gather (ModuleResourceIntake.resourceName), a mod's own air.
        private static HashSet<string> AirResources(List<Part> parts)
        {
            HashSet<string> air = new HashSet<string> { "IntakeAir" };
            foreach (Part part in parts)
                if (part != null)
                    foreach (ModuleResourceIntake intake in part.FindModulesImplementing<ModuleResourceIntake>())
                        if (intake != null && !string.IsNullOrEmpty(intake.resourceName)) air.Add(intake.resourceName);
            return air;
        }

        private static bool BreathesAir(List<Part> parts, HashSet<string> air)
        {
            foreach (Part part in parts)
                if (part != null)
                    foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
                        if (engine != null && engine.propellants != null && engine.propellants.Exists(p => p != null && air.Contains(p.name)))
                            return true;
            return false;
        }

        // Every part as StageSim takes it, in the order of `facts`: its place, how
        // fuel crosses it, its resources that can flow, and its engine's rocket and
        // air-breathing modes. A fuel line (CModuleFuelLine on a CompoundPart) feeds
        // its target from the part it is attached to.
        // The mass KSP's simulation starts from: its first stage's.
        private static double TopStart(List<StageRow> rows)
        {
            double start = 0.0;
            int top = int.MinValue;
            foreach (StageRow row in rows)
                if (row.Number > top)
                {
                    top = row.Number;
                    start = row.StartMass;
                }
            return start;
        }

        // `start`: KSP's start mass; what it counts beyond the parts and their
        // resources (the crew) goes to the root part.
        private static List<SimPart> SimOf(List<Part> parts, List<PartFacts> facts, HashSet<string> air, Dictionary<string, double> density,
                                           double start)
        {
            List<SimPart> sim = new List<SimPart>();
            Dictionary<Part, int> index = new Dictionary<Part, int>();
            for (int i = 0; i < parts.Count; i++)
                if (parts[i] != null) index[parts[i]] = i;
            for (int i = 0; i < parts.Count; i++)
            {
                Part part = parts[i];
                PartFacts fact = i < facts.Count ? facts[i] : new PartFacts();
                SimPart simPart = new SimPart
                {
                    Parent = fact.Parent, DecoupleStage = fact.DecoupleStage, ActivatesAt = part != null ? part.inverseStage : -1,
                    Clamp = fact.Clamp, Crossfeed = part == null || part.fuelCrossFeed, Fixed = part != null ? part.mass : 0.0,
                };
                sim.Add(simPart);
                if (part == null) continue;
                for (int r = 0; r < part.Resources.Count; r++)
                {
                    PartResource resource = part.Resources[r];
                    if (resource == null || resource.info == null) continue;
                    double unit = resource.info.density;
                    density[resource.resourceName] = unit;
                    if (unit <= 0.0) continue;
                    if (!resource.flowState)
                    {
                        simPart.Fixed += resource.amount * unit;
                        continue;
                    }
                    double held;
                    simPart.Amount.TryGetValue(resource.resourceName, out held);
                    simPart.Amount[resource.resourceName] = held + resource.amount;
                }
                EnginesOf(part, air, simPart);
            }
            for (int i = 0; i < sim.Count; i++)
                if (sim[i].Parent >= 0 && sim[i].Parent < sim.Count) sim[sim[i].Parent].Children.Add(i);
            double total = 0.0;
            foreach (SimPart part in sim)
            {
                if (part.Clamp) continue;
                total += part.Fixed;
                foreach (KeyValuePair<string, double> held in part.Amount)
                {
                    double unit;
                    if (density.TryGetValue(held.Key, out unit)) total += held.Value * unit;
                }
            }
            int root = sim.FindIndex(part => part.Parent < 0 && !part.Clamp);
            if (root >= 0 && start > 0.0) sim[root].Fixed = Math.Max(0.0, sim[root].Fixed + start - total);
            for (int i = 0; i < parts.Count; i++)
            {
                CompoundPart line = parts[i] as CompoundPart;
                int target, source;
                if (line == null || line.target == null || line.parent == null || !line.HasModuleImplementing<CompoundParts.CModuleFuelLine>()) continue;
                if (index.TryGetValue(line.target, out target) && index.TryGetValue(line.parent, out source))
                    sim[target].FuelLineSources.Add(source);
            }
            return sim;
        }

        // The part's rocket mode and air-breathing mode: a multi-mode engine's two
        // (the air-breathing one that goes furthest), else the first engine, as KSP
        // counts it; not a motor KSP leaves out.
        private static void EnginesOf(Part part, HashSet<string> air, SimPart simPart)
        {
            List<ModuleEngines> engines = part.FindModulesImplementing<ModuleEngines>();
            bool multi = part.HasModuleImplementing<MultiModeEngine>();
            for (int e = 0; e < engines.Count; e++)
            {
                ModuleEngines engine = engines[e];
                if (engine == null || (!multi && e > 0) || (engine.nonThrustMotor && !engine.includeinDVCalcs)) continue;
                bool breathes = engine.propellants != null && engine.propellants.Exists(p => p != null && air.Contains(p.name));
                SimEngine mode = ModeOf(engine, breathes, air);
                if (mode == null) continue;
                if (breathes)
                {
                    if (simPart.Air == null || mode.Mach > simPart.Air.Mach) simPart.Air = mode;
                }
                else if (simPart.Rocket == null)
                {
                    simPart.Rocket = mode;
                }
            }
        }

        private static SimEngine ModeOf(ModuleEngines engine, bool breathes, HashSet<string> air)
        {
            if (engine.atmosphereCurve == null || engine.propellants == null) return null;
            SimEngine mode = new SimEngine
            {
                Thrust = engine.maxThrust * engine.thrustPercentage * 0.01, Solid = engine.throttleLocked || !engine.allowShutdown,
                // A jet's Isp as it breathes the air near the ground; a rocket's in vacuum.
                Isp = engine.atmosphereCurve.Evaluate(breathes ? 1f : 0f),
            };
            foreach (Propellant propellant in engine.propellants)
            {
                if (propellant == null || propellant.resourceDef == null || propellant.resourceDef.density <= 0f || air.Contains(propellant.name)
                    || propellant.name == "ElectricCharge") continue;
                mode.Propellants.Add(new SimPropellant { Name = propellant.name, Ratio = propellant.ratio, Flow = FlowOf(propellant.GetFlowMode()) });
            }
            if (FlightGlobals.Bodies != null)
                foreach (CelestialBody body in FlightGlobals.Bodies)
                {
                    if (body == null || !body.atmosphere || (breathes && !body.atmosphereContainsOxygen)) continue;
                    mode.ThrustAtSurface[body.bodyName] = engine.MaxThrustOutputAtm(false, true, (float)body.atmPressureASL,
                        body.atmosphereTemperatureSeaLevel, body.atmDensityASL);
                }
            if (breathes)
                mode.Mach = engine.useVelCurve && engine.velCurve != null
                    ? AirLaunch.MachLimit(mach => engine.velCurve.Evaluate((float)mach))
                    : 1.0;
            return mode.Thrust > 0.0 && mode.Isp > 0.0 && mode.Propellants.Count > 0 ? mode : null;
        }

        private static SimFlow FlowOf(ResourceFlowMode mode)
        {
            switch (mode)
            {
                case ResourceFlowMode.NO_FLOW:
                    return SimFlow.Own;
                case ResourceFlowMode.STACK_PRIORITY_SEARCH:
                case ResourceFlowMode.STAGE_STACK_FLOW:
                case ResourceFlowMode.STAGE_STACK_FLOW_BALANCE:
                    return SimFlow.Stack;
                default:
                    return SimFlow.Vessel;
            }
        }

        // An engine KSP's delta-v simulation counts: not a motor that gives no thrust
        // of its own (a tank's separation motor, an escape tower), unless the player
        // includes it (ModuleEngines.nonThrustMotor, includeinDVCalcs; VesselDeltaV,
        // decompiled).
        internal static bool HasThrust(Part part)
        {
            foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
                if (engine != null && (!engine.nonThrustMotor || engine.includeinDVCalcs)) return true;
            return false;
        }

        // Per propellant, its share by mass of the flow of thrusters whose thrust over
        // Isp is `weight` (so proportional to their mass flow): its ratio times its
        // density over the mix's. Resources of no mass (electric charge) take none.
        internal static void AddMix(Dictionary<string, Dictionary<string, double>> mixes, List<Propellant> propellants, double weight)
        {
            if (propellants == null || weight <= 0.0) return;
            List<string> names = new List<string>();
            foreach (Propellant propellant in propellants)
                if (propellant != null && propellant.resourceDef != null && propellant.resourceDef.density > 0f) names.Add(propellant.name);
            names.Sort(string.CompareOrdinal);
            string key = string.Join("+", names.ToArray());
            Dictionary<string, double> mix;
            if (!mixes.TryGetValue(key, out mix)) mixes[key] = mix = new Dictionary<string, double>();
            double total = 0.0;
            foreach (Propellant propellant in propellants)
                if (propellant != null && propellant.resourceDef != null) total += propellant.ratio * propellant.resourceDef.density;
            if (total <= 0.0) return;
            foreach (Propellant propellant in propellants)
            {
                if (propellant == null || propellant.resourceDef == null || propellant.resourceDef.density <= 0f) continue;
                double share;
                mix.TryGetValue(propellant.name, out share);
                mix[propellant.name] = share + weight * propellant.ratio * propellant.resourceDef.density / total;
            }
        }

        private static StageRow RowOf(DeltaVStageInfo info)
        {
            // The figures for the situation chosen, as KSP's staging stack picks them:
            // at sea level, at the app's altitude ("Actual"), or in vacuum
            // (DeltaVStageInfo.GetSituation*; decompiled).
            DeltaVSituationOptions situation = Situation();
            StageRow row = new StageRow
            {
                Number = info.stage,
                DeltaV = info.GetSituationDeltaV(situation),
                Thrust = info.GetSituationThrust(situation),
                BurnTime = info.stageBurnTime,
                StartMass = info.startMass,
                EndMass = info.endMass,
                DeltaVVacuum = info.deltaVinVac,
                ThrustVacuum = info.thrustVac,
            };
            SurfaceThrust(row, info);
            row.Solid = Solid(info);
            row.ElectricRate = ElectricRate(info);
            return row;
        }

        // The electric charge the stage's engines burn at full thrust, in units per
        // second: each engine's mass flow (thrust over Isp) shared out over its
        // propellants by ratio and density, which gives the charge, of no mass, its
        // units by ratio.
        private static double ElectricRate(DeltaVStageInfo info)
        {
            if (info.enginesActiveInStage == null) return 0.0;
            double rate = 0.0;
            foreach (DeltaVEngineInfo engineInfo in info.enginesActiveInStage)
            {
                ModuleEngines engine = engineInfo != null ? engineInfo.engine : null;
                if (engine == null || engine.propellants == null || engine.atmosphereCurve == null) continue;
                double isp = engine.atmosphereCurve.Evaluate(0f);
                double total = 0.0, charge = 0.0;
                foreach (Propellant propellant in engine.propellants)
                {
                    if (propellant == null) continue;
                    if (propellant.resourceDef != null) total += propellant.ratio * propellant.resourceDef.density;
                    if (propellant.name == "ElectricCharge") charge += propellant.ratio;
                }
                if (charge <= 0.0 || total <= 0.0 || isp <= 0.0) continue;
                double flow = engine.maxThrust * engine.thrustPercentage * 0.01 / (isp * StageTable.StandardGravity);
                rate += flow * charge / total;
            }
            return rate;
        }

        // Every engine of the stage burns out once lit: none can be shut down or
        // throttled.
        private static bool Solid(DeltaVStageInfo info)
        {
            if (info.enginesActiveInStage == null || info.enginesActiveInStage.Count == 0) return false;
            foreach (DeltaVEngineInfo engineInfo in info.enginesActiveInStage)
            {
                ModuleEngines engine = engineInfo != null ? engineInfo.engine : null;
                if (engine != null && !engine.throttleLocked && engine.allowShutdown) return false;
            }
            return true;
        }

        // The situation KSP's delta-v app holds; vacuum where its body has no air.
        private static DeltaVSituationOptions Situation()
        {
            DeltaVAppValues values = DeltaVGlobals.DeltaVAppValues;
            CelestialBody body = values != null && values.body != null ? values.body : FlightGlobals.GetHomeBody();
            if (values == null || body == null || !body.atmosphere) return DeltaVSituationOptions.Vaccum;
            return values.situation;
        }

        // The stage's thrust at the surface of each body with air, as KSP gives it at
        // the sea level of its delta-v app's body (DeltaVEngineInfo.CalculateThrustVector:
        // ModuleEngines.MaxThrustOutputAtm at the body's atmPressureASL,
        // atmosphereTemperatureSeaLevel and atmDensityASL; decompiled).
        private static void SurfaceThrust(StageRow row, DeltaVStageInfo info)
        {
            if (info.enginesActiveInStage == null || FlightGlobals.Bodies == null) return;
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body == null || !body.atmosphere) continue;
                double thrust = 0.0;
                foreach (DeltaVEngineInfo engineInfo in info.enginesActiveInStage)
                {
                    ModuleEngines engine = engineInfo != null ? engineInfo.engine : null;
                    if (engine == null) continue;
                    thrust += engine.MaxThrustOutputAtm(false, true, (float)body.atmPressureASL, body.atmosphereTemperatureSeaLevel,
                        body.atmDensityASL);
                }
                row.ThrustAtSurface[body.bodyName] = thrust;
            }
        }
    }
}
