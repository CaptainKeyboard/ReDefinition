using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ReDefinition.Landing;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ReDefinition.Autopilot.Guidance
{
    // The prediction of the whole descent. It runs on a thread and is asked for again as soon as the last one is in. The class holds what the
    // rules of the descent take from the vessel and the body to ask for it. That is where the brake ends, whether the air does the braking, the
    // speed limit, and the drag of the vessel and its parachutes (MechJebModuleLandingAutopilot, GPL-3.0).
    internal sealed class LandingPrediction
    {
        // The vessel's brake ends this high over the landing site where there is no air to brake in; a controlled descent follows.
        private const double VacuumBrakeEnd = 200.0;

        private readonly Queue<ReentrySimulation.Result> ready = new Queue<ReentrySimulation.Result>();
        private readonly Stopwatch clock = new Stopwatch();
        private ReentrySimulation.Result result;
        private bool running;
        private long waitMilliseconds;

        // The last prediction that came in; null before one has.
        internal ReentrySimulation.Result Result
        {
            get { return result; }
        }

        // Where the brake ends: 200 m over the landing site in a vacuum; with air, as high as the air takes to stop the vessel, from the drag length.
        internal static double BrakeEnd(Vessel vessel, double landing)
        {
            CelestialBody body = vessel.mainBody;
            if (!UsesAir(vessel, landing)) return VacuumBrakeEnd + landing;
            return 1.1 * DragLength(body, landing, AverageDrag(vessel) + ParachuteDrag(vessel), vessel.GetTotalMass()) + landing;
        }

        // MechJeb's test: the air brakes the vessel before it reaches the ground where the drag length at the site is under 0.7 of the air's height.
        internal static bool UsesAir(Vessel vessel, double landing)
        {
            CelestialBody body = vessel.mainBody;
            return body.atmosphere && DragLength(body, landing, AverageDrag(vessel) + ParachuteDrag(vessel), vessel.GetTotalMass()) < 0.7 * body.atmosphereDepth;
        }

        internal static IDescentSpeedPolicy Policy(Vessel vessel, double landing, double decelEnd, double gravity, double thrust)
        {
            double radius = vessel.mainBody.Radius + decelEnd;
            return UsesAir(vessel, landing)
                ? (IDescentSpeedPolicy)new PoweredCoastDescentSpeedPolicy(radius, gravity, thrust)
                : new SafeDescentSpeedPolicy(radius, gravity, thrust);
        }

        // How far the vessel goes before the air has taken its speed, from the air's density there, the drag and the mass.
        private static double DragLength(CelestialBody body, double altitude, double drag, double mass)
        {
            Vector3d position = body.GetWorldSurfacePosition(0.0, 0.0, altitude);
            double density = FlightGlobals.getAtmDensity(FlightGlobals.getStaticPressure(position, body), FlightGlobals.getExternalTemperature(position, body));
            if (density <= 0.0) return double.MaxValue;
            return mass / (0.0005 * PhysicsGlobals.DragMultiplier * density * drag);
        }

        // What the parachutes add to the drag when they are all out: the most the deployed shape has over the packed one (MechJeb's
        // ParachuteAddedDragCoef; not the exact number, but good enough).
        private static double ParachuteDrag(Vessel vessel)
        {
            if (!vessel.mainBody.atmosphere) return 0.0;
            float added = 0f;
            foreach (Part part in vessel.parts)
                foreach (ModuleParachute chute in part.FindModulesImplementing<ModuleParachute>())
                {
                    float most = 0f;
                    foreach (DragCube cube in chute.part.DragCubes.Cubes)
                    {
                        if (cube.Name != "DEPLOYED") continue;
                        for (int face = 0; face < 6; face++) most = Mathf.Max(most, chute.part.DragCubes.WeightedDrag[face] - cube.Weight * cube.Drag[face]);
                    }
                    added += most;
                }
            return added * PhysicsGlobals.DragCubeMultiplier;
        }

        // The parachutes that are packed, and may be let out now without tearing: where the vessel is under the height they are set to open at
        // over the landing site (MechJeb's DeployParachutes).
        internal static bool ChutesToDeploy(Vessel vessel, double landing)
        {
            if (!vessel.mainBody.atmosphere) return false;
            foreach (Part part in vessel.parts)
                foreach (ModuleParachute chute in part.FindModulesImplementing<ModuleParachute>())
                    if (chute.deploymentState == ModuleParachute.deploymentStates.STOWED && chute.deploymentSafeState == ModuleParachute.deploymentSafeStates.SAFE
                        && chute.deployAltitude + landing > vessel.altitude)
                        return true;
            return false;
        }

        // An average drag for the whole vessel: far from exact, and quick (MechJeb's VesselAverageDrag).
        private static double AverageDrag(Vessel vessel)
        {
            float coefficient = 0f;
            foreach (Part part in vessel.parts)
            {
                if (part.DragCubes.None || part.ShieldedFromAirstream) continue;
                float area = 0f;
                for (int face = 0; face < 6; face++) area = part.DragCubes.WeightedDrag[face] * part.DragCubes.AreaOccluded[face];
                coefficient += area / 6f;
            }
            return coefficient * PhysicsGlobals.DragCubeMultiplier;
        }

        // Starts the prediction again where the last is in.
        internal void Start(Vessel vessel, IDescentSpeedPolicy policy, double decelEnd, double thrust, double landing)
        {
            if (running || vessel.LandedOrSplashed) return;
            if (clock.IsRunning && clock.ElapsedMilliseconds < waitMilliseconds) return;
            running = true;
            clock.Reset();
            clock.Start();
            Orbit patch = vessel.orbit;
            ReentrySimulation.SimCurves curves = ReentrySimulation.SimCurves.Borrow(patch.referenceBody);
            SimulatedVessel simulated = SimulatedVessel.Borrow(vessel, curves, patch.StartUT, -1);
            ReentrySimulation simulation = ReentrySimulation.Borrow(patch, patch.StartUT, simulated, curves, policy, decelEnd, thrust, 3.0, landing, false, 0.2,
                Time.fixedDeltaTime, 1.0, false);
            ThreadPool.QueueUserWorkItem(Run, simulation);
        }

        private void Run(object state)
        {
            var simulation = (ReentrySimulation)state;
            try
            {
                ReentrySimulation.Result outcome = simulation.RunSimulation();
                lock (ready) ready.Enqueue(outcome);
                long took = clock.ElapsedMilliseconds;
                waitMilliseconds = (long)Math.Max(2 * took, 200.0);
                clock.Reset();
                clock.Start();
            }
            catch (Exception e)
            {
                Debug.LogError("[ReDefinition] The landing prediction failed: " + e);
            }
            finally
            {
                simulation.Release();
                running = false;
            }
        }

        // Takes in the predictions that are ready, and gives each the height of the ground where it ends.
        internal void Collect()
        {
            lock (ready)
            {
                while (ready.Count > 0)
                {
                    ReentrySimulation.Result next = ready.Dequeue();
                    if (next.Outcome == ReentrySimulation.Outcome.ERROR)
                    {
                        if (next.Exception != null) Debug.LogError("[ReDefinition] The landing prediction threw: " + next.Exception);
                        next.Release();
                        continue;
                    }
                    if (next.Body != null) next.EndASL = next.Body.TerrainAltitude(next.EndPosition.Latitude, next.EndPosition.Longitude);
                    if (result != null) result.Release();
                    result = next;
                }
            }
        }
    }
}
