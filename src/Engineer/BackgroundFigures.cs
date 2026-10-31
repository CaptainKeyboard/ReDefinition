using ReDefinition.Autopilot.Background;
using ReDefinition.Autopilot.Planning;

namespace ReDefinition.Engineer
{
    // The engineer's figures for the background route: KSP's stages of the vessel as the planner reads them, and the planner's estimate of a launch.
    internal sealed class BackgroundFigures : IVesselFigures
    {
        public IBurnShare StagesOf(Vessel vessel)
        {
            return new StageBurner(EditorReadout.Read(vessel));
        }

        public double LaunchDeltaV(CelestialBody body, double altitude)
        {
            return RoutePlanner.Launch(Read(body, altitude));
        }

        public double LandingDeltaV(CelestialBody body, bool parachutes)
        {
            return RoutePlanner.Landing(Read(body, NextStepPlanner.LowOrbit(body)), parachutes);
        }

        public bool IsParachute(Part part)
        {
            return part.HasModuleImplementing<ModuleParachute>() || EditorReadout.IsRealChute(part);
        }

        private static PlannerBody Read(CelestialBody body, double altitude)
        {
            return new PlannerBody
            {
                Mu = body.gravParameter, Radius = body.Radius, RotationPeriod = body.rotationPeriod,
                AtmosphereDepth = body.atmosphere ? body.atmosphereDepth : 0.0,
                SurfacePressure = body.atmosphere ? body.atmospherePressureSeaLevel / 101.325 : 0.0, LowOrbitAltitude = altitude,
            };
        }
    }
}
