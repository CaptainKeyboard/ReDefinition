using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Dashboard
{
    // What the dashboard's resource tapes mark: where on each tape engines run dry, and whether an
    // engine lit now is empty. Where they run dry comes from the planner's simulation (PlannerHooks.DryMarks)
    // run on the vessel as it flies. Each time rocket engines run dry, the tape marks the vessel's total
    // of every propellant they burn then, in units, as r.resource counts it. Without the planner
    // nothing is marked, and the tapes show as without marks. Empty means an engine lit (ModuleEngines.EngineIgnited, which a
    // solid booster stays) that reaches none of a propellant it burns, air aside
    // (Propellant.totalResourceAvailable, which the engine updates every physics frame while lit, or
    // isDeprived as it flames out). It marks every propellant the engine burns.
    internal sealed class FuelTelemetry
    {
        // The simulation is run again after this many seconds.
        private const float DryInterval = 1f;
        // The vessel the run-dry marks were last simulated for, when (Time.unscaledTime), and the marks per propellant.
        private Vessel dryVessel;
        private float dryAt = float.MinValue;
        private readonly Dictionary<string, List<object>> dry = new Dictionary<string, List<object>>();

        // The vessel and frame the empty propellants were last found for, and those propellants.
        private Vessel emptyVessel;
        private int emptyFrame = -1;
        private readonly HashSet<string> empty = new HashSet<string>();
        // What Dry answers where no mark is known; never written to, so it can be shared.
        private static readonly List<object> NoMarks = new List<object>();

        // r.engineDry[name]: the totals of that propellant at which engines run dry, simulated again at most
        // every DryInterval; an empty list where none do.
        internal object Dry(Vessel v, string[] args)
        {
            if (args.Length == 0) return null;
            if (v != dryVessel || Time.unscaledTime - dryAt >= DryInterval)
            {
                dryVessel = v;
                dryAt = Time.unscaledTime;
                Simulate(v);
            }
            List<object> marks;
            return dry.TryGetValue(args[0], out marks) ? marks : NoMarks;
        }

        // r.engineEmpty[name]: whether an engine lit now that burns that propellant is empty; found once a frame.
        internal object Empty(Vessel v, string[] args)
        {
            if (args.Length == 0) return null;
            if (v != emptyVessel || Time.frameCount != emptyFrame)
            {
                emptyVessel = v;
                emptyFrame = Time.frameCount;
                FindEmpty(v);
            }
            return empty.Contains(args[0]);
        }

        // Fills `empty` with every propellant, air aside, of each lit engine that reaches none of one of them.
        private void FindEmpty(Vessel v)
        {
            empty.Clear();
            HashSet<string> air = AirResources(v.parts);
            foreach (Part part in v.parts)
            {
                if (part == null) continue;
                for (int m = 0; m < part.Modules.Count; m++)
                {
                    ModuleEngines engine = part.Modules[m] as ModuleEngines;
                    if (engine == null || !engine.EngineIgnited || engine.propellants == null) continue;
                    bool dryNow = false;
                    foreach (Propellant propellant in engine.propellants)
                        if (propellant != null && !air.Contains(propellant.name)
                            && (propellant.totalResourceAvailable <= 0.0 || (engine.flameout && propellant.isDeprived)))
                            dryNow = true;
                    if (!dryNow) continue;
                    foreach (Propellant propellant in engine.propellants)
                        if (propellant != null && !air.Contains(propellant.name)) empty.Add(propellant.name);
                }
            }
        }

        // Fills `dry` from the planner's simulation of the vessel as it flies; left empty without the planner,
        // or where KSP's delta-v simulation has no drop stage for every part.
        private void Simulate(Vessel v)
        {
            dry.Clear();
            DryMarksHook hook = PlannerHooks.DryMarks;
            Dictionary<string, List<object>> marks = hook != null ? hook.Marks(v) : null;
            if (marks == null) return;
            foreach (KeyValuePair<string, List<object>> mark in marks) dry[mark.Key] = mark.Value;
        }

        // The resources engines take in from the air: IntakeAir, as KSP's simulation knows it
        // (DeltaVGlobals.PropellantsToIgnore), and whatever the vessel's intakes gather (ModuleResourceIntake.resourceName).
        private static HashSet<string> AirResources(List<Part> parts)
        {
            HashSet<string> air = new HashSet<string> { "IntakeAir" };
            foreach (Part part in parts)
            {
                if (part == null) continue;
                for (int m = 0; m < part.Modules.Count; m++)
                {
                    ModuleResourceIntake intake = part.Modules[m] as ModuleResourceIntake;
                    if (intake != null && !string.IsNullOrEmpty(intake.resourceName)) air.Add(intake.resourceName);
                }
            }
            return air;
        }
    }
}
