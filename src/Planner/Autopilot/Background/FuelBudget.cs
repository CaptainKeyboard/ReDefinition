using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Planner.Autopilot.Flight;
using ReDefinition.Planner.Autopilot.Guidance;
using ReDefinition.Planner.Autopilot.Planning;
using ReDefinition.Core;
using ReDefinition.Planner.Landing;
using ReDefinition.Planner.Orbits;
using UnityEngine;

namespace ReDefinition.Planner.Autopilot.Background
{
    // What a burn costs the vessel: the stages' propellant where KSP's stages of it are known, else one tank for all.
    internal sealed class FuelBudget
    {
        public FuelTank FuelTank;
        public IBurnShare Stages;
        public string Name = "";

        public void Apply(BackgroundStep step, double deltaV)
        {
            if (Stages == null)
            {
                step.Keep = FuelTank.Burn(deltaV);
                return;
            }
            var gone = new List<uint>();
            Stages.Burn(deltaV, step.Keeps, gone);
            if (gone.Count == 0) return;
            var cut = new PartsCut { Name = Name + " Debris", Type = VesselType.Debris };
            cut.Parts.AddRange(gone);
            step.Cuts.Add(cut);
        }
    }

    // The propellant left and the vessel's mass, as the burns of the plan use them up.
    internal sealed class FuelTank
    {
        public double Propellant, Mass, Exhaust;

        // The share of the propellant kept after a burn of this delta-v, in m/s.
        public double Burn(double deltaV)
        {
            if (deltaV <= 0.0 || Exhaust <= 0.0 || Propellant <= 0.0) return 1.0;
            double used = Math.Min(Propellant, Mass * (1.0 - Math.Exp(-deltaV / Exhaust)));
            double keep = 1.0 - used / Propellant;
            Propellant -= used;
            Mass -= used;
            return keep;
        }
    }
}
