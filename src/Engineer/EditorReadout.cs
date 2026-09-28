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

        public double TotalDeltaV;
        public double WetMass;
        public double DryMass;
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
            readout.Resources = ResourcesOf(ship.parts);

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
                    Engine = part.HasModuleImplementing<ModuleEngines>(),
                    Legs = legs,
                    // RealChute's parachutes are a module of their own.
                    Parachute = part.HasModuleImplementing<ModuleParachute>() || part.Modules.Contains("RealChuteModule"),
                    HeatShield = part.HasModuleImplementing<ModuleAblator>(),
                    Separator = part.HasModuleImplementing<ModuleDockingNode>() ? Separation.DockingPort
                        : part.HasModuleImplementing<ModuleAnchoredDecoupler>() ? Separation.Anchored
                        : part.HasModuleImplementing<ModuleDecouple>() ? Separation.Decoupler : Separation.None,
                    Id = part.persistentId,
                    Title = part.partInfo != null ? part.partInfo.title : part.name,
                    DryMass = dry,
                    Mass = dry + part.GetResourceMass(),
                    Tag = part,
                };
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

        private static StageRow RowOf(DeltaVStageInfo info, List<DeltaVPartInfo> partInfos, bool vectoredThrust)
        {
            StageRow row = new StageRow
            {
                Number = info.stage,
                HasEngines = info.enginesActiveInStage != null && info.enginesActiveInStage.Count > 0,
                DeltaV = info.deltaVActual,
                Isp = info.ispActual,
                Thrust = vectoredThrust ? info.vectoredThrustActual : info.thrustActual,
                Twr = info.TWRActual,
                BurnTime = info.stageBurnTime,
                StartMass = info.startMass,
                EndMass = info.endMass,
                DeltaVVacuum = info.deltaVinVac,
                ThrustVacuum = vectoredThrust ? info.vectoredThrustVac : info.thrustVac,
                Engines = EnginesOf(info),
            };
            // KSP's TWR is of the full thrust; with the vectored thrust the ratio
            // follows it.
            if (vectoredThrust && info.thrustActual > 0f) row.Twr = info.TWRActual * info.vectoredThrustActual / info.thrustActual;

            // A part is attached while a stage burns where KSP decouples it at
            // that stage or later (DeltaVStageInfo.ProcessParts: decoupleStage
            // <= the stage), and dropped with it where it decouples at it.
            if (partInfos != null)
            {
                foreach (DeltaVPartInfo partInfo in partInfos)
                {
                    if (partInfo == null || partInfo.part == null || partInfo.decoupleStage > info.stage) continue;
                    double cost = PartCost(partInfo.part);
                    row.AttachedParts++;
                    row.AttachedCost += cost;
                    if (partInfo.decoupleStage != info.stage) continue;
                    row.OwnParts++;
                    row.OwnCost += cost;
                }
            }
            row.ThrustTorque = ThrustTorque(info, partInfos);
            return row;
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
            HashSet<string> propellants = new HashSet<string>();
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
                    thrust += rcs.thrusterPower;
                    ispWeighted += rcs.thrusterPower / Math.Max(isp, 1e-6);
                    if (rcs.propellants == null) continue;
                    foreach (Propellant propellant in rcs.propellants) propellants.Add(propellant.name);
                }
            }
            if (thrust <= 0.0) return null;

            double propellantMass = 0.0;
            foreach (Part part in attached)
            {
                for (int i = 0; i < part.Resources.Count; i++)
                {
                    PartResource resource = part.Resources[i];
                    if (propellants.Contains(resource.resourceName)) propellantMass += resource.amount * resource.info.density;
                }
            }

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
