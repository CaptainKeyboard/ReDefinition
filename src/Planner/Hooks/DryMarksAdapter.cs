using System;
using System.Collections.Generic;
using ReDefinition.Core;
using ReDefinition.Planner.Engineer;

namespace ReDefinition.Planner
{
    // Where engines run dry on the dashboard's resource tapes, from the planner's own simulation (StageSim) run on the vessel as it flies.
    // The stages lit by now count as the current one, and an engine switched off is left out. It needs KSP's delta-v simulation
    // for the stage each part is dropped in (VesselDeltaV.PartInfo, decoupleStage), and without it nothing is marked.
    internal sealed class DryMarksAdapter : DryMarksHook
    {
        // Less of a resource than this, in units, dropped with parts is none.
        private const double Thrown = 0.01;

        // The engines of the part being looked at, reused so that a run allocates no list per part.
        private readonly List<ModuleEngines> engines = new List<ModuleEngines>();

        public override Dictionary<string, List<object>> Marks(Vessel v)
        {
            List<DeltaVPartInfo> infos = v.VesselDeltaV != null ? v.VesselDeltaV.PartInfo : null;
            if (infos == null || infos.Count == 0) return null;
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
                if (!decouple.TryGetValue(part, out stage)) return null;
                facts.Add(new PartFacts
                {
                    Parent = part.parent != null && index.TryGetValue(part.parent, out parent) ? parent : -1,
                    DecoupleStage = stage,
                    Clamp = part.HasModuleImplementing<LaunchClamp>(),
                });
            }

            Dictionary<string, double> density = new Dictionary<string, double>();
            List<SimPart> sim = EditorReadout.SimOf(parts, facts, VesselFacts.AirResources(parts), density, 0.0);
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
            return Marks(sim, density, v.currentStage, off, total);
        }

        // Whether any engine on the part is lit.
        private bool HasIgnitedEngine(Part part)
        {
            foreach (ModuleEngines engine in PartModules.Of(part, engines))
                if (engine != null && engine.EngineIgnited) return true;
            return false;
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
