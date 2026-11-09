using System.Collections.Generic;

namespace ReDefinition.Core
{
    // The propellant RCS thrusters burn: the mix each kind draws, the parts it is drawn from, and how much of what is held can be burnt.
    internal static class PropellantMix
    {
        // The parts a thruster on `part` draws a propellant of flow mode `mode` from,
        // as Part.GetConnectedResourceTotals takes them. They are its crossfeed set
        // `crossfeed` (in the editor the one built for its simulation, ShipConstruct:
        // PartSet.BuildPartSets with Simulate, into Part.simulationCrossfeedPartSet;
        // decompiled), the whole vessel `parts`, or the part itself. The flight's
        // readout takes them the same way (ReadoutFrame.FindRcsDeltaV).
        internal static void AddRcsSources(Part part, ResourceFlowMode mode, PartSet crossfeed, List<Part> parts, HashSet<Part> into)
        {
            switch (mode)
            {
                case ResourceFlowMode.STACK_PRIORITY_SEARCH:
                case ResourceFlowMode.STAGE_STACK_FLOW:
                case ResourceFlowMode.STAGE_STACK_FLOW_BALANCE:
                    if (crossfeed != null) into.UnionWith(crossfeed.GetParts());
                    else into.Add(part);
                    break;
                case ResourceFlowMode.ALL_VESSEL:
                case ResourceFlowMode.STAGE_PRIORITY_FLOW:
                case ResourceFlowMode.ALL_VESSEL_BALANCE:
                case ResourceFlowMode.STAGE_PRIORITY_FLOW_BALANCE:
                    foreach (Part other in parts)
                        if (other != null) into.Add(other);
                    break;
                default:
                    into.Add(part);
                    break;
            }
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

        // The propellant thrusters can burn from what is held, in tonnes. They draw
        // their propellants in the mix they burn them in (`mix`, per resource its
        // share of the flow by mass, in any unit). The resource that runs out
        // first ends it, and the rest of the others stays. A Vernor's liquid fuel and
        // oxidizer come from the tanks it reaches, as far as they go together.
        // `held`: per resource, in tonnes.
        internal static double UsablePropellant(IDictionary<string, double> mix, IDictionary<string, double> held)
        {
            double total = 0.0;
            foreach (double share in mix.Values) total += System.Math.Max(0.0, share);
            if (total <= 0.0) return 0.0;
            double usable = double.MaxValue;
            foreach (KeyValuePair<string, double> share in mix)
            {
                if (share.Value <= 0.0) continue;
                double amount;
                held.TryGetValue(share.Key, out amount);
                usable = System.Math.Min(usable, System.Math.Max(0.0, amount) * total / share.Value);
            }
            return usable == double.MaxValue ? 0.0 : usable;
        }

        // Thrusters of several mixes (monopropellant blocks and Vernors) each burn their
        // own: the sum of what each mix can burn of what is held, each resource
        // counted once, for the mix that burns the most of it.
        internal static double UsablePropellant(IDictionary<string, Dictionary<string, double>> mixes, IDictionary<string, double> held)
        {
            double total = 0.0;
            Dictionary<string, double> left = new Dictionary<string, double>(held);
            foreach (Dictionary<string, double> mix in mixes.Values)
            {
                double usable = UsablePropellant(mix, left);
                double sum = 0.0;
                foreach (double share in mix.Values) sum += System.Math.Max(0.0, share);
                if (usable <= 0.0 || sum <= 0.0) continue;
                total += usable;
                foreach (KeyValuePair<string, double> share in mix)
                {
                    double amount;
                    if (left.TryGetValue(share.Key, out amount)) left[share.Key] = System.Math.Max(0.0, amount - usable * share.Value / sum);
                }
            }
            return total;
        }
    }
}
