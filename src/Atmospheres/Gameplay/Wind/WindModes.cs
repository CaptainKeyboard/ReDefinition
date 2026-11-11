using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // A wind mod this package knows by the name of its assembly. AppliesWind: it pushes vehicles with a wind of its own (so Auto adds only
    // the storm delta). Infrastructure: FAR and WindAPI carry wind but are not a wind of their own. The assembly names are those the
    // mods ship; no copy of KWP, CPWE, Windy or Kerbal Wind is installed here, so each name is [VERIFY].
    internal sealed class ForeignWindMod
    {
        internal readonly string Name;
        internal readonly string[] Assemblies;
        internal readonly bool AppliesWind;
        internal readonly bool Infrastructure;

        internal ForeignWindMod(string name, bool appliesWind, bool infrastructure, params string[] assemblies)
        {
            Name = name;
            AppliesWind = appliesWind;
            Infrastructure = infrastructure;
            Assemblies = assemblies;
        }
    }

    // What the scan of the loaded assemblies found.
    internal sealed class ForeignWinds
    {
        internal static readonly ForeignWinds None = new ForeignWinds(new List<ForeignWindMod>());

        internal readonly List<ForeignWindMod> Found;

        internal ForeignWinds(List<ForeignWindMod> found)
        {
            Found = found;
        }

        // The first detected mod that applies a wind of its own, or null.
        internal string Owner
        {
            get
            {
                foreach (ForeignWindMod mod in Found) if (mod.AppliesWind) return mod.Name;
                return null;
            }
        }

        internal bool Has(string name)
        {
            foreach (ForeignWindMod mod in Found) if (mod.Name == name) return true;
            return false;
        }

        internal bool FarPresent { get { return Has("FAR"); } }

        internal bool WindApiPresent { get { return Has("WindAPI"); } }

        // The names, for the log.
        internal string Names()
        {
            if (Found.Count == 0) return "none";
            var parts = new string[Found.Count];
            for (int i = 0; i < parts.Length; i++) parts[i] = Found[i].Name;
            return string.Join(", ", parts);
        }
    }

    // Finds the wind mods by assembly name. Pure: the caller hands over the names of the loaded assemblies.
    internal static class ForeignWindDetector
    {
        internal static readonly ForeignWindMod[] Catalogue =
        {
            new ForeignWindMod("FAR", false, true, "FerramAerospaceResearch"),
            new ForeignWindMod("WindAPI", false, true, "WindAPI"),
            new ForeignWindMod("Kerbal Weather Project", true, false, "KerbalWeatherProject", "KWP"),
            new ForeignWindMod("CPWE", true, false, "CPWE", "ConfigurablePlanetaryWindEffects"),
            new ForeignWindMod("Windy", true, false, "Windy"),
            new ForeignWindMod("Kerbal Wind", true, false, "KerbalWind", "KerbalWindContinued"),
            // Advanced Atmosphere Tools changes the atmosphere's properties; whether it pushes vehicles is not known [VERIFY], so it is
            // reported and not counted as a wind of its own.
            new ForeignWindMod("Advanced Atmosphere Tools", false, false, "AdvancedAtmosphereTools", "AAT")
        };

        internal static ForeignWinds Detect(IEnumerable<string> loadedAssemblyNames)
        {
            var found = new List<ForeignWindMod>();
            if (loadedAssemblyNames == null) return new ForeignWinds(found);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in loadedAssemblyNames) if (!string.IsNullOrEmpty(n)) names.Add(n);
            foreach (ForeignWindMod mod in Catalogue)
            {
                foreach (string a in mod.Assemblies)
                {
                    if (names.Contains(a))
                    {
                        found.Add(mod);
                        break;
                    }
                }
            }
            return new ForeignWinds(found);
        }
    }

    // How the wind reaches the aerodynamics on this flight.
    internal enum WindRoute
    {
        // Nothing is applied.
        Off,
        // H1: the ModularFlightIntegrator override.
        Aero,
        // H3: a force on the parts.
        Force,
        // Through FAR's wind (WindAPI's provider).
        Far
    }

    // The decision of the single applier: which parts of the wind this mod applies, by which route, and what it says about it.
    internal sealed class WindPlan
    {
        internal bool ApplyBase, ApplyStorm;
        internal WindRoute Route;
        // The foreign mod that applies a wind of its own, or null.
        internal string ForeignOwner;
        internal WindSourceMode Mode;
        // The wind's text for the settings window (WindStatus) and the line of the log and the screen message.
        internal string Status = "";
        internal string InjectionStatus = "";
        // A reason the mode cannot do what it says (empty when none).
        internal string Warning = "";

        internal bool AppliesAnything { get { return Route != WindRoute.Off && (ApplyBase || ApplyStorm); } }

        // "Wind owner: <name>, mode: <H1/H3/FAR/off>".
        internal string OwnerLine()
        {
            string owner = ForeignOwner ?? "ReDefinition";
            if (!AppliesAnything) return "Wind owner: " + (ForeignOwner ?? "none") + ", mode: off";
            string mode = Route == WindRoute.Aero ? "H1" : Route == WindRoute.Force ? "H3" : "FAR";
            if (ForeignOwner != null && !ApplyBase) owner = ForeignOwner + " (base wind), ReDefinition (storm delta)";
            return "Wind owner: " + owner + ", mode: " + mode;
        }
    }

    // The mode table: windSource x foreign wind present or absent -> one applier. A foreign wind that is applied and ours added in full
    // would push twice, so Auto adds only the storm delta where a foreign mod applies a base wind, and Foreign and Off apply nothing.
    internal static class WindPlanner
    {
        internal static WindPlan Decide(WindSourceMode mode, ForeignWinds foreign, WindInjection injection, bool modularFlightIntegrator, bool windForces)
        {
            if (foreign == null) foreign = ForeignWinds.None;
            var plan = new WindPlan { ForeignOwner = foreign.Owner, Mode = mode };
            plan.Status = WindStatus.Text(mode, plan.ForeignOwner);
            if (!windForces || mode == WindSourceMode.Off || mode == WindSourceMode.Foreign)
            {
                plan.Route = WindRoute.Off;
                return plan;
            }

            if (foreign.FarPresent)
            {
                // FAR replaces the stock aerodynamics: the stock hook would do nothing. The only way in is WindAPI's provider list.
                if (foreign.WindApiPresent)
                {
                    plan.Route = WindRoute.Far;
                }
                else
                {
                    plan.Route = WindRoute.Off;
                    plan.Warning = "FAR is installed without WindAPI, so there is no way to add a wind to its aerodynamics; no wind forces.";
                    return plan;
                }
            }
            else
            {
                bool aero = injection == WindInjection.Aero || (injection == WindInjection.Auto && modularFlightIntegrator);
                plan.Route = aero && modularFlightIntegrator ? WindRoute.Aero : WindRoute.Force;
                plan.InjectionStatus = WindStatus.InjectionText(injection, modularFlightIntegrator, true);
            }

            if (mode == WindSourceMode.Auto)
            {
                plan.ApplyStorm = true;
                plan.ApplyBase = plan.ForeignOwner == null;
            }
            else
            {
                plan.ApplyStorm = true;
                plan.ApplyBase = true;
                if (plan.ForeignOwner != null)
                {
                    plan.Warning = plan.ForeignOwner + " applies a wind of its own and cannot be replaced: both winds act. Turn its wind off or use the Auto mode.";
                }
            }
            return plan;
        }
    }
}
