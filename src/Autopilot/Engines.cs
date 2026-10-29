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

        // How much of the nominal thrust the engines gave when last measured, and before a measurement a share that leaves room. Engines that
        // are lit may be starved of fuel or limited, and give less than their maximum says.
        private const double UnmeasuredShare = 0.75;
        private static double measuredShare = UnmeasuredShare;

        // The acceleration the engines that are lit give, or those of the stage to fire next where none is lit, at full thrust, in m/s2. It is
        // the nominal thrust of the engines times the share they last gave, which is measured whenever the throttle is up.
        internal static double Acceleration(Vessel vessel)
        {
            double thrust = 0.0;
            double actual = 0.0;
            bool lit = Running(vessel);
            foreach (Part part in vessel.parts)
                foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
                    if (lit ? engine.EngineIgnited && !engine.flameout : part.inverseStage == StageManager.CurrentStage - 1)
                    {
                        thrust += engine.maxThrust * engine.thrustPercentage / 100.0;
                        actual += engine.finalThrust;
                    }
            float throttle = FlightInputHandler.state.mainThrottle;
            if (lit && throttle > 0.2f && thrust > 0.0)
                measuredShare += (System.Math.Max(0.2, System.Math.Min(1.0, actual / (throttle * thrust))) - measuredShare) * 0.1;
            double mass = vessel.GetTotalMass();
            return mass > 0.0 ? thrust * measuredShare / mass : 0.0;
        }

        // Whether an engine gives thrust now: what the burn is, whoever holds the throttle.
        internal static bool Burning(Vessel vessel)
        {
            if (vessel == null) return false;
            foreach (Part part in vessel.parts)
                foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
                    if (engine.finalThrust > 0.01f) return true;
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
