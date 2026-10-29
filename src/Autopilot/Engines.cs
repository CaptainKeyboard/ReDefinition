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
