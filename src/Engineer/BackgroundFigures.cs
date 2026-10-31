using ReDefinition.Autopilot.Background;

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
            return RoutePlanner.Launch(new PlannerBody
            {
                Mu = body.gravParameter, Radius = body.Radius, RotationPeriod = body.rotationPeriod,
                AtmosphereDepth = body.atmosphere ? body.atmosphereDepth : 0.0,
                SurfacePressure = body.atmosphere ? body.atmospherePressureSeaLevel / 101.325 : 0.0, LowOrbitAltitude = altitude,
            });
        }
    }
}
