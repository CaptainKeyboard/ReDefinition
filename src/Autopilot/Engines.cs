using KSP.UI.Screens;

namespace ReDefinition.Autopilot
{
    // The vessel's engines as a burn needs them: whether one is running, and the next stage fired when none is.
    internal static class Engines
    {
        // Whether an engine that has not flamed out is lit and can give thrust.
        internal static bool Running(Vessel vessel)
        {
            if (vessel == null) return false;
            foreach (Part part in vessel.parts)
                foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
                    if (engine.EngineIgnited && !engine.flameout && engine.isOperational) return true;
            return false;
        }

        // The delta-v the propellant on board gives: the best vacuum specific impulse of its engines on the whole mass, by the rocket equation. It
        // counts every tank, the stages not yet fired too, so it is the most the vessel could give.
        internal static double DeltaV(Vessel vessel)
        {
            double propellant = 0.0;
            double isp = 0.0;
            foreach (Part part in vessel.parts)
            {
                foreach (PartResource resource in part.Resources)
                    if (resource.resourceName == "LiquidFuel" || resource.resourceName == "Oxidizer" || resource.resourceName == "SolidFuel")
                        propellant += resource.amount * resource.info.density;
                foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
                    isp = System.Math.Max(isp, engine.atmosphereCurve.Evaluate(0f));
            }
            double mass = vessel.GetTotalMass();
            if (propellant <= 0.0 || isp <= 0.0 || propellant >= mass) return 0.0;
            return isp * 9.80665 * System.Math.Log(mass / (mass - propellant));
        }

        // The fuel the vessel carries, in units of liquid fuel, and the throttle: what a log line says about a burn.
        internal static string Report(Vessel vessel)
        {
            double fuel = 0.0;
            foreach (Part part in vessel.parts)
                foreach (PartResource resource in part.Resources)
                    if (resource.resourceName == "LiquidFuel") fuel += resource.amount;
            double thrust = 0.0;
            int engines = 0;
            foreach (Part part in vessel.parts)
                foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
                    if (engine.EngineIgnited && !engine.flameout)
                    {
                        engines++;
                        thrust += engine.maxThrust * engine.thrustPercentage / 100.0;
                    }
            double mass = vessel.GetTotalMass();
            return fuel.ToString("0") + " fuel, " + engines + " engines " + thrust.ToString("0") + " kN on " + mass.ToString("0.0") + " t = " + (thrust / mass).ToString("0.0") + " m/s2, engine "
                   + Running(vessel) + ", stage " + StageManager.CurrentStage;
        }

        // Fires the next stage where no engine runs, so that the burn has thrust: a stage dropped when its fuel is gone, the upper stage lit.
        // One stage a call; false where no stage is left to fire.
        internal static bool FireNextStageIfCold(Vessel vessel)
        {
            if (Running(vessel)) return true;
            if (StageManager.CurrentStage <= 0) return false;
            StageManager.ActivateNextStage();
            return true;
        }
    }
}
