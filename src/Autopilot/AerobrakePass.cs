using System;
using ReDefinition.Landing;
using UnityEngine;

namespace ReDefinition.Autopilot
{
    // How deep a pass through the air must dip to slow a vessel into the orbit asked for. The pass is simulated with the descent's simulation
    // (MechJeb's ReentrySimulation, which ends a run where the vessel leaves the air again). The orbit is the vessel's with its periapsis moved
    // to the height tried, and the apoapsis kept. The height is found by halving between the ground and the top of the air. It is found to
    // a part of the height over which the air's pressure falls by a factor e, which the body's own pressure gives. The shallowest pass that
    // brings the apoapsis down to the orbit's is taken, for it loads the vessel the least.
    internal static class AerobrakePass
    {
        // The most halvings, so that a body whose pressure gives no height cannot keep it searching.
        private const int MostHalvings = 40;
        // The part of that height the pass is found to.
        private const double Precision = 0.05;

        // What one simulated pass came to: whether the vessel left the air again, and the apoapsis it left with.
        private struct Pass
        {
            public bool Leaves;
            public double Apoapsis;
        }

        // The height over the ground to dip to, in metres, or a negative number with `why` set where no pass slows the vessel in the air.
        internal static double Choose(Vessel vessel, double targetApoapsis, out string why)
        {
            why = null;
            CelestialBody body = vessel.mainBody;
            Orbit orbit = vessel.orbit;
            if (!body.atmosphere || orbit == null || orbit.eccentricity >= 1.0)
            {
                why = "There is no closed orbit to aerobrake from.";
                return -1.0;
            }
            double now = Planetarium.GetUniversalTime();
            double apoapsisUT = now + orbit.timeToAp;
            Vector3d position = orbit.getRelativePositionAtUT(apoapsisUT);
            Vector3d direction = orbit.getOrbitalVelocityAtUT(apoapsisUT).normalized;
            double allowed = targetApoapsis + NodePlanner.Near(targetApoapsis);
            double known = Precision * ScaleHeight(body);

            // The shallow end is the top of the air, which does not slow the vessel; the deep end the ground, which it does not leave.
            double shallow = body.atmosphereDepth, deep = 0.0;
            Pass shallowest = Run(vessel, body, position, direction, apoapsisUT, shallow);
            if (shallowest.Leaves && shallowest.Apoapsis <= allowed) return shallow;
            for (int i = 0; i < MostHalvings && shallow - deep > known; i++)
            {
                double middle = 0.5 * (shallow + deep);
                Pass pass = Run(vessel, body, position, direction, apoapsisUT, middle);
                if (pass.Leaves && pass.Apoapsis > allowed) shallow = middle;
                else deep = middle;
            }
            Pass atDeep = Run(vessel, body, position, direction, apoapsisUT, deep);
            if (atDeep.Leaves && atDeep.Apoapsis <= allowed) return deep;
            // No pass does it in one go: the deepest that still leaves the air brakes most, and the next pass goes on from there.
            Pass atShallow = Run(vessel, body, position, direction, apoapsisUT, shallow);
            if (atShallow.Leaves) return shallow;
            why = "No pass through the air of " + body.bodyName + " slows the vessel and lets it leave the air.";
            return -1.0;
        }

        // The height over which the air's pressure falls by a factor e, found from the body's own pressure curve: the height of the pass is
        // known to a part of it.
        private static double ScaleHeight(CelestialBody body)
        {
            double sea = body.GetPressure(0.0);
            if (sea <= 0.0 || body.GetPressure(body.atmosphereDepth) > sea / Math.E) return body.atmosphereDepth;
            double low = 0.0, high = body.atmosphereDepth;
            while (high - low > 1.0)
            {
                double middle = 0.5 * (low + high);
                if (body.GetPressure(middle) > sea / Math.E) low = middle;
                else high = middle;
            }
            return 0.5 * (low + high);
        }

        // The simulated pass from the apoapsis down to a periapsis `height` over the ground.
        private static Pass Run(Vessel vessel, CelestialBody body, Vector3d position, Vector3d direction, double ut, double height)
        {
            double apoapsisRadius = position.magnitude;
            double periapsisRadius = body.Radius + height;
            double semiMajor = 0.5 * (apoapsisRadius + periapsisRadius);
            double speed = Math.Sqrt(body.gravParameter * (2.0 / apoapsisRadius - 1.0 / semiMajor));
            var orbit = new Orbit();
            orbit.UpdateFromStateVectors(position, direction * speed, body, ut);
            ReentrySimulation.SimCurves curves = ReentrySimulation.SimCurves.Borrow(body);
            SimulatedVessel simulated = SimulatedVessel.Borrow(vessel, curves, ut, -1);
            ReentrySimulation simulation = ReentrySimulation.Borrow(orbit, ut, simulated, curves, null, 0.0, 0.0, 3.0, 0.0, false, 0.2, Time.fixedDeltaTime, 1.0, false);
            ReentrySimulation.Result result = null;
            try
            {
                result = simulation.RunSimulation();
                if (result.Outcome != ReentrySimulation.Outcome.AEROBRAKED) return new Pass { Leaves = false, Apoapsis = double.MaxValue };
                Orbit end = result.EndOrbit();
                return new Pass { Leaves = end.eccentricity < 1.0, Apoapsis = end.eccentricity < 1.0 ? end.ApA : double.MaxValue };
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ReDefinition] The aerobraking pass could not be simulated: " + e.Message);
                return new Pass { Leaves = false, Apoapsis = double.MaxValue };
            }
            finally
            {
                simulation.Release();
                if (result != null) result.Release();
            }
        }
    }
}
