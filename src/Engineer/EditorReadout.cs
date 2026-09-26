using System.Collections.Generic;
using System;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // What the engineer view shows of the vessel in the editor, read from KSP's
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
        public List<ResourceLine> Resources = new List<ResourceLine>();
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
            }
            readout.Stages = StageTable.Complete(rows);
            readout.TotalDeltaV = StageTable.TotalDeltaV(readout.Stages);
            readout.Rcs = RcsOf(readout.Stages, partInfos);
            return readout;
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
