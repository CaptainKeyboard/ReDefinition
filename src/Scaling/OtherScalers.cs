using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Scaling
{
    // Other mods that scale what ReDefinition scales, their scaling turned off so
    // that ReDefinition's applies alone and nothing is scaled twice.
    // The rest of each mod stays as it is (their sources were read).
    // Of 4kSP and 4kSP Expanded, FourkSP.UpdateNodes sizes the map's icons and
    // captions, and its toolbar button has a window that sets only those sizes.
    // Of 4kSP Expanded's R&D part, its bootstrap's Start patches the R&D part
    // list, and RDSceneScaler.Start hooks the R&D screen's opening.
    // Of 4kSP Expanded's window part, its bootstrap's Start patches the IMGUI
    // window methods.
    // Maneuver Node Evolved is handled in OtherScalers.KeepManeuverGizmo.
    // Those Starts run in the main menu or later, after this is installed as the
    // game starts.
    // Each is patched only where its mod is installed.
    // 4kSP's R&D and window parts are skipped only where ReDefinition's own scaling of the same is in
    // (and, for the windows, switched on as the bootstrap starts). Otherwise they scale as the mod means them to.
    internal static class OtherScalers
    {
        // The Harmony id the patches are installed under (one suffix per mod), the type of 4kSP Expanded's R&D
        // screen hook, and the binding of every method looked up.
        private const string HarmonyId = "ReDefinition.UiScaling.OtherScalers";
        private const string FourkSpRnDScene = "_4kSP_RnD.RDSceneScaler";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // Turns off the scaling of each of those mods that is installed; from ScalingAddon as the game starts.
        internal static void Install()
        {
            // The map's icons and captions are scaled by MapNodeScale, an addon of each map
            // scene that needs no hook, so 4kSP's sizing of them is skipped whatever was
            // installed: run beside it, it would size the icons a second time. Only the
            // captions' room needs ScalingHooks.
            Guarded("4kSP", () => Skip("4kSP", UiScaling.FourkSpMap, () => true, "UpdateNodes", "AddToolbarButton"));
            Guarded("4kSP's R&D part", () => Skip("4kSP's R&D part", UiScaling.FourkSpRnD, () => RnDNodeHooks.Installed, "Start"));
            Guarded("4kSP's R&D part", () => Skip("4kSP's R&D part", FourkSpRnDScene, () => RnDNodeHooks.Installed, "Start"));
            Guarded("4kSP's window part", () => Skip("4kSP's window part", UiScaling.FourkSpWindows,
                                                       () => ModWindowScale.Installed && UiScaling.ModWindows, "Start"));
            Guarded("Maneuver Node Evolved", KeepManeuverGizmo);
        }

        // Runs one installer; a failure is logged as a warning, and the other mods are still handled.
        private static void Guarded(string mod, System.Action install)
        {
            try
            {
                install();
            }
            catch (System.Exception e)
            {
                CompatibilityLog.Warn("other-scalers", mod + "'s scaling could not be turned off, so it scales on top of "
                                                       + "ReDefinition's (" + CompatibilityLog.Reason(e) + ").");
            }
        }

        // Redirects Maneuver Node Evolved's writes to the gizmo's screenSize to Store (KeepScreenSize).
        // The writes are in ManeuverController.LateUpdate and gizmoSpawn.
        // Store keeps ReDefinition's size and only reopens a gizmo KSP collapsed, where its "Force Maneuver Node Open" asks for that.
        // The mod's snapping, input and other windows stay.
        private static void KeepManeuverGizmo()
        {
            System.Type controller = TypeLookup.Find(UiScaling.ManeuverNodeEvolved);
            if (controller == null) return;
            List<HarmonyHook> hooks = new List<HarmonyHook>();
            foreach (string name in new[] { "LateUpdate", "gizmoSpawn" })
            {
                MethodInfo method = controller.GetMethod(name, Any);
                if (method != null) hooks.Add(new HarmonyHook(method, null, null, null, -1, -1, nameof(KeepScreenSize)));
                else Missing("Maneuver Node Evolved", name);
            }
            if (hooks.Count == 0) return;
            replaced = 0;
            HarmonyHooks.Install(HarmonyId + ".ManeuverNodeEvolved", typeof(OtherScalers), hooks);
            if (replaced == 0)
            {
                CompatibilityLog.Warn("other-scalers", "Maneuver Node Evolved: no write to the gizmo's size found, so its "
                                                       + "scaling may apply on top of ReDefinition's.");
                return;
            }
            Debug.Log(Log.Tag + " UI scaling: Maneuver Node Evolved's gizmo scaling turned off (" + replaced
                      + " writes); ReDefinition scales the gizmo.");
        }

        // Warns that a method to turn off was not found in the installed mod.
        private static void Missing(string mod, string name)
        {
            CompatibilityLog.Warn("other-scalers", mod + ": " + name + " not found, so what it scales may apply on top of "
                                                   + "ReDefinition's.");
        }

        // The methods named on the mod's type skipped, where the mod is installed.
        // By method skipped: whether ReDefinition's own scaling of the same runs.
        private static readonly Dictionary<MethodBase, System.Func<bool>> skipWhen = new Dictionary<MethodBase, System.Func<bool>>();

        // Patches the named methods of `type` to be skipped while `oursRuns`; nothing where the mod is not installed.
        private static void Skip(string mod, string type, System.Func<bool> oursRuns, params string[] names)
        {
            System.Type found = TypeLookup.Find(type);
            if (found == null) return;
            List<HarmonyHook> hooks = new List<HarmonyHook>();
            List<string> skipped = new List<string>();
            foreach (string name in names)
            {
                MethodInfo method = found.GetMethod(name, Any);
                if (method == null)
                {
                    Missing(mod, type + "." + name);
                    continue;
                }
                skipWhen[method] = oursRuns;
                hooks.Add(new HarmonyHook(method, nameof(SkipOriginal), null, null));
                skipped.Add(name);
            }
            if (hooks.Count == 0) return;
            HarmonyHooks.Install(HarmonyId + "." + type, typeof(OtherScalers), hooks);
            Debug.Log(Log.Tag + " UI scaling: " + mod + "'s " + string.Join(", ", skipped.ToArray()) + " turned off; ReDefinition scales that.");
        }

        // Harmony prefix of every method Skip patched: false, skipping the original, while ReDefinition's scaling runs.
        private static bool SkipOriginal(MethodBase __originalMethod)
        {
            System.Func<bool> oursRuns;
            try
            {
                // A method not in the table is not one skipped here: it runs.
                if (__originalMethod == null || !skipWhen.TryGetValue(__originalMethod, out oursRuns)) return true;
                return !oursRuns();
            }
            catch (System.Exception)
            {
                return true;
            }
        }

        // How many stores to screenSize KeepScreenSize redirected, counted for the log.
        private static int replaced;

        // Every store to ManeuverGizmoBase.screenSize a call to Store, which takes
        // the same two operands.
        private static IEnumerable<CodeInstruction> KeepScreenSize(IEnumerable<CodeInstruction> instructions)
        {
            FieldInfo screenSize = typeof(ManeuverGizmoBase).GetField("screenSize", Any);
            MethodInfo store = typeof(OtherScalers).GetMethod(nameof(Store), BindingFlags.Static | BindingFlags.NonPublic);
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, screenSize))
                {
                    CodeInstruction call = new CodeInstruction(OpCodes.Call, store);
                    call.labels.AddRange(instruction.labels);
                    call.blocks.AddRange(instruction.blocks);
                    replaced++;
                    yield return call;
                    continue;
                }
                yield return instruction;
            }
        }

        // Maneuver Node Evolved's size for the gizmo: ignored while the gizmo is
        // open; where KSP collapsed it (screenSize 0 on a click beside it) and the
        // mod opens it again, it opens at ReDefinition's size.
        private static void Store(ManeuverGizmoBase gizmo, float size)
        {
            if (gizmo == null || gizmo.screenSize > 0f || size <= 0f) return;
            float open = ManeuverGizmos.OpenSize(gizmo);
            if (open > 0f) gizmo.screenSize = open;
        }
    }
}
