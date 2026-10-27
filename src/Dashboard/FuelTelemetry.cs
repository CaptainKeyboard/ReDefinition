using System;
using System.Collections.Generic;
using ReDefinition.Engineer;
using UnityEngine;

namespace ReDefinition.Dashboard
{
    // What the dashboard's resource tapes mark: where on each tape engines run dry, and whether an
    // engine lit now is empty. Where they run dry comes from the planner's own simulation (StageSim)
    // run on the vessel as it flies. The stages lit by now count as the current one, and an engine
    // switched off is left out. Each time rocket engines run dry, the tape marks the vessel's total
    // of every propellant they burn then, in units, as r.resource counts it. It needs KSP's delta-v
    // simulation for the stage each part is dropped in (VesselDeltaV.PartInfo, decoupleStage), and
    // without it nothing is marked. Empty means an engine lit (ModuleEngines.EngineIgnited, which a
    // solid booster stays) that reaches none of a propellant it burns, air aside
    // (Propellant.totalResourceAvailable, which the engine updates every physics frame while lit, or
    // isDeprived as it flames out; decompiled). It marks every propellant the engine burns.
    internal sealed class FuelTelemetry
    {
        // The simulation is run again after this many seconds.
        private const float DryInterval = 1f;
        // Less of a resource than this, in units, dropped with parts is none.
        private const double Thrown = 0.01;

        // The vessel the run-dry marks were last simulated for, when (Time.unscaledTime), and the marks per propellant.
        private Vessel dryVessel;
        private float dryAt = float.MinValue;
        private readonly Dictionary<string, List<object>> dry = new Dictionary<string, List<object>>();

        // The vessel and frame the empty propellants were last found for, and those propellants.
        private Vessel emptyVessel;
        private int emptyFrame = -1;
        private readonly HashSet<string> empty = new HashSet<string>();
        // The engines of the part being looked at, reused so that a frame allocates no list per part.
        private readonly List<ModuleEngines> engines = new List<ModuleEngines>();
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
            HashSet<string> air = EditorReadout.AirResources(v.parts);
            foreach (Part part in v.parts)
            {
                if (part == null) continue;
                foreach (ModuleEngines engine in PartModules.Of(part, engines))
                {
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

        // Whether any engine on the part is lit.
        private bool HasIgnitedEngine(Part part)
        {
            foreach (ModuleEngines engine in PartModules.Of(part, engines))
                if (engine != null && engine.EngineIgnited) return true;
            return false;
        }

        // Fills `dry` from the planner's simulation of the vessel as it flies (Marks); left empty where KSP's
        // delta-v simulation has no drop stage for every part.
        private void Simulate(Vessel v)
        {
            dry.Clear();
            List<DeltaVPartInfo> infos = v.VesselDeltaV != null ? v.VesselDeltaV.PartInfo : null;
            if (infos == null || infos.Count == 0) return;
            Dictionary<Part, int> decouple = new Dictionary<Part, int>();
            foreach (DeltaVPartInfo info in infos)
                if (info != null && info.part != null) decouple[info.part] = info.decoupleStage;

            List<Part> parts = new List<Part>();
            foreach (Part part in v.parts)
                if (part != null) parts.Add(part);
            Dictionary<Part, int> index = new Dictionary<Part, int>();
            for (int i = 0; i < parts.Count; i++) index[parts[i]] = i;
            List<PartFacts> facts = new List<PartFacts>();
            foreach (Part part in parts)
            {
                int parent, stage;
                if (!decouple.TryGetValue(part, out stage)) return;
                facts.Add(new PartFacts
                {
                    Parent = part.parent != null && index.TryGetValue(part.parent, out parent) ? parent : -1,
                    DecoupleStage = stage,
                    Clamp = part.HasModuleImplementing<LaunchClamp>(),
                });
            }

            Dictionary<string, double> density = new Dictionary<string, double>();
            List<SimPart> sim = EditorReadout.SimOf(parts, facts, EditorReadout.AirResources(parts), density, 0.0);
            // The engines staged by now and switched off.
            HashSet<int> off = new HashSet<int>();
            for (int i = 0; i < parts.Count; i++)
                if (parts[i].inverseStage >= v.currentStage && !HasIgnitedEngine(parts[i])) off.Add(i);
            Dictionary<string, double> total = new Dictionary<string, double>();
            foreach (Part part in parts)
                for (int r = 0; r < part.Resources.Count; r++)
                {
                    PartResource resource = part.Resources[r];
                    double sum;
                    total.TryGetValue(resource.resourceName, out sum);
                    total[resource.resourceName] = sum + resource.amount;
                }
            foreach (KeyValuePair<string, List<object>> marks in Marks(sim, density, v.currentStage, off, total)) dry[marks.Key] = marks.Value;
        }

        // Per propellant, the totals of it at which engines burning it run dry, in order: `total` (the
        // vessel's, every part's, a locked tank's too) less what the simulation burns of it by then.
        // None once parts holding any of it are dropped first: the total then is no longer the one on
        // the tape. The parts staged by now (activated at `currentStage` or above) are lit with it,
        // those in `off` not at all. Free of Unity, tested.
        internal static Dictionary<string, List<object>> Marks(List<SimPart> sim, IDictionary<string, double> density, int currentStage,
                                                               ICollection<int> off, IDictionary<string, double> total)
        {
            Dictionary<string, double> start = new Dictionary<string, double>();
            for (int i = 0; i < sim.Count; i++)
            {
                if (sim[i].ActivatesAt >= currentStage)
                {
                    sim[i].ActivatesAt = currentStage;
                    if (off.Contains(i)) sim[i].Rocket = null;
                }
                if (sim[i].Clamp) continue;
                foreach (KeyValuePair<string, double> held in sim[i].Amount)
                {
                    double sum;
                    start.TryGetValue(held.Key, out sum);
                    start[held.Key] = sum + held.Value;
                }
            }
            Dictionary<string, List<object>> dry = new Dictionary<string, List<object>>();
            foreach (StageSim.Dry ran in StageSim.Run(sim, density, 0.0, null).Dry)
                foreach (string name in ran.Propellants)
                {
                    double before, left, dropped, now;
                    if (!start.TryGetValue(name, out before) || !ran.Left.TryGetValue(name, out left) || !total.TryGetValue(name, out now)) continue;
                    ran.Dropped.TryGetValue(name, out dropped);
                    if (dropped > Thrown) continue;
                    List<object> marks;
                    if (!dry.TryGetValue(name, out marks)) dry[name] = marks = new List<object>();
                    marks.Add(Math.Round(now - (before - left), 1));
                }
            return dry;
        }
    }
}
