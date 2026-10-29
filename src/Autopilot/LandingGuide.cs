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
        // How fast the throttle follows the limit, in seconds: MechJeb's.
        private const double FollowTime = 0.3;
        // The most the throttle changes in a second, as a share of its range, so that it is a curve a pilot can fly. It is no lag: a lag in the
        // loop that holds a hover makes the vessel swing up and down.
        private const double MaxChange = 1.5;
        // The speed a vessel touches down at, in m/s: MechJeb's setting for it.
        private const double TouchdownSpeed = 0.5;
        private const double MinimumGap = 0.2;

        private readonly Queue<ReentrySimulation.Result> ready = new Queue<ReentrySimulation.Result>();
        private readonly Stopwatch clock = new Stopwatch();
        private ReentrySimulation.Result result;
        private bool running;
        private long waitMilliseconds;

        // The throttle the last call asked for, 0 to 1, and the direction to point the nose along, where there is one.
        internal double Throttle { get; private set; }
        internal Vector3d Direction { get; private set; }
        internal bool HasDirection { get; private set; }
        // The bar of the descent: the vertical speed against the speed it should be at this height, over the mark where it is too fast.
        internal double BarFill { get; private set; }
        internal string BarText { get; private set; }
        internal int BarLevel { get; private set; }
        internal bool HasBar { get; private set; }

        // What the player is to do now, and the throttle and the pointing for it. Without air the descent is straight down. With air it is
        // MechJeb's: the prediction is asked for again as soon as the last one is in.
        internal string Describe(Vessel vessel)
        {
            string advice = Advice(vessel);
            string part;
            double hottest = Heat.Hottest(vessel, out part);
            return hottest > 0.8 ? advice + " HOT: " + part + " at " + (hottest * 100.0).ToString("0") + " % of its limit." : advice;
        }

        // The steps of MechJeb's landing (GPL-3.0). It coasts until the prediction says the brake is to be lit (CoastToDeceleration,
        // DecelerationBurn) and brakes against the speed limit. At the brake's end it kills the sideways speed in a hover (KillHorizontalVelocity),
        // and comes down at a speed that falls with the height (FinalDescent).
        private enum Step
        {
            Coast,
            Brake,
            Kill,
            Final,
        }

        private Step step = Step.Coast;
        // Set once the final descent has lit the engines, and kept, as MechJeb keeps it, so that the engines do not go on and off at the boundary.
        private bool finalLit;
        private double smooth;
        private double smoothedAt = -1.0;

        // The throttle moved towards what is asked by no more than a share of its range in a second, so that it is a curve and not a series of bursts.
        private double Ease(double asked)
        {
            double now = Time.realtimeSinceStartup;
            double dt = smoothedAt < 0.0 ? 0.0 : Math.Min(0.5, now - smoothedAt);
            smoothedAt = now;
            double most = MaxChange * dt;
            smooth += Math.Max(-most, Math.Min(most, asked - smooth));
            smooth = Math.Max(0.0, Math.Min(1.0, smooth));
            return smooth;
        }

        private string Advice(Vessel vessel)
        {
            Throttle = 0.0;
            HasDirection = false;
            HasBar = false;
            if (vessel.LandedOrSplashed)
            {
                step = Step.Coast;
                finalLit = false;
            }
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
            Vector3d up = position.normalized;
            double speed = velocity.magnitude;
            double vertical = Vector3d.Dot(velocity, up);
            double above = vessel.altitude - landing;
            if (above < 3.0 && speed < 3.0) return "Down. Cut the engines.";
            if (!Engines.Running(vessel) && speed > 20.0) return "Descent: no engine is lit, fire the next stage.";
            if (result == null) return "Descent: working out where it lands.";
            HasDirection = true;
            double limit = policy.MaxAllowedSpeed(position, velocity);
            if (vessel.heightFromTerrain >= 0f) above = Math.Min(above, vessel.heightFromTerrain);
            if (vessel.altitude < decelEnd + 5.0 && step != Step.Final) step = UsesAir(vessel, landing) ? Step.Final : Step.Kill;
            if (step == Step.Kill || step == Step.Final) return Landing(vessel, position, velocity, up, vertical, above, gravity, thrust);

            Direction = -velocity.normalized;
            double startIn = result.Trajectory != null && result.Trajectory.Count > 0 ? result.Trajectory[0].UT - Planetarium.GetUniversalTime() : double.MaxValue;
            if (step == Step.Coast && startIn <= 5.0) step = Step.Brake;
            HasBar = true;
            BarFill = Math.Max(0.0, Math.Min(1.0, 0.5 * speed / Math.Max(1.0, limit)));
            BarText = speed.ToString("0") + " m/s";
            BarLevel = speed > 2.0 * limit ? 2 : speed > 1.15 * limit ? 1 : 0;
            if (step == Step.Coast)
                return "Descent: point retrograde, brake in " + (startIn < 1e8 ? Math.Max(0.0, startIn).ToString("0") + " s" : "a while") + ". Landing at "
                       + (landing / 1000.0).ToString("0.0") + " km altitude.";
            double limitAfter = policy.MaxAllowedSpeed(position + velocity * Time.fixedDeltaTime, velocity);
            bool aligned = Vector3d.Dot(vessel.GetTransform().up, Direction) >= 0.75;
            Throttle = Ease(vertical > 0.0 || !aligned ? 0.0 : Follow(velocity, position, limit, limitAfter, gravity, thrust));
            return "Descent: throttle " + (Throttle * 100.0).ToString("0") + " %, retrograde, " + speed.ToString("0") + " m/s, limit " + limit.ToString("0") + " m/s, "
                   + above.ToString("0") + " m up.";
        }

        // The last steps. First the sideways speed is killed in a hover, the vessel pointing a little against it; then the vessel comes down at a
        // speed that falls with the height, as MechJeb's final descent has it, and touches down slowly.
        private string Landing(Vessel vessel, Vector3d position, Vector3d velocity, Vector3d up, double vertical, double above, double gravity, double thrust)
        {
            Vector3d forward = vessel.GetTransform().up;
            Vector3d pointing = Vector3d.Exclude(up, forward).normalized;
            double sideways = (velocity - up * vertical).magnitude;
            if (step == Step.Kill && Vector3d.Dot(pointing, velocity) > 0.0) step = Step.Final;
            if (step == Step.Final && above > 300.0)
            {
                // Above 300 m the speed is held to the limit of the gravity turn policy, pointing retrograde (FinalDescent).
                Direction = vertical > 0.0 || velocity.magnitude < 5.0 ? up : -velocity.normalized;
                double limit = new GravityTurnDescentSpeedPolicy(vessel.mainBody.Radius + (vessel.altitude - above), gravity, thrust).MaxAllowedSpeed(position, velocity);
                Throttle = Ease(vertical > 0.0 ? 0.0 : Follow(velocity, position, limit, limit, gravity, thrust));
                HasBar = true;
                BarFill = Math.Max(0.0, Math.Min(1.0, 0.5 * velocity.magnitude / Math.Max(1.0, limit)));
                BarText = velocity.magnitude.ToString("0") + " m/s";
                BarLevel = velocity.magnitude > 2.0 * limit ? 2 : velocity.magnitude > 1.15 * limit ? 1 : 0;
                return "Final descent: throttle " + (Throttle * 100.0).ToString("0") + " %, " + velocity.magnitude.ToString("0") + " m/s, limit " + limit.ToString("0")
                       + " m/s, " + above.ToString("0") + " m up.";
            }
            if (step == Step.Kill)
            {
                Direction = (up + 0.2 * pointing).normalized;
                double wanted = (0.0 - vertical) / 1.0;
                double most = -gravity + Vector3d.Dot(forward, up) * thrust;
                Throttle = Ease(most + gravity > 0.0 ? Math.Max(0.0, Math.Min(1.0, (wanted + gravity) / (most + gravity))) : 0.0);
                HasBar = true;
                BarFill = 0.5;
                BarText = sideways.ToString("0") + " m/s";
                return "Descent: kill the sideways speed in a hover, " + sideways.ToString("0") + " m/s left, throttle " + (Throttle * 100.0).ToString("0") + " %.";
            }
            Direction = sideways > 3.0 ? -velocity.normalized : up;
            if (sideways >= 5.0)
            {
                Throttle = Ease(1.0);
                return "Descent: the sideways speed is " + sideways.ToString("0") + " m/s. Kill it, retrograde at full throttle.";
            }
            double desired = -Math.Min(1.0, above / 300.0) * Math.Sqrt((thrust - gravity) * 2.0 * 300.0) * 0.9;
            double target = Math.Min(-TouchdownSpeed, desired);
            if (desired >= vertical) finalLit = true;
            if (!finalLit)
            {
                Throttle = Ease(0.0);
                HasBar = true;
                BarFill = 0.5 * Math.Max(0.0, Math.Min(1.0, vertical / Math.Min(-0.1, target)));
                BarText = (-vertical).ToString("0") + " m/s";
                return "Descent: engines off, falling. Light them when the speed reaches " + (-desired).ToString("0") + " m/s, now " + (-vertical).ToString("0") + " m/s.";
            }
            double wantedAccel = (target - vertical) / 1.0;
            double least = -gravity;
            double greatest = thrust - gravity;
            Throttle = Ease(Math.Max(0.0, Math.Min(1.0, (wantedAccel - least) / (greatest - least))));
            HasBar = true;
            BarFill = Math.Max(0.0, Math.Min(1.0, 0.5 * vertical / Math.Min(-0.1, target)));
            BarText = (-vertical).ToString("0.0") + " m/s";
            BarLevel = vertical < 2.0 * target ? 2 : vertical < 1.15 * target ? 1 : 0;
            return "Final descent: throttle " + (Throttle * 100.0).ToString("0") + " %, " + (-vertical).ToString("0.0") + " m/s down, wanted " + (-target).ToString("0.0")
                   + " m/s, " + above.ToString("0") + " m up.";
        }

        // The throttle that brings the speed to `limit`: MechJeb's DecelerationBurn, with its lead for the limit's own change.
        private static double Follow(Vector3d velocity, Vector3d position, double limit, double limitAfter, double gravity, double thrust)
        {
            double vertical = Math.Abs(Vector3d.Dot(velocity.normalized, position.normalized));
            double controlled = velocity.magnitude * Math.Sign(Vector3d.Dot(velocity, position));
            double wanted = (-limit - controlled) / FollowTime + (limit - limitAfter) / Math.Max(0.005, Time.fixedDeltaTime);
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
