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
    }
}
