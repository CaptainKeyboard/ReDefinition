using System;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Autopilot.Flight
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

        // A module of the master core by its class's name, which the core's GetComputerModule finds; null where there is none.
        private static object ComputerModule(Vessel vessel, string name)
        {
            if (vessel == null || !Available) return null;
            try
            {
                object core = masterOf.Invoke(null, new object[] { vessel });
                MethodInfo get = core != null ? core.GetType().GetMethod("GetComputerModule", TypeLookup.Any, null, new[] { typeof(string) }, null) : null;
                return get != null ? get.Invoke(core, new object[] { name }) : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " MechJeb: no " + name + ": " + e.Message);
                return null;
            }
        }

        // The vessel or port MechJeb flies to: its target controller takes it, which is also KSP's target.
        private static bool SetTarget(Vessel vessel, ITargetable target)
        {
            object targeting = Module(vessel, "Target");
            MethodInfo set = targeting != null ? targeting.GetType().GetMethod("Set", TypeLookup.Any, null, new[] { typeof(ITargetable) }, null) : null;
            if (set == null) return false;
            set.Invoke(targeting, new object[] { target });
            return true;
        }

        // Hands the meeting with a vessel to MechJeb's rendezvous autopilot, which plans and flies its own burns and warps with the node executor's
        // autowarp. It ends by itself; whether it met the target is judged apart.
        internal static bool StartRendezvous(Vessel vessel, Vessel target)
        {
            if (!ForeignAssumptions.MechJebRendezvous.Holds) return false;
            object module = ComputerModule(vessel, "MechJebModuleRendezvousAutopilot");
            object executor = Executor(vessel);
            if (module == null || executor == null || !SetTarget(vessel, target)) return false;
            autowarpField.SetValue(executor, true);
            Use(module, true);
            return Enabled(module);
        }

        internal static bool RendezvousBusy(Vessel vessel)
        {
            return Enabled(ComputerModule(vessel, "MechJebModuleRendezvousAutopilot"));
        }

        internal static void StopRendezvous(Vessel vessel)
        {
            object module = ComputerModule(vessel, "MechJebModuleRendezvousAutopilot");
            if (module != null) Use(module, false);
        }

        // The distance MechJeb's rendezvous brings the vessel to, as it is set in the unit; its own default is a hundred metres.
        internal static double RendezvousDistance(Vessel vessel)
        {
            return EditableOf(ComputerModule(vessel, "MechJebModuleRendezvousAutopilot"), "desiredDistance", 100.0);
        }

        // How many orbits MechJeb's rendezvous may take to bring the vessel to the target, as it is set in the unit (its default is five).
        internal static double RendezvousOrbits(Vessel vessel)
        {
            return EditableOf(ComputerModule(vessel, "MechJebModuleRendezvousAutopilot"), "maxPhasingOrbits", 5.0);
        }

        // The speed MechJeb's docking approaches at, as it is set in the unit (its default is one metre a second).
        internal static double DockingSpeed(Vessel vessel)
        {
            return EditableOf(ComputerModule(vessel, "MechJebModuleDockingAutopilot"), "speedLimit", 1.0);
        }

        // The value of one of a module's editable numbers; the fallback where it is not there.
        private static double EditableOf(object module, string field, double fallback)
        {
            try
            {
                FieldInfo info = module != null ? module.GetType().GetField(field, TypeLookup.Any) : null;
                object editable = info != null ? info.GetValue(module) : null;
                PropertyInfo value = editable != null ? editable.GetType().GetProperty("Val") : null;
                return value != null ? (double)value.GetValue(editable, null) : fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        // How far the target is and how fast it moves against the vessel, as MechJeb's target controller measures them (between the reference
        // parts, which is what its rendezvous ends by); false where the controller does not say.
        internal static bool TargetRange(Vessel vessel, out double distance, out double speed)
        {
            distance = speed = 0.0;
            try
            {
                object targeting = Module(vessel, "Target");
                if (targeting == null) return false;
                Type type = targeting.GetType();
                distance = (double)type.GetProperty("Distance").GetValue(targeting, null);
                speed = ((Vector3d)type.GetProperty("RelativeVelocity").GetValue(targeting, null)).magnitude;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Hands the docking to MechJeb's docking autopilot: the vessel's own port made the one it is controlled from, the target's port the target.
        internal static bool StartDocking(Vessel vessel, Vessel targetVessel, uint targetPort)
        {
            if (!ForeignAssumptions.MechJebRendezvous.Holds) return false;
            object module = ComputerModule(vessel, "MechJebModuleDockingAutopilot");
            ModuleDockingNode target = null;
            ModuleDockingNode own = null;
            foreach (Part part in targetVessel.parts)
                if (part.persistentId == targetPort) target = part.FindModuleImplementing<ModuleDockingNode>();
            if (module == null || target == null || target.state != "Ready") return false;
            // The port of the vessel's own that suits the target's and lies nearest to it.
            double nearest = double.MaxValue;
            foreach (Part part in vessel.parts)
            {
                ModuleDockingNode port = part.FindModuleImplementing<ModuleDockingNode>();
                if (port == null || port.state != "Ready" || port.nodeType != target.nodeType || (port.gendered && port.genderFemale == target.genderFemale)) continue;
                double distance = (part.transform.position - target.part.transform.position).magnitude;
                if (distance >= nearest) continue;
                nearest = distance;
                own = port;
            }
            if (own == null || !HasRcs(vessel)) return false;
            own.MakeReferenceTransform();
            if (!SetTarget(vessel, target)) return false;
            Use(module, true);
            return Enabled(module);
        }

        // Whether the vessel has an RCS thruster with propellant: MechJeb's docking moves the vessel by it alone.
        private static bool HasRcs(Vessel vessel)
        {
            foreach (Part part in vessel.parts)
                foreach (ModuleRCS rcs in part.FindModulesImplementing<ModuleRCS>())
                    if (rcs.rcsEnabled && !rcs.flameout && rcs.isEnabled) return true;
            return false;
        }

        internal static bool DockingBusy(Vessel vessel)
        {
            return Enabled(ComputerModule(vessel, "MechJebModuleDockingAutopilot"));
        }

        internal static void StopDocking(Vessel vessel)
        {
            object module = ComputerModule(vessel, "MechJebModuleDockingAutopilot");
            if (module != null) Use(module, false);
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
            // MechJeb's landing warps through the coast to the brake only where its node executor's autowarp is on, which the burns set.
            object executor = Executor(vessel);
            if (executor != null) autowarpField.SetValue(executor, true);
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

        // What MechJeb's landing is doing: the step it is in and the status that step gives, for the log.
        internal static string LandingStatus(Vessel vessel)
        {
            object landing = Module(vessel, "Landing");
            if (landing == null) return "no landing module";
            object step = landing.GetType().GetProperty("CurrentStep").GetValue(landing, null);
            object status = landing.GetType().GetProperty("Status").GetValue(landing, null);
            return (step != null ? step.GetType().Name : "no step") + ", \"" + status + "\"";
        }

        internal static void StopLanding(Vessel vessel)
        {
            object landing = Module(vessel, "Landing");
            if (landing != null) landing.GetType().GetMethod("StopLanding").Invoke(landing, null);
        }
    }
}
