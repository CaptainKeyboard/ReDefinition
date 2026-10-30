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

        // The acceleration the engines that are lit give, or those of the stage to fire next where none is lit, at full thrust, in m/s2. Each
        // engine gives its fuel flow times its specific impulse at the air pressure there is now. An engine made for a vacuum gives little at
        // the ground. This is how MechJeb's vessel state has it (VesselState.cs, GPL-3.0).
        internal static double Acceleration(Vessel vessel)
        {
            double thrust = 0.0;
            bool lit = Running(vessel);
            double pressure = vessel.staticPressurekPa * PhysicsGlobals.KpaToAtmospheres;
            foreach (Part part in vessel.parts)
                foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
                    if (lit ? engine.EngineIgnited && !engine.flameout : part.inverseStage == StageManager.CurrentStage - 1)
                        thrust += FullThrust(engine, pressure);
            double mass = vessel.GetTotalMass();
            return mass > 0.0 ? thrust / mass : 0.0;
        }

        // What one engine gives at full throttle at an air pressure (in atmospheres), at its thrust limit, in kN. It is the fuel flow times the
        // specific impulse at that pressure times g0, as MechJeb's vessel state and the game work it out. A vacuum engine gives little at the ground.
        internal static double FullThrust(ModuleEngines engine, double pressureAtmospheres)
        {
            double isp = engine.atmosphereCurve != null ? engine.atmosphereCurve.Evaluate((float)pressureAtmospheres) : 0.0;
            double most = engine.maxFuelFlow * engine.flowMultiplier * isp * engine.g;
            double least = engine.minFuelFlow * engine.flowMultiplier * isp * engine.g;
            return least + (most - least) * engine.thrustPercentage / 100.0;
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
