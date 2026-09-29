using System.Collections.Generic;
using System;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // What the engineer window and the planner show of the vessel in the editor, read from KSP's
    // own delta-v simulation (ShipConstruct.vesselDeltaV, VesselDeltaV,
    // DeltaVStageInfo; decompiled) for the situation its delta-v app holds
    // (DeltaVGlobals.DeltaVAppValues: body, sea level, altitude or vacuum). KSP
    // shows the same numbers in its staging stack and its delta-v app, so the
    // two never disagree. Worked out here beside it: the parts and costs of
    // each stage, the torque of its engines' thrust, RCS, the resources, and the
    // part under the mouse.
    internal sealed class EditorReadout
    {
        internal sealed class ResourceLine
        {
            public string Name;
            public double Amount;
            public double MaxAmount;
            public double Mass;
        }

        internal sealed class RcsLine
        {
            public double DeltaV;
            public double Isp;
            public double Thrust;
            public double Twr;
            public double BurnTime;
        }

        public List<StageRow> Stages = new List<StageRow>();
        // By stage number: the engines that burn in it and the parts whose
        // propellant it burns (PartsBurning).
        public Dictionary<int, List<Part>> BurningParts = new Dictionary<int, List<Part>>();
        public List<ResourceLine> Resources = new List<ResourceLine>();
        // Every part as the planner finds the lander and the capsule by it
        // (VesselShape).
        public List<PartFacts> PartFacts = new List<PartFacts>();
        public RcsLine Rcs;
        // The parts as the planner's own simulation takes them (StageSim), and per
        // resource its density in tonnes per unit; null where no engine breathes air
        // and KSP's stages are the planner's.
        public List<SimPart> Sim;
        public Dictionary<string, double> Density;

        public double TotalDeltaV;
        public double WetMass;
        public double DryMass;
        // The resources in it, without crew (ShipConstruct.GetShipMass).
        public double FuelMass;
        // The electric charge the vessel holds as it starts, in units.
        public double ElectricCharge;
        public double Cost;
        public int Parts;
        public bool HasShip;
        public bool Simulated;

        // Read again: the stock simulation runs by itself as the vessel changes.
        internal static EditorReadout Read(bool vectoredThrust)
        {
            EditorReadout readout = new EditorReadout();
            ShipConstruct ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
            if (ship == null || ship.parts == null || ship.parts.Count == 0) return readout;

            readout.HasShip = true;
            readout.Parts = ship.parts.Count;
            float dryCost, fuelCost, dryMass, fuelMass;
            // With the crew and their inventory, as KSP's cost widget and its mass
            // limit count them (CostWidget, EngineersReport; decompiled).
            VesselCrewManifest crew = ShipConstruction.ShipManifest;
            readout.Cost = ship.GetShipCosts(out dryCost, out fuelCost, crew);
            readout.WetMass = ship.GetShipMass(out dryMass, out fuelMass, crew);
            readout.DryMass = dryMass;
            readout.FuelMass = fuelMass;
            readout.Resources = ResourcesOf(ship.parts);
            foreach (Part part in ship.parts)
                if (part != null && part.Resources.Contains("ElectricCharge")) readout.ElectricCharge += part.Resources["ElectricCharge"].amount;

            VesselDeltaV deltaV = ship.vesselDeltaV;
            List<DeltaVStageInfo> infos = deltaV != null ? deltaV.OperatingStageInfo : null;
            if (infos == null || infos.Count == 0) return readout;

            readout.Simulated = true;
            List<DeltaVPartInfo> partInfos = deltaV.PartInfo;
            List<StageRow> rows = new List<StageRow>();
            foreach (DeltaVStageInfo info in infos)
            {
                if (info == null) continue;
                rows.Add(RowOf(info, partInfos, vectoredThrust));
                readout.BurningParts[info.stage] = PartsBurning(info, partInfos);
            }
            readout.Stages = StageTable.Complete(rows);
            readout.PartFacts = FactsOf(ship.parts, partInfos);
            HashSet<string> air = AirResources(ship.parts);
            if (BreathesAir(ship.parts, air))
            {
                readout.Density = new Dictionary<string, double>();
                readout.Sim = SimOf(ship.parts, readout.PartFacts, air, readout.Density, TopStart(readout.Stages));
            }
            readout.TotalDeltaV = StageTable.TotalDeltaV(readout.Stages);
            readout.Rcs = RcsOf(readout.Stages, partInfos);
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

        private static StageRow RowOf(DeltaVStageInfo info, List<DeltaVPartInfo> partInfos, bool vectoredThrust)
        {
            // The figures for the situation chosen, as KSP's staging stack picks them:
            // at sea level, at the app's altitude ("Actual"), or in vacuum
            // (DeltaVStageInfo.GetSituation*; decompiled).
            DeltaVSituationOptions situation = EngineerSituation.Situation();
            float thrust = info.GetSituationThrust(situation);
            StageRow row = new StageRow
            {
                Number = info.stage,
                HasEngines = info.enginesActiveInStage != null && info.enginesActiveInStage.Count > 0,
                DeltaV = info.GetSituationDeltaV(situation),
                Isp = info.GetSituationISP(situation),
                Thrust = vectoredThrust ? info.GetSituationVectoredThrust(situation) : thrust,
                Twr = info.GetSituationTWR(situation),
                BurnTime = info.stageBurnTime,
                StartMass = info.startMass,
                EndMass = info.endMass,
                DeltaVVacuum = info.deltaVinVac,
                ThrustVacuum = vectoredThrust ? info.vectoredThrustVac : info.thrustVac,
                Engines = EnginesOf(info),
            };
            // KSP's TWR is of the full thrust; with the vectored thrust the ratio
            // follows it.
            if (vectoredThrust && thrust > 0f) row.Twr = row.Twr * info.GetSituationVectoredThrust(situation) / thrust;

            // A part is attached while a stage burns where KSP decouples it with a
            // later stage (DeltaVPartInfo.decoupleStage below the stage's number: a
            // decoupler fires as its stage starts), and belongs to the stage where
            // the next stage drops it.
            if (partInfos != null)
            {
                foreach (DeltaVPartInfo partInfo in partInfos)
                {
                    if (partInfo == null || partInfo.part == null || partInfo.decoupleStage >= info.stage) continue;
                    double cost = PartCost(partInfo.part);
                    row.AttachedParts++;
                    row.AttachedCost += cost;
                    if (partInfo.decoupleStage != info.stage - 1) continue;
                    row.OwnParts++;
                    row.OwnCost += cost;
                }
            }
            row.ThrustTorque = ThrustTorque(info, partInfos);
            SurfaceThrust(row, info, vectoredThrust);
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

        // The stage's thrust at the surface of each body with air, as KSP gives it at
        // the sea level of its delta-v app's body (DeltaVEngineInfo.CalculateThrustVector:
        // ModuleEngines.MaxThrustOutputAtm at the body's atmPressureASL,
        // atmosphereTemperatureSeaLevel and atmDensityASL; decompiled). The vectored
        // thrust in the same share of the full thrust as in vacuum.
        private static void SurfaceThrust(StageRow row, DeltaVStageInfo info, bool vectoredThrust)
        {
            if (info.enginesActiveInStage == null || FlightGlobals.Bodies == null) return;
            double share = vectoredThrust && info.thrustVac > 0f ? info.vectoredThrustVac / info.thrustVac : 1.0;
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
                row.ThrustAtSurface[body.bodyName] = thrust * share;
            }
        }

        // The engines active in the stage, and every part that has less
        // propellant at the stage's end than at its start: KSP's simulation keeps
        // that per part and stage (DeltaVPartInfo.stageFuelMass, decompiled).
        private static List<Part> PartsBurning(DeltaVStageInfo info, List<DeltaVPartInfo> partInfos)
        {
            List<Part> parts = new List<Part>();
            if (info.enginesActiveInStage != null)
                foreach (DeltaVEngineInfo engine in info.enginesActiveInStage)
                    if (engine != null && engine.engine != null && engine.engine.part != null && !parts.Contains(engine.engine.part))
                        parts.Add(engine.engine.part);
            if (partInfos == null) return parts;
            foreach (DeltaVPartInfo partInfo in partInfos)
            {
                if (partInfo == null || partInfo.part == null || partInfo.stageFuelMass == null) continue;
                DeltaVPartInfo.PartStageFuelMass fuel;
                if (!partInfo.stageFuelMass.TryGetValue(info.stage, out fuel) || fuel == null) continue;
                if (fuel.startMass - fuel.endMass > 1e-4f && !parts.Contains(partInfo.part)) parts.Add(partInfo.part);
            }
            return parts;
        }

        // The engines burning in a stage by title, counted: "2 x LV-T45, 1 x Spark".
        private static string EnginesOf(DeltaVStageInfo info)
        {
            if (info.enginesActiveInStage == null || info.enginesActiveInStage.Count == 0) return "";
            Dictionary<string, int> counts = new Dictionary<string, int>();
            List<string> order = new List<string>();
            foreach (DeltaVEngineInfo engine in info.enginesActiveInStage)
            {
                Part part = engine != null && engine.engine != null ? engine.engine.part : null;
                string title = part != null && part.partInfo != null ? part.partInfo.title : "engine";
                int count;
                if (!counts.TryGetValue(title, out count)) order.Add(title);
                counts[title] = count + 1;
            }
            List<string> text = new List<string>();
            foreach (string title in order) text.Add(counts[title] + " x " + title);
            return string.Join(", ", text.ToArray());
        }

        // As ShipConstruct.GetShipCosts does it for the whole vessel: the part's
        // cost with its modules', its resources as they are filled.
        internal static double PartCost(Part part)
        {
            AvailablePart available = part.partInfo;
            if (available == null) return 0.0;
            double cost = available.cost + part.GetModuleCosts(available.cost);
            for (int i = 0; i < part.Resources.Count; i++)
            {
                PartResource resource = part.Resources[i];
                cost -= resource.info.unitCost * resource.maxAmount;
                cost += resource.info.unitCost * resource.amount;
            }
            return cost;
        }

        // The turning moment of the stage's engines about the centre of mass of
        // what is attached at its start, at full thrust: zero for a vessel whose
        // thrust runs through its centre of mass. KSP's exhaust leaves along a
        // thrust transform's forward axis, and the thrust pushes the other way.
        private static double ThrustTorque(DeltaVStageInfo info, List<DeltaVPartInfo> partInfos)
        {
            if (info.enginesActiveInStage == null || info.enginesActiveInStage.Count == 0 || partInfos == null) return 0.0;

            Vector3 weighted = Vector3.zero;
            double mass = 0.0;
            foreach (DeltaVPartInfo partInfo in partInfos)
            {
                if (partInfo == null || partInfo.part == null || partInfo.decoupleStage > info.stage) continue;
                Part part = partInfo.part;
                double partMass = part.mass + part.GetResourceMass();
                weighted += part.transform.position * (float)partMass;
                mass += partMass;
            }
            if (mass <= 0.0) return 0.0;
            Vector3 centre = weighted / (float)mass;

            Vector3 torque = Vector3.zero;
            foreach (DeltaVEngineInfo engineInfo in info.enginesActiveInStage)
            {
                ModuleEngines engine = engineInfo != null ? engineInfo.engine : null;
                if (engine == null || engine.thrustTransforms == null || engine.thrustTransforms.Count == 0) continue;
                float thrust = engineInfo.thrustActual;
                for (int i = 0; i < engine.thrustTransforms.Count; i++)
                {
                    Transform nozzle = engine.thrustTransforms[i];
                    if (nozzle == null) continue;
                    float share = engine.thrustTransformMultipliers != null && i < engine.thrustTransformMultipliers.Count
                        ? engine.thrustTransformMultipliers[i]
                        : 1f / engine.thrustTransforms.Count;
                    Vector3 force = -nozzle.forward * thrust * share;
                    torque += Vector3.Cross(nozzle.position - centre, force);
                }
            }
            return torque.magnitude;
        }

        // Of what is left at the last stage: the vessel's RCS thrusters and the
        // propellant they burn. Null where it has none.
        private static RcsLine RcsOf(List<StageRow> stages, List<DeltaVPartInfo> partInfos)
        {
            if (stages.Count == 0 || partInfos == null) return null;
            StageRow last = stages[0];

            double thrust = 0.0;
            double ispWeighted = 0.0;
            Dictionary<string, Dictionary<string, double>> mix = new Dictionary<string, Dictionary<string, double>>();
            List<Part> attached = new List<Part>();
            foreach (DeltaVPartInfo partInfo in partInfos)
            {
                if (partInfo == null || partInfo.part == null || partInfo.decoupleStage > last.Number) continue;
                attached.Add(partInfo.part);
                List<ModuleRCS> modules = partInfo.part.FindModulesImplementing<ModuleRCS>();
                foreach (ModuleRCS rcs in modules)
                {
                    if (rcs == null || !rcs.moduleIsEnabled) continue;
                    double isp = rcs.atmosphereCurve != null ? rcs.atmosphereCurve.Evaluate(0f) : 0.0;
                    if (isp <= 0.0) continue;
                    // As ModuleRCS applies it per nozzle (decompiled).
                    double power = rcs.thrusterPower * rcs.thrustPercentage * 0.01;
                    thrust += power;
                    ispWeighted += power / isp;
                    AddMix(mix, rcs.propellants, power / isp);
                }
            }
            if (thrust <= 0.0 || ispWeighted <= 0.0) return null;

            // What the thrusters can burn in their mix, of what the parts still
            // attached hold; not a locked tank's.
            Dictionary<string, double> held = new Dictionary<string, double>();
            foreach (Part part in attached)
            {
                for (int i = 0; i < part.Resources.Count; i++)
                {
                    PartResource resource = part.Resources[i];
                    if (resource == null || resource.info == null || !resource.flowState) continue;
                    double amount;
                    held.TryGetValue(resource.resourceName, out amount);
                    held[resource.resourceName] = amount + resource.amount * resource.info.density;
                }
            }
            double propellantMass = StageTable.UsablePropellant(mix, held);

            // Thrusters of different impulse burn together: the impulse of the
            // whole is the thrust over the mass flow.
            double combinedIsp = thrust / ispWeighted;
            double startMass = last.StartMass > 0.0 ? last.StartMass : 0.0;
            RcsLine line = new RcsLine { Isp = combinedIsp, Thrust = thrust };
            if (startMass > propellantMass && propellantMass > 0.0)
            {
                line.DeltaV = StageTable.RocketDeltaV(combinedIsp, startMass, startMass - propellantMass);
                double flow = thrust / (combinedIsp * StageTable.StandardGravity);
                line.BurnTime = flow > 0.0 ? propellantMass / flow : 0.0;
            }
            double gravity = SelectedGravity();
            line.Twr = startMass > 0.0 && gravity > 0.0 ? thrust / (startMass * gravity) : 0.0;
            return line;
        }

        // The surface gravity of the body KSP's delta-v app is set to, in m/s².
        internal static double SelectedGravity()
        {
            CelestialBody body = DeltaVGlobals.DeltaVAppValues != null ? DeltaVGlobals.DeltaVAppValues.body : null;
            if (body == null) body = FlightGlobals.GetHomeBody();
            return body != null ? body.GeeASL * StageTable.StandardGravity : StageTable.StandardGravity;
        }

        private static List<ResourceLine> ResourcesOf(List<Part> parts)
        {
            Dictionary<string, ResourceLine> byName = new Dictionary<string, ResourceLine>();
            List<ResourceLine> lines = new List<ResourceLine>();
            foreach (Part part in parts)
            {
                for (int i = 0; i < part.Resources.Count; i++)
                {
                    PartResource resource = part.Resources[i];
                    ResourceLine line;
                    if (!byName.TryGetValue(resource.resourceName, out line))
                    {
                        line = new ResourceLine { Name = resource.info.displayName ?? resource.resourceName };
                        byName[resource.resourceName] = line;
                        lines.Add(line);
                    }
                    line.Amount += resource.amount;
                    line.MaxAmount += resource.maxAmount;
                    line.Mass += resource.amount * resource.info.density;
                }
            }
            lines.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return lines;
        }
    }
}
