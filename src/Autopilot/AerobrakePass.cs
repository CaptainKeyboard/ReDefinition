using System;
using ReDefinition.Landing;
using UnityEngine;

namespace ReDefinition.Autopilot
{
    // How deep a pass through the air must dip, found by simulating it with the descent's simulation (MechJeb's ReentrySimulation, which ends a
    // run where the vessel leaves the air again). The height is found by halving between the ground and the top of the air. It is found to a
    // part of the height over which the air's pressure falls by a factor e, which the body's own pressure gives. The shallowest pass that does
    // what is asked is taken, for it loads the vessel the least. A pass that would load it beyond its limits is none: the dynamic pressure
    // MechJeb's thrust controller holds to, and the g-force the weakest of the vessel's parts takes. Where no pass is within them the way
    // through the air is refused, with the figures.
    internal static class AerobrakePass
    {
        // The most halvings, so that a body whose pressure gives no height cannot keep it searching.
        private const int MostHalvings = 40;
        // The part of the pressure's height the pass is found to.
        private const double Precision = 0.05;
        // The settling temperature of the hottest part over its limit, above which a pass is refused. The settling temperature is a flat-plate
        // bound. On a Mun return through Kerbin's air it came out at twice the skin temperature the game showed on the hottest part (203 % of
        // its limit against 101 %). A part at twice its limit by the bound is therefore at it by the game's model. Below that the flight's live
        // guard holds.
        private const double HeatBound = 2.0;

        // What one simulated pass came to.
        private struct Pass
        {
            // The vessel met the ground, or left the air on a closed orbit with this apoapsis; else it left on an open one: it skips out.
            public bool Landed;
            public bool Captured;
            public double Apoapsis;
            // The most the pass loads the vessel: dynamic pressure in kPa, and g-force.
            public double Pressure;
            public double Gees;
            // The hottest part's settling temperature over its limit.
            public double Heat;

            public bool Skips
            {
                get { return !Landed && !Captured; }
            }
        }

        // The pass from a closed orbit, whose air is to bring its apoapsis down to `targetApoapsis`: the height to dip to over the ground, in
        // metres, or a negative number with `why` set. A pass too shallow to do it in one still counts, for the passes that follow go on.
        internal static double FromOrbit(Vessel vessel, double targetApoapsis, out string why)
        {
            why = null;
            CelestialBody body = vessel.mainBody;
            Orbit orbit = vessel.orbit;
            if (!body.atmosphere || orbit == null || orbit.eccentricity >= 1.0)
            {
                why = "There is no closed orbit to aerobrake from.";
                return -1.0;
            }
            double apoapsisUT = Planetarium.GetUniversalTime() + orbit.timeToAp;
            Vector3d position = orbit.getRelativePositionAtUT(apoapsisUT);
            Vector3d direction = orbit.getOrbitalVelocityAtUT(apoapsisUT).normalized;
            double allowed = targetApoapsis + NodePlanner.Near(targetApoapsis);
            return Search(vessel, body, height =>
                {
                    double apoapsisRadius = position.magnitude;
                    double semiMajor = 0.5 * (apoapsisRadius + body.Radius + height);
                    double speed = Math.Sqrt(body.gravParameter * (2.0 / apoapsisRadius - 1.0 / semiMajor));
                    var start = new Orbit();
                    start.UpdateFromStateVectors(position, direction * speed, body, apoapsisUT);
                    return Run(vessel, body, start, apoapsisUT);
                },
                pass => pass.Skips || (pass.Captured && pass.Apoapsis > allowed), pass => pass.Captured && pass.Apoapsis <= allowed, pass => pass.Captured, out why);
        }

        // The pass for an approach on the open orbit `encounter` round the body, which is to meet the air: the shallowest that does not skip out
        // of it, and for a landing straight in, may end on the ground. The height of the periapsis to aim at, over the ground, or a negative number
        // with `why` set. The approach keeps its speed at infinity, so what changes between the heights is where the periapsis lies.
        internal static double FromApproach(Vessel vessel, Orbit encounter, bool landing, out string why)
        {
            why = null;
            CelestialBody body = encounter.referenceBody;
            if (!body.atmosphere || encounter.eccentricity < 1.0)
            {
                why = "There is no open approach to the air.";
                return -1.0;
            }
            double periapsisUT = encounter.NextPeriapsisTime(encounter.StartUT);
            Vector3d direction = encounter.getRelativePositionAtUT(periapsisUT).normalized;
            Vector3d motion = encounter.getOrbitalVelocityAtUT(periapsisUT).normalized;
            double excess = -body.gravParameter / encounter.semiMajorAxis;
            return Search(vessel, body, height =>
                {
                    double radius = body.Radius + height;
                    double speed = Math.Sqrt(excess + 2.0 * body.gravParameter / radius);
                    var start = new Orbit();
                    start.UpdateFromStateVectors(direction * radius, motion * speed, body, periapsisUT);
                    // From a little above the air on the way in, where the periapsis is the deepest point.
                    double above = body.Radius + body.atmosphereDepth + ScaleHeight(body);
                    double before = Math.Abs(start.GetDTforTrueAnomaly(start.TrueAnomalyAtRadius(above), 0.0));
                    return Run(vessel, body, start, periapsisUT - before);
                },
                pass => pass.Skips, pass => landing ? !pass.Skips : pass.Captured, pass => false, out why);
        }

        // Halves the height between the top of the air and the ground. `tooShallow` says the pass does too little, `enough` that it is what is
        // asked, `partly` that it is a pass worth taking where none is enough.
        private static double Search(Vessel vessel, CelestialBody body, Func<double, Pass> run, Func<Pass, bool> tooShallow, Func<Pass, bool> enough,
            Func<Pass, bool> partly, out string why)
        {
            why = null;
            double pressureLimit = Heat.MaxDynamicPressure / 1000.0;
            double geeLimit = GeeLimit(vessel);
            Func<Pass, bool> within = pass => pass.Pressure <= pressureLimit && pass.Gees <= geeLimit && pass.Heat <= HeatBound;
            double known = Precision * ScaleHeight(body);
            double shallow = body.atmosphereDepth, deep = 0.0;
            Pass top = run(shallow);
            if (enough(top) && within(top)) return shallow;
            for (int i = 0; i < MostHalvings && shallow - deep > known; i++)
            {
                double middle = 0.5 * (shallow + deep);
                if (tooShallow(run(middle))) shallow = middle;
                else deep = middle;
            }
            Pass atDeep = run(deep);
            if (enough(atDeep) && within(atDeep))
            {
                Debug.Log("[ReDefinition] Aerobrake: a pass at " + deep.ToString("0") + " m is " + atDeep.Pressure.ToString("0.0") + " kPa, " + atDeep.Gees.ToString("0.0") + " g, "
                          + (atDeep.Heat * 100.0).ToString("0") + " % heat by the bound.");
                return deep;
            }
            // No pass that does it is within the limits: the shallowest that still brakes loads the vessel least, and several of them in turn do it.
            double skipping = shallow, braking = deep;
            for (int i = 0; i < MostHalvings && skipping - braking > known; i++)
            {
                double middle = 0.5 * (skipping + braking);
                if (run(middle).Skips) skipping = middle;
                else braking = middle;
            }
            Pass atBraking = run(braking);
            Debug.Log("[ReDefinition] Aerobrake: the pass that does it, at " + deep.ToString("0") + " m, is " + atDeep.Pressure.ToString("0.0") + " kPa, " + atDeep.Gees.ToString("0.0")
                      + " g, " + (atDeep.Heat * 100.0).ToString("0") + " % heat by the bound; the shallowest that brakes, at " + braking.ToString("0") + " m, " + atBraking.Pressure.ToString("0.0")
                      + " kPa, " + atBraking.Gees.ToString("0.0") + " g, " + (atBraking.Heat * 100.0).ToString("0") + " %.");
            if (partly(atBraking) && within(atBraking))
            {
                Debug.Log("[ReDefinition] Aerobrake: no pass does it in one go within the limits; the shallowest that brakes, at " + braking.ToString("0") + " m, is "
                          + atBraking.Pressure.ToString("0.0") + " kPa, " + atBraking.Gees.ToString("0.0") + " g, " + (atBraking.Heat * 100.0).ToString("0") + " % heat by the bound.");
                return braking;
            }
            if (enough(atDeep))
                why = "The air of " + body.bodyName + " would load the vessel with " + atDeep.Pressure.ToString("0") + " kPa, " + atDeep.Gees.ToString("0") + " g and heat to "
                      + (atDeep.Heat * 100.0).ToString("0") + " % of a part's limit, over what it takes (" + pressureLimit.ToString("0") + " kPa, " + geeLimit.ToString("0")
                      + " g, " + (HeatBound * 100.0).ToString("0") + " %). Slow it before it goes in.";
            else
                why = "No pass through the air of " + body.bodyName + " does what is asked of it.";
            return -1.0;
        }

        // The g-force the weakest part of the vessel takes.
        private static double GeeLimit(Vessel vessel)
        {
            double lowest = double.MaxValue;
            foreach (Part part in vessel.parts)
                if (part.gTolerance > 0.0) lowest = Math.Min(lowest, part.gTolerance);
            return lowest;
        }

        // The height over which the air's pressure falls by a factor e, found from the body's own pressure curve.
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

        // The simulated pass of the vessel along the orbit, from the time given.
        private static Pass Run(Vessel vessel, CelestialBody body, Orbit orbit, double startUT)
        {
            ReentrySimulation.SimCurves curves = ReentrySimulation.SimCurves.Borrow(body);
            SimulatedVessel simulated = SimulatedVessel.Borrow(vessel, curves, startUT, -1);
            ReentrySimulation simulation = ReentrySimulation.Borrow(orbit, startUT, simulated, curves, null, 0.0, 0.0, 3.0, 0.0, false, 0.2, Time.fixedDeltaTime, 1.0, false);
            ReentrySimulation.Result result = null;
            try
            {
                result = simulation.RunSimulation();
                var pass = new Pass { Pressure = result.MaxDynamicPressurekPa, Gees = result.MaxDragGees, Apoapsis = double.MaxValue, Heat = Heat.PassShare(vessel, result.MaxHeatParameter) };
                if (result.Outcome == ReentrySimulation.Outcome.LANDED) pass.Landed = true;
                else if (result.Outcome == ReentrySimulation.Outcome.AEROBRAKED)
                {
                    Orbit end = result.EndOrbit();
                    if (end.eccentricity < 1.0)
                    {
                        pass.Captured = true;
                        pass.Apoapsis = end.ApA;
                    }
                }
                return pass;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ReDefinition] The pass through the air could not be simulated: " + e.Message);
                return new Pass { Apoapsis = double.MaxValue };
            }
            finally
            {
                simulation.Release();
                if (result != null) result.Release();
            }
        }
    }
}
