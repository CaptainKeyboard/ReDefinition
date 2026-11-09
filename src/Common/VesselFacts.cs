using System;

namespace ReDefinition.Core
{
    // What a vessel's engines and parts give right now, as the flight readouts and the planner both read it.
    internal static class VesselFacts
    {
        // What one engine gives at full throttle at an air pressure (in atmospheres), at its thrust limit, in kN. It is the fuel flow times the
        // specific impulse at that pressure times g0, as MechJeb's vessel state and the game work it out. A vacuum engine gives little at the ground.
        internal static double FullThrust(ModuleEngines engine, double pressureAtmospheres)
        {
            double isp = engine.atmosphereCurve != null ? engine.atmosphereCurve.Evaluate((float)pressureAtmospheres) : 0.0;
            double most = engine.maxFuelFlow * engine.flowMultiplier * isp * engine.g;
            double least = engine.minFuelFlow * engine.flowMultiplier * isp * engine.g;
            return least + (most - least) * engine.thrustPercentage / 100.0;
        }

        // The hottest part's temperature against its limit; the part's name comes with it. Zero for a vessel with no part that has a limit.
        // `scale` makes the parts count as hotter, to see a guard act early; 1 for the plain figure.
        internal static double Hottest(Vessel vessel, double scale, out string name)
        {
            double worst = 0.0;
            name = "";
            foreach (Part part in vessel.parts)
            {
                // A part burns where its inside or its skin goes over its limit.
                double share = part.maxTemp > 0.0 ? part.temperature / part.maxTemp : 0.0;
                if (part.skinMaxTemp > 0.0) share = Math.Max(share, part.skinTemperature / part.skinMaxTemp);
                share *= scale;
                if (share <= worst) continue;
                worst = share;
                name = part.partInfo != null ? part.partInfo.title : part.name;
            }
            return worst;
        }
    }
}
