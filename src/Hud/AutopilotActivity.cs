using System;
using System.Reflection;
using ReDefinition.Autopilot.Flight;
using ReDefinition.Core;
using ReDefinition.Engineer;

namespace ReDefinition.Hud
{
    // One line for the autopilot button's tooltip: what the autopilot does now, where the rocket turns to and where time warp goes to.
    internal static class AutopilotActivity
    {
        // The current action, or an empty text where nothing is flown.
        internal static string Describe(Vessel vessel)
        {
            if (vessel == null) return "";
            try
            {
                string mechJeb = FromMechJeb(vessel);
                if (mechJeb.Length > 0) return mechJeb;
                RouteDriver driver = RouteDriver.Instance;
                if (driver != null && driver.Flying) return Trim(driver.Status);
            }
            catch (Exception)
            {
            }
            return "";
        }

        // What MechJeb's node executor, rendezvous, docking or warp is doing; empty where none of them is.
        private static string FromMechJeb(Vessel vessel)
        {
            if (!MechJebDriver.Available) return "";
            double now = Planetarium.GetUniversalTime();
            string state = MechJebDriver.State(vessel);
            string line = "";
            if (state != null && state != "IDLE") line = NodePhase(vessel, state, now);
            bool known = ForeignAssumptions.MechJebActivity.Holds;
            if (line.Length == 0 && known && MechJebDriver.RendezvousBusy(vessel)) line = StatusOf(vessel, "MechJebModuleRendezvousAutopilot", "Rendezvous");
            if (line.Length == 0 && known && MechJebDriver.DockingBusy(vessel)) line = StatusOf(vessel, "MechJebModuleDockingAutopilot", "Docking");
            if (!ForeignAssumptions.MechJebActivity.Holds) return line;
            string warp = WarpTarget(vessel, now);
            if (warp.Length > 0 && line.IndexOf("Warping", StringComparison.Ordinal) < 0) line = line.Length > 0 ? line + "; " + warp : warp;
            return line;
        }

        // The executor's phase for the first maneuver node.
        private static string NodePhase(Vessel vessel, string state, double now)
        {
            if (vessel.patchedConicSolver == null || vessel.patchedConicSolver.maneuverNodes.Count == 0) return "";
            ManeuverNode node = vessel.patchedConicSolver.maneuverNodes[0];
            string at = Countdown(node.UT - now);
            switch (state)
            {
                case "BURN":
                    return "Burning: " + node.GetBurnVector(vessel.orbit).magnitude.ToString("0") + " m/s left";
                case "LEAD":
                    return "Waiting for the burn at " + at + " (maneuver node)";
                case "ALIGNING":
                    return "Turning to the burn at " + at + " (maneuver node)";
                case "WARPALIGN":
                    return "Turning to the burn and warping at " + at + " (maneuver node)";
                case "INITIAL_WARP":
                case "WARPING":
                    return "Warping to the burn in " + at + " (maneuver node)";
                default:
                    return "";
            }
        }

        // The status text of a rendezvous or docking module, headed by the module's kind.
        private static string StatusOf(Vessel vessel, string module, string head)
        {
            object instance = MechJebDriver.ComputerModule(vessel, module);
            if (instance == null) return "";
            FieldInfo field = instance.GetType().GetField("status", TypeLookup.Any);
            string text = field != null ? field.GetValue(instance) as string : null;
            return string.IsNullOrEmpty(text) ? head : head + ": " + Trim(text);
        }

        // Where MechJeb's warp controller takes time warp to, or empty while it holds no target.
        private static string WarpTarget(Vessel vessel, double now)
        {
            object warp = MechJebDriver.Module(vessel, "Warp");
            PropertyInfo property = warp != null ? warp.GetType().GetProperty("warpToUT", TypeLookup.Any) : null;
            if (property == null || !(property.GetValue(warp, null) is double)) return "";
            double ut = (double)property.GetValue(warp, null);
            if (ut <= now) return "";
            return "Warping to " + Countdown(ut - now) + " (UT " + ut.ToString("0") + ")";
        }

        // A span as T-hh:mm:ss.
        internal static string Countdown(double seconds)
        {
            long total = (long)Math.Max(0.0, Math.Round(seconds));
            return "T-" + (total / 3600).ToString("00") + ":" + (total / 60 % 60).ToString("00") + ":" + (total % 60).ToString("00");
        }

        private static string Trim(string text)
        {
            return (text ?? "").Trim().TrimEnd('.');
        }
    }
}
