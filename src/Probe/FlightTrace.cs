using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using ReDefinition.Autopilot.Flight;
using ReDefinition.Autopilot.Guidance;

namespace ReDefinition.Probe
{
    // The flight of the active vessel written to a file, a line twice a second: what a pilot, or MechJeb, does in a descent, to be read afterwards. The
    // line has the time, the height over the ground and over the sea, the speeds, the throttle and the thrust that came of it, the mass, and how far the
    // nose is from pointing against the velocity. The probes that fly with MechJeb run it, so that its landing is measured and not guessed at.
    internal sealed class FlightTrace : MonoBehaviour
    {
        private const float Gap = 0.5f;

        private static bool begun;

        private string path;
        private float next;

        // Starts the trace into the file, with its head line.
        internal static void Begin(string file)
        {
            if (begun) return;
            begun = true;
            var host = new GameObject("ReDefinitionFlightTrace");
            DontDestroyOnLoad(host);
            FlightTrace trace = host.AddComponent<FlightTrace>();
            trace.path = file;
            File.WriteAllText(file, "ut,met,situation,height,asl,speed,vertical,sideways,throttle,thrust,mass,nose_off,warp,mj_limit,mj_brake_end,mj_accel,mj_policy,mj_limiter,asked,guidance\n");
        }

        // The first words of what the guidance says now, without the commas, to tell the step it is in.
        private static string Guidance(Vessel vessel)
        {
            string text = GuidanceBoard.Current(vessel) ? GuidanceBoard.Line : "";
            if (text == null) return "";
            return (text.Length > 40 ? text.Substring(0, 40) : text).Replace(',', ';');
        }

        private void FixedUpdate()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (path == null || vessel == null || Time.realtimeSinceStartup < next) return;
            next = Time.realtimeSinceStartup + Gap;
            CelestialBody body = vessel.mainBody;
            Vector3d up = (vessel.GetWorldPos3D() - body.position).normalized;
            Vector3d velocity = vessel.srf_velocity;
            double vertical = Vector3d.Dot(velocity, up);
            double sideways = (velocity - up * vertical).magnitude;
            double thrust = 0.0;
            foreach (Part part in vessel.parts)
                foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
                    thrust += engine.finalThrust;
            double noseOff = velocity.magnitude > 1.0 ? Vector3d.Angle(vessel.GetTransform().up, -velocity) : 0.0;
            var line = new StringBuilder();
            var culture = CultureInfo.InvariantCulture;
            line.Append(Planetarium.GetUniversalTime().ToString("0.0", culture)).Append(',')
                .Append(vessel.missionTime.ToString("0.0", culture)).Append(',')
                .Append(vessel.situation).Append(',')
                .Append((vessel.altitude - System.Math.Max(0.0, vessel.terrainAltitude)).ToString("0.0", culture)).Append(',')
                .Append(vessel.altitude.ToString("0.0", culture)).Append(',')
                .Append(velocity.magnitude.ToString("0.00", culture)).Append(',')
                .Append(vertical.ToString("0.00", culture)).Append(',')
                .Append(sideways.ToString("0.00", culture)).Append(',')
                .Append(vessel.ctrlState.mainThrottle.ToString("0.000", culture)).Append(',')
                .Append(thrust.ToString("0.0", culture)).Append(',')
                .Append(vessel.GetTotalMass().ToString("0.000", culture)).Append(',')
                .Append(noseOff.ToString("0.0", culture)).Append(',')
                .Append(TimeWarp.CurrentRateIndex).Append(',');
            double limit, brakeEnd, accel;
            string policy, limiter;
            bool known = MechJebDriver.LandingNumbers(vessel, out limit, out brakeEnd, out accel, out policy, out limiter);
            line.Append(known ? limit.ToString("0.0", culture) : "").Append(',').Append(known ? brakeEnd.ToString("0.0", culture) : "").Append(',')
                .Append(known ? accel.ToString("0.00", culture) : "").Append(',').Append(policy).Append(',').Append(limiter).Append(',')
                .Append(ReDefinition.Engineer.RouteDriver.Instance != null ? ReDefinition.Engineer.RouteDriver.Instance.GuidedThrottle.ToString("0.000", culture) : "").Append(',')
                .Append(Guidance(vessel)).Append('\n');
            File.AppendAllText(path, line.ToString());
        }
    }
}
