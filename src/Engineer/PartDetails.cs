using System.Collections.Generic;
using System.Text;

namespace ReDefinition.Engineer
{
    // The details of one part in the editor, for the part under the mouse:
    // what the parts list's tooltip shows of a part not yet placed, for a part on
    // the vessel -- with its tanks as they are filled.
    internal static class PartDetails
    {
        internal sealed class Line
        {
            public string Name;
            public string Value;
            // Indented under the line before.
            public bool Detail;
        }

        internal static List<Line> Of(Part part)
        {
            List<Line> lines = new List<Line>();
            if (part == null || part.partInfo == null) return lines;

            double dryMass = part.mass;
            double wetMass = dryMass + part.GetResourceMass();
            Add(lines, "Mass", Pair(EngineerFormat.Mass(dryMass), EngineerFormat.Mass(wetMass)));
            double wetCost = EditorReadout.PartCost(part);
            double dryCost = wetCost;
            for (int i = 0; i < part.Resources.Count; i++)
                dryCost -= part.Resources[i].info.unitCost * part.Resources[i].amount;
            Add(lines, "Cost", Pair(EngineerFormat.Cost(dryCost), EngineerFormat.Cost(wetCost)));

            foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
            {
                if (engine == null) continue;
                string name = string.IsNullOrEmpty(engine.engineID) || engine.engineID == "basicEngine"
                    ? "Engine"
                    : "Engine (" + engine.engineID + ")";
                Add(lines, name, EngineerFormat.Force(engine.maxThrust));
                if (engine.atmosphereCurve != null)
                    Add(lines, "Isp, sea level / vacuum",
                        Pair(EngineerFormat.Isp(engine.atmosphereCurve.Evaluate(1f)),
                            EngineerFormat.Isp(engine.atmosphereCurve.Evaluate(0f))), true);
                if (engine.propellants != null && engine.propellants.Count > 0)
                    Add(lines, "Burns", Propellants(engine.propellants), true);
            }

            ModuleGimbal gimbal = part.FindModuleImplementing<ModuleGimbal>();
            if (gimbal != null) Add(lines, "Gimbal", gimbal.gimbalRange.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "°");

            ModuleDecouple decoupler = part.FindModuleImplementing<ModuleDecouple>();
            if (decoupler != null) Add(lines, "Ejection force", EngineerFormat.Force(decoupler.ejectionForce));
            ModuleAnchoredDecoupler anchored = part.FindModuleImplementing<ModuleAnchoredDecoupler>();
            if (anchored != null) Add(lines, "Ejection force", EngineerFormat.Force(anchored.ejectionForce));

            ModuleReactionWheel wheel = part.FindModuleImplementing<ModuleReactionWheel>();
            if (wheel != null)
                Add(lines, "Reaction wheel, pitch / yaw / roll",
                    EngineerFormat.Torque(wheel.PitchTorque) + " / " + EngineerFormat.Torque(wheel.YawTorque) + " / "
                    + EngineerFormat.Torque(wheel.RollTorque));

            foreach (ModuleRCS rcs in part.FindModulesImplementing<ModuleRCS>())
            {
                if (rcs == null) continue;
                Add(lines, "RCS thruster", EngineerFormat.Force(rcs.thrusterPower));
                if (rcs.atmosphereCurve != null)
                    Add(lines, "Isp, sea level / vacuum",
                        Pair(EngineerFormat.Isp(rcs.atmosphereCurve.Evaluate(1f)), EngineerFormat.Isp(rcs.atmosphereCurve.Evaluate(0f))),
                        true);
            }

            ModuleParachute chute = part.FindModuleImplementing<ModuleParachute>();
            if (chute != null)
            {
                Add(lines, "Parachute, deploys at", EngineerFormat.Distance(chute.deployAltitude));
                Add(lines, "Opens from pressure",
                    chute.minAirPressureToOpen.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " atm", true);
            }

            ModuleDeployableSolarPanel solar = part.FindModuleImplementing<ModuleDeployableSolarPanel>();
            if (solar != null) Add(lines, "Solar panel", Rate(solar.chargeRate, "EC"));

            foreach (ModuleGenerator generator in part.FindModulesImplementing<ModuleGenerator>())
            {
                if (generator == null || generator.resHandler == null) continue;
                foreach (ModuleResource output in generator.resHandler.outputResources)
                    Add(lines, "Generates", Rate(output.rate, output.name));
            }

            ModuleDataTransmitter antenna = part.FindModuleImplementing<ModuleDataTransmitter>();
            if (antenna != null)
                Add(lines, "Antenna power", KSPUtil.PrintSI(antenna.antennaPower, "", 3));

            ModuleScienceExperiment experiment = part.FindModuleImplementing<ModuleScienceExperiment>();
            if (experiment != null && !string.IsNullOrEmpty(experiment.experimentActionName))
                Add(lines, "Experiment", experiment.experimentActionName);

            if (part.Resources.Count > 0)
            {
                Add(lines, "Resources", "");
                for (int i = 0; i < part.Resources.Count; i++)
                {
                    PartResource resource = part.Resources[i];
                    string name = resource.info.displayName ?? resource.resourceName;
                    string value = EngineerFormat.Amount(resource.amount) + " / " + EngineerFormat.Amount(resource.maxAmount);
                    if (resource.info.density > 0f) value += " (" + EngineerFormat.Mass(resource.amount * resource.info.density) + ")";
                    Add(lines, name, value, true);
                }
            }
            return lines;
        }

        private static void Add(List<Line> lines, string name, string value, bool detail = false)
        {
            lines.Add(new Line { Name = name, Value = value, Detail = detail });
        }

        private static string Pair(string first, string second)
        {
            return first == second ? first : first + " / " + second;
        }

        private static string Rate(double perSecond, string resource)
        {
            return perSecond.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " " + resource + "/s";
        }

        private static string Propellants(List<Propellant> propellants)
        {
            float total = 0f;
            foreach (Propellant propellant in propellants) total += propellant.ratio;
            StringBuilder text = new StringBuilder();
            foreach (Propellant propellant in propellants)
            {
                if (text.Length > 0) text.Append(", ");
                string name = propellant.displayName ?? propellant.name;
                text.Append(name);
                if (propellants.Count > 1 && total > 0f)
                    text.Append(' ').Append((propellant.ratio / total * 100f).ToString("0", System.Globalization.CultureInfo.InvariantCulture))
                        .Append('%');
            }
            return text.ToString();
        }
    }
}
