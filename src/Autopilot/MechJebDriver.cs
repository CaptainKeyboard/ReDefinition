using System;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Autopilot
{
    // MechJeb as the hand that flies the nodes: its node executor aligns, warps to the burn, burns and stops. Reached through the vessel's master
    // core and reflection, behind the MechJebAutopilot assumption; without MechJeb, or with a build that does not match, nothing here is available.
    internal static class MechJebDriver
    {
        // The user MechJeb's module pools list us under: its autopilots stop when their last user leaves.
        private static readonly object user = new object();

        private static MethodInfo masterOf;
        private static FieldInfo nodeField;
        private static FieldInfo stateField;
        private static FieldInfo autowarpField;

        // Whether MechJeb is installed and is the build the driver was checked against.
        internal static bool Available
        {
            get
            {
                if (!ForeignAssumptions.MechJebAutopilot.Holds) return false;
                if (masterOf != null) return true;
                Type extensions = TypeLookup.Find("MuMech.VesselExtensions");
                Type core = TypeLookup.Find("MuMech.MechJebCore");
                Type executor = TypeLookup.Find("MuMech.MechJebModuleNodeExecutor");
                if (extensions == null || core == null || executor == null) return false;
                masterOf = extensions.GetMethod("GetMasterMechJeb", TypeLookup.Any, null, new[] { typeof(Vessel) }, null);
                nodeField = core.GetField("Node", TypeLookup.Any);
                stateField = executor.GetField("State", TypeLookup.Any);
                autowarpField = executor.GetField("Autowarp", TypeLookup.Any);
                return masterOf != null && nodeField != null && stateField != null && autowarpField != null;
            }
        }

        // Why the autopilot is not available, for the window's tooltip; null where it is.
        internal static string Unavailable
        {
            get
            {
                if (TypeLookup.Find("MuMech.MechJebCore") == null) return "MechJeb is not installed.";
                if (!Available) return "This MechJeb is not a build the autopilot was checked against.";
                return null;
            }
        }

        // The vessel's node executor, or null where it has no MechJeb unit.
        private static object Executor(Vessel vessel)
        {
            if (vessel == null || !Available) return null;
            try
            {
                object core = masterOf.Invoke(null, new object[] { vessel });
                return core != null ? nodeField.GetValue(core) : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " MechJeb: no node executor: " + e.Message);
                return null;
            }
        }

        // Whether the vessel has a MechJeb unit that can fly nodes.
        internal static bool CanFly(Vessel vessel)
        {
            return Executor(vessel) != null;
        }

        // Hands the first node to MechJeb: it aligns, warps and burns, and stops when the node is done.
        internal static bool ExecuteNode(Vessel vessel, bool warp)
        {
            object executor = Executor(vessel);
            if (executor == null || vessel.patchedConicSolver == null || vessel.patchedConicSolver.maneuverNodes.Count == 0) return false;
            autowarpField.SetValue(executor, warp);
            executor.GetType().GetMethod("ExecuteOneNode").Invoke(executor, new[] { user });
            return true;
        }

        // Takes the node away from MechJeb at once: the engines off, the warp down.
        internal static void Abort(Vessel vessel)
        {
            object executor = Executor(vessel);
            if (executor != null) executor.GetType().GetMethod("Abort").Invoke(executor, null);
        }

        // What the executor is doing: IDLE when it is not flying a node, else INITIAL_WARP, ALIGNING, WARPING, LEAD or BURN; null with no MechJeb.
        internal static string State(Vessel vessel)
        {
            object executor = Executor(vessel);
            return executor != null ? stateField.GetValue(executor).ToString() : null;
        }

        // Whether MechJeb is flying a node now.
        internal static bool Busy(Vessel vessel)
        {
            string state = State(vessel);
            return state != null && state != "IDLE";
        }

        // A module of the vessel's MechJeb unit by the name of the core's field, or null.
        private static object Module(Vessel vessel, string field)
        {
            if (vessel == null || !Available) return null;
            try
            {
                object core = masterOf.Invoke(null, new object[] { vessel });
                FieldInfo info = core != null ? core.GetType().GetField(field, TypeLookup.Any) : null;
                return info != null ? info.GetValue(core) : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " MechJeb: no " + field + ": " + e.Message);
                return null;
            }
        }

        // Makes us a user of the module, which switches it on; and takes us off its users again, which switches it off where we were the last.
        private static void Use(object module, bool on)
        {
            object pool = module.GetType().GetField("Users", TypeLookup.Any).GetValue(module);
            MethodInfo method = pool.GetType().GetMethod(on ? "Add" : "Remove", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, new[] { typeof(object) }, null);
            method.Invoke(pool, new[] { user });
        }

        private static bool Enabled(object module)
        {
            return module != null && (bool)module.GetType().GetProperty("Enabled", TypeLookup.Any).GetValue(module, null);
        }

        // Hands the launch to MechJeb's ascent autopilot: up from the pad or the air into a circular orbit at `altitude` metres, the staging its own.
        internal static bool StartAscent(Vessel vessel, double altitude, double inclination)
        {
            object settings = Module(vessel, "AscentSettings");
            if (settings == null) return false;
            Type type = settings.GetType();
            SetValue(type.GetField("DesiredOrbitAltitude").GetValue(settings), altitude);
            SetValue(type.GetField("DesiredInclination").GetValue(settings), inclination);
            object ascent = type.GetProperty("AscentAutopilot").GetValue(settings, null);
            if (ascent == null) return false;
            Use(ascent, true);
            return true;
        }

        // Whether the ascent autopilot is flying.
        internal static bool AscentBusy(Vessel vessel)
        {
            object settings = Module(vessel, "AscentSettings");
            return settings != null && Enabled(settings.GetType().GetProperty("AscentAutopilot").GetValue(settings, null));
        }

        internal static void StopAscent(Vessel vessel)
        {
            object settings = Module(vessel, "AscentSettings");
            object ascent = settings != null ? settings.GetType().GetProperty("AscentAutopilot").GetValue(settings, null) : null;
            if (ascent != null) Use(ascent, false);
        }

        // The value of one of MechJeb's editable numbers, in its base units.
        private static void SetValue(object editable, double value)
        {
            editable.GetType().GetProperty("Val").SetValue(editable, value, null);
        }

        // Hands the descent to MechJeb's landing autopilot: the deorbit, the burn that brakes, the legs and chutes, and the touchdown, anywhere.
        internal static bool StartLanding(Vessel vessel)
        {
            object landing = Module(vessel, "Landing");
            if (landing == null) return false;
            landing.GetType().GetMethod("LandUntargeted").Invoke(landing, new[] { user });
            return true;
        }

        // MechJeb's own numbers in its landing, for the probes that measure it: the speed limit of its descent policy, the height its brake ends
        // at, the acceleration it counts on, and the policy's name. False where its landing is not flying.
        internal static bool LandingNumbers(Vessel vessel, out double maxAllowed, out double brakeEnd, out double acceleration, out string policy, out string limiter)
        {
            maxAllowed = brakeEnd = acceleration = 0.0;
            policy = "";
            limiter = "";
            object landing = Module(vessel, "Landing");
            object core = null;
            try
            {
                core = masterOf.Invoke(null, new object[] { vessel });
                if (landing == null || core == null) return false;
                object policyObject = landing.GetType().GetField("DescentSpeedPolicy").GetValue(landing);
                if (policyObject == null) return false;
                policy = policyObject.GetType().Name;
                maxAllowed = (double)landing.GetType().GetMethod("MaxAllowedSpeed").Invoke(landing, null);
                brakeEnd = (double)landing.GetType().GetMethod("DecelerationEndAltitude").Invoke(landing, null);
                object state = core.GetType().GetField("VesselState").GetValue(core);
                acceleration = (double)state.GetType().GetProperty("LimitedMaxThrustAcceleration").GetValue(state, null);
                object thrust = core.GetType().GetField("Thrust").GetValue(core);
                float throttleLimit = (float)thrust.GetType().GetProperty("ThrottleLimit").GetValue(thrust, null);
                limiter = thrust.GetType().GetField("Limiter").GetValue(thrust) + ":" + throttleLimit.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool LandingBusy(Vessel vessel)
        {
            return Enabled(Module(vessel, "Landing"));
        }

        internal static void StopLanding(Vessel vessel)
        {
            object landing = Module(vessel, "Landing");
            if (landing != null) landing.GetType().GetMethod("StopLanding").Invoke(landing, null);
        }
    }
}
