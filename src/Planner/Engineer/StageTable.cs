using System.Collections.Generic;

namespace ReDefinition.Planner.Engineer
{
    // One stage of the vessel in the editor, as the planner takes it: what KSP's
    // own delta-v simulation gives for it (VesselDeltaV) and what is worked out
    // beside it. Stages are numbered as KSP numbers them: the highest fires
    // first, stage 0 last.
    internal sealed class StageRow
    {
        // KSP's number for the stage: the highest fires first, stage 0 last.
        public int Number;

        // For the situation chosen (body, sea level, altitude or vacuum).
        public double DeltaV;
        public double Thrust;
        public double BurnTime;
        public double StartMass;
        public double EndMass;

        // In vacuum, whatever situation is chosen: what the planner budgets with.
        public double DeltaVVacuum;
        public double ThrustVacuum;
        // At the surface of each body with air, by its name (CelestialBody.bodyName).
        public Dictionary<string, double> ThrustAtSurface = new Dictionary<string, double>();

        // Its engines cannot be shut down or throttled (solid rocket boosters,
        // ModuleEngines.throttleLocked or !allowShutdown): once lit it burns out.
        public bool Solid;
        // The air-breathing engines' part of the launch (AirLaunch), not a stage of
        // KSP's.
        public bool Air;
        // The electric charge its engines burn at full thrust, in units per second
        // (an ion engine's), which KSP's simulation leaves out.
        public double ElectricRate;

        // A copy to change, its thrust at the surfaces its own.
        internal StageRow Copy()
        {
            StageRow copy = (StageRow)MemberwiseClone();
            copy.ThrustAtSurface = new Dictionary<string, double>(ThrustAtSurface);
            return copy;
        }
    }

    // The rocket equation and the propellant thrusters can burn, as the planner
    // works with them. Free of Unity, tested.
    internal static class StageTable
    {

        // PhysicsGlobals.GravitationalAcceleration, KSP's g0.
        internal const double StandardGravity = 9.80665;
    }
}
