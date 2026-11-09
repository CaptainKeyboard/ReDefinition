#if REDEFINITION_PLANNER_PROBES
using System.Collections;
using ReDefinition.Planner.Autopilot.Flight;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Probe
{
    // The checks of a probe's vessel against the plan the probe flies, made before the probe waits for anything.
    // A vessel that cannot do what the plan asks fails the probe in seconds, with the numbers.
    internal static partial class LightProbe
    {
        // The slowest acceleration a burn of the plan may have: a hundred metres a second then take at most 200 seconds.
        private const double LeastAcceleration = 0.5;
        // Seconds of BURN with no engine thrust that fail a probe.
        private const float BurnWithoutThrust = 30f;

        // The engines' thrust now in kilonewtons.
        private static double EngineThrust(Vessel vessel)
        {
            double sum = 0.0;
            foreach (Part part in vessel.parts)
                foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
                    sum += engine.finalThrust;
            return sum;
        }

        // The engines' thrust at full throttle, those that are on and have propellant, in kilonewtons.
        private static double EngineThrustAvailable(Vessel vessel)
        {
            double sum = 0.0;
            foreach (Part part in vessel.parts)
                foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
                    if (engine.isEnabled && !engine.flameout && HasPropellant(engine)) sum += engine.maxThrust * engine.thrustPercentage / 100.0;
            return sum;
        }

        // Whether every propellant of the engine is in the tanks that feed it.
        private static bool HasPropellant(ModuleEngines engine)
        {
            foreach (Propellant propellant in engine.propellants)
            {
                double amount, capacity;
                engine.part.GetConnectedResourceTotals(propellant.id, propellant.GetFlowMode(), out amount, out capacity, true);
                if (amount <= 0.0) return false;
            }
            return true;
        }

        // The RCS thrust along each of the vessel's axes in kilonewtons, as the thrusters point: a thruster counts for the axes it pushes along.
        private static Vector3 RcsThrustAxes(Vessel vessel)
        {
            Transform frame = vessel.ReferenceTransform != null ? vessel.ReferenceTransform : vessel.transform;
            Vector3 sum = Vector3.zero;
            foreach (Part part in vessel.parts)
                foreach (ModuleRCS rcs in part.FindModulesImplementing<ModuleRCS>())
                {
                    if (rcs.thrusterTransforms == null) continue;
                    foreach (Transform thruster in rcs.thrusterTransforms)
                    {
                        Vector3 direction = frame.InverseTransformDirection(thruster.up);
                        sum += rcs.thrusterPower * new Vector3(Mathf.Abs(direction.x), Mathf.Abs(direction.y), Mathf.Abs(direction.z));
                    }
                }
            return sum;
        }

        // The checks, by what the plan needs; a failed one ends the probe at once. The numbers are logged either way.
        private static bool Preflight(string probe, Vessel vessel, bool burns, bool docks, bool drives)
        {
            double mass = vessel.GetTotalMass();
            double thrust = EngineThrustAvailable(vessel);
            double acceleration = mass > 0.0 ? thrust / mass : 0.0;
            Vector3 rcs = RcsThrustAxes(vessel);
            int freePorts = 0;
            foreach (Part part in vessel.parts)
            {
                ModuleDockingNode port = part.FindModuleImplementing<ModuleDockingNode>();
                if (port != null && port.state == "Ready") freePorts++;
            }
            int motors = vessel.FindPartModulesImplementing<ModuleWheels.ModuleWheelMotor>().Count;
            Debug.Log(Log.Tag + " Probe: " + probe + ": preflight: " + vessel.vesselName + ", " + mass.ToString("0.0") + " t, engines " + thrust.ToString("0.0") + " kN = "
                      + acceleration.ToString("0.00") + " m/s2, RCS " + rcs.x.ToString("0.0") + "/" + rcs.y.ToString("0.0") + "/" + rcs.z.ToString("0.0") + " kN, free ports " + freePorts
                      + ", wheel motors " + motors + ".");
            if (burns && acceleration < LeastAcceleration)
            {
                ProbeSteps.Failed(probe + ": the vessel cannot burn: its engines give " + acceleration.ToString("0.00") + " m/s2 (at least " + LeastAcceleration + " needed).");
                return false;
            }
            if (docks && (rcs.x < 0.1f || rcs.y < 0.1f || rcs.z < 0.1f))
            {
                ProbeSteps.Failed(probe + ": the vessel cannot dock: RCS thrust " + rcs.x.ToString("0.0") + "/" + rcs.y.ToString("0.0") + "/" + rcs.z.ToString("0.0") + " kN on its three axes.");
                return false;
            }
            if (docks && freePorts == 0)
            {
                ProbeSteps.Failed(probe + ": the vessel cannot dock: it has no free docking port.");
                return false;
            }
            if (drives && motors == 0)
            {
                ProbeSteps.Failed(probe + ": the vessel cannot drive: it has no wheel motor.");
                return false;
            }
            return true;
        }

        // Called each round of a probe's wait: MechJeb burning for half a minute with no engine thrust ends the probe.
        private static bool BurnStalled(string probe, Vessel vessel, ref float since)
        {
            if (MechJebDriver.State(vessel) != "BURN" || EngineThrust(vessel) > 0.01)
            {
                since = -1f;
                return false;
            }
            if (since < 0f) since = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - since < BurnWithoutThrust) return false;
            ProbeSteps.Failed(probe + ": MechJeb burns, and the engines give no thrust.");
            return true;
        }
    }
}
#endif
