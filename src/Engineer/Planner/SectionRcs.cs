using System;
using System.Collections.Generic;

namespace ReDefinition.Engineer
{
    // The RCS of a group of parts as a stage starts or after it burnt (-1 for none):
    // the thrusters' thrust in kN and their thrust over Isp, the propellant for
    // them, and the delta-v that gives `mass`, in m/s. Of the group's parts only
    // those still attached count (PartFacts.AttachedIn), and only the resources
    // their own RCS thrusters burn, in the mix they burn them in
    // (StageTable.UsablePropellant), no more than what `mass` still holds over those
    // parts' dry mass. Free of Unity, tested.
    internal static class SectionRcs
    {
        internal static void Of(IList<PartFacts> parts, IEnumerable<int> members, int stage, double mass, out double thrust,
                                out double overIsp, out double propellant, out double deltaV)
        {
            thrust = 0.0;
            overIsp = 0.0;
            propellant = 0.0;
            deltaV = 0.0;
            if (members == null) return;
            double dry = 0.0;
            Dictionary<string, Dictionary<string, double>> mix = new Dictionary<string, Dictionary<string, double>>();
            Dictionary<string, double> held = new Dictionary<string, double>();
            List<int> attached = new List<int>();
            foreach (int at in members)
            {
                if (at < 0 || at >= parts.Count || parts[at].Clamp) continue;
                if (!parts[at].AttachedIn(stage)) continue;
                attached.Add(at);
                thrust += parts[at].RcsThrust;
                overIsp += parts[at].RcsThrustOverIsp;
                foreach (KeyValuePair<string, Dictionary<string, double>> kind in parts[at].RcsMix)
                {
                    Dictionary<string, double> into;
                    if (!mix.TryGetValue(kind.Key, out into)) mix[kind.Key] = into = new Dictionary<string, double>();
                    foreach (KeyValuePair<string, double> share in kind.Value)
                    {
                        double sum;
                        into.TryGetValue(share.Key, out sum);
                        into[share.Key] = sum + share.Value;
                    }
                }
                dry += parts[at].DryMass;
            }
            foreach (int at in attached)
                foreach (KeyValuePair<string, double> resource in parts[at].RcsResources)
                {
                    double sum;
                    held.TryGetValue(resource.Key, out sum);
                    held[resource.Key] = sum + resource.Value;
                }
            propellant = Math.Min(StageTable.UsablePropellant(mix, held), Math.Max(0.0, mass - dry));
            if (thrust <= 0.0 || overIsp <= 0.0 || propellant <= 0.0 || mass <= propellant) return;
            deltaV = thrust / overIsp * StageTable.StandardGravity * Math.Log(mass / (mass - propellant));
        }
    }
}
