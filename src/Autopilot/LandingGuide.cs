using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ReDefinition.Landing;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ReDefinition.Autopilot
{
    // The descent to the ground as MechJeb's landing flies it, told to the player instead. The prediction of the whole descent runs on a thread.
    // From it come the height of the landing site, when the brake is lit, and the throttle that follows the speed limit. The rules are those of
    // MechJebModuleLandingAutopilot and its steps (CoastToDeceleration, DecelerationBurn; GPL-3.0), with the vessel's state read from KSP.
    internal sealed class LandingGuide
    {
        // The vessel's brake ends this high over the landing site where there is no air to brake in; a controlled descent follows.
        private const double VacuumBrakeEnd = 200.0;
        // The speed, as a part of the limit, from which the brake is lit.
        private const double BrakeAt = 0.9;
        // How fast the throttle follows the limit, in seconds.
        private const double FollowTime = 0.3;
        private const double MinimumGap = 0.2;

        private readonly Queue<ReentrySimulation.Result> ready = new Queue<ReentrySimulation.Result>();
        private readonly Stopwatch clock = new Stopwatch();
        private ReentrySimulation.Result result;
        private bool running;
        private long waitMilliseconds;

        // The throttle the last call asked for, 0 to 1.
        internal double Throttle { get; private set; }

        // What the player is to do now, and the throttle for it. The prediction is asked for again as soon as the last one is in.
        internal string Describe(Vessel vessel)
        {
            Throttle = 0.0;
            CelestialBody body = vessel.mainBody;
            Collect();
            double landing = result != null && result.Outcome == ReentrySimulation.Outcome.LANDED ? result.EndASL : Math.Max(0.0, vessel.terrainAltitude);
            double thrust = Engines.Acceleration(vessel);
            double gravity = body.GeeASL * 9.81;
            if (thrust <= gravity) return "Descent: the engines are too weak to brake here.";
            double decelEnd = BrakeEnd(vessel, landing);
            IDescentSpeedPolicy policy = Policy(vessel, landing, decelEnd, gravity, thrust);
            Start(vessel, policy, decelEnd, thrust, landing);

            Vector3d position = vessel.GetWorldPos3D() - body.position;
            Vector3d velocity = vessel.srf_velocity;
            double speed = velocity.magnitude;
            double above = vessel.altitude - landing;
            if (above < 30.0 && speed < 5.0) return "Descent: nearly down, touch down gently.";
            if (!Engines.Running(vessel) && speed > 20.0) return "Descent: no engine is lit, fire the next stage.";
            if (result == null) return "Descent: working out where it lands.";
            double limit = policy.MaxAllowedSpeed(position, velocity);
            if (vessel.altitude < decelEnd + 5.0)
            {
                double wanted = Math.Max(0.5, Math.Min(8.0, above / 10.0));
                Throttle = Follow(velocity, position, wanted, gravity, thrust);
                return "Final descent: kill the sideways speed, lower at " + wanted.ToString("0.0") + " m/s, throttle " + (Throttle * 100.0).ToString("0") + " %, "
                       + above.ToString("0") + " m up.";
            }
            if (speed > BrakeAt * limit)
            {
                Throttle = Follow(velocity, position, limit, gravity, thrust);
                return "Descent: brake, retrograde, throttle " + (Throttle * 100.0).ToString("0") + " %, " + speed.ToString("0") + " m/s, limit " + limit.ToString("0")
                       + " m/s, " + (above / 1000.0).ToString("0.0") + " km up.";
            }
            if (result.Trajectory != null && result.Trajectory.Count > 0)
            {
                double seconds = result.Trajectory[0].UT - Planetarium.GetUniversalTime();
                return "Descent: point retrograde, brake in " + Math.Max(0.0, seconds).ToString("0") + " s, landing at " + (landing / 1000.0).ToString("0.0") + " km altitude.";
            }
            return "Descent: point retrograde, the prediction has the brake at the speed limit of " + limit.ToString("0") + " m/s.";
        }

        // The throttle that brings the speed to `limit`: MechJeb's DecelerationBurn.
        private static double Follow(Vector3d velocity, Vector3d position, double limit, double gravity, double thrust)
        {
            double vertical = Math.Abs(Vector3d.Dot(velocity.normalized, position.normalized));
            double controlled = velocity.magnitude * Math.Sign(Vector3d.Dot(velocity, position));
            double wanted = (-limit - controlled) / FollowTime;
            double least = -gravity * vertical;
            double most = thrust - gravity * vertical;
            return most - least > 0.0 ? Math.Max(0.0, Math.Min(1.0, (wanted - least) / (most - least))) : 0.0;
        }

        // Where the brake ends: 200 m over the landing site in a vacuum; with air, as high as the air takes to stop the vessel, from the drag length.
        private static double BrakeEnd(Vessel vessel, double landing)
        {
            CelestialBody body = vessel.mainBody;
            if (!UsesAir(vessel, landing)) return VacuumBrakeEnd + landing;
            return 1.1 * DragLength(body, landing, AverageDrag(vessel), vessel.GetTotalMass()) + landing;
        }

        // MechJeb's test: the air brakes the vessel before it reaches the ground where the drag length at the site is under 0.7 of the air's height.
        private static bool UsesAir(Vessel vessel, double landing)
        {
            CelestialBody body = vessel.mainBody;
            return body.atmosphere && DragLength(body, landing, AverageDrag(vessel), vessel.GetTotalMass()) < 0.7 * body.atmosphereDepth;
        }

        private static IDescentSpeedPolicy Policy(Vessel vessel, double landing, double decelEnd, double gravity, double thrust)
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
        private void Start(Vessel vessel, IDescentSpeedPolicy policy, double decelEnd, double thrust, double landing)
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
        private void Collect()
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
