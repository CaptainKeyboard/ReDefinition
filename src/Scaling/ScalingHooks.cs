using System;
using System.Collections.Generic;
using System.Reflection;
using EditorGizmos;
using KSP.UI.Screens.Flight;
using KSP.UI.Screens.Mapview;
using ReDefinition.Core;
using UnityEngine;
using Vectrosity;

namespace ReDefinition.Scaling
{
    // What KSP sizes once, as it makes it, at a fixed number of pixels
    // (docs/development/ui-scaling.md; decompiled).
    // The editor's move and rotate gizmos have ScreenSpaceObjectScaling.screenSize,
    // from the prefab, over Screen.height, as the maneuver gizmo.
    // The orbit lines are 5 px (OrbitRendererBase.MakeLine), the target's orbit 5 px
    // (OrbitTargeter.DottedLineUpdate), and the patches PatchRendering.lineWidth
    // (MakeVector).
    // The portrait gallery's room comes from GetMaxGalleryCapacity, which divides the screen's
    // pixels by the portrait's width in canvas units, so at a UI scale above 1 it
    // counts room for more portraits than fit.
    // The map captions' bounds are textBounds, which MapNode.UpdateCaptionText keeps in the
    // caption's own units, and MoveAwayFromEachOthers adds them to its position on
    // the screen. A map node scaled by MapNodeScale draws them larger.
    //
    // A postfix on each sets them from what KSP made, times UiScaling.World.
    // The gallery's postfix uses the UI and crew scales it is drawn at.
    // No other mod sizes these (their sources were read).
    // KSPCommunityFixes patches the gizmos' undo and the portraits' enabling only, and Kopernicus the orbits' colours.
    internal static class ScalingHooks
    {
        // The id the postfixes are installed under.
        private const string HarmonyId = "ReDefinition.UiScaling";
        // KSP's orbit and target line widths (OrbitRendererBase.MakeLine,
        // OrbitTargeter.DottedLineUpdate).
        private const float OrbitLine = 5f;

        // The binding of every member looked up.
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // The patch's VectorLine, which PatchRendering keeps private.
        private static readonly FieldInfo PatchLine = typeof(PatchRendering).GetField("vectorLine", Any);
        // The gizmos' own ScreenSpaceObjectScaling, which they size by.
        private static readonly FieldInfo OffsetScaling = typeof(GizmoOffset).GetField("ssScaling", Any);
        private static readonly FieldInfo RotateScaling = typeof(GizmoRotate).GetField("ssScaling", Any);

        // Every method hooked here and the postfix on it, for the install and the
        // tests (ScalingHookTests).
        internal static List<KeyValuePair<MethodBase, string>> Targets()
        {
            return new List<KeyValuePair<MethodBase, string>>
            {
                Target(typeof(GizmoOffset).GetMethod("Attach", Any), nameof(GizmoPostfix)),
                Target(typeof(GizmoRotate).GetMethod("Attach", Any), nameof(GizmoPostfix)),
                Target(typeof(OrbitRendererBase).GetMethod("MakeLine", Any), nameof(OrbitLinePostfix)),
                // Declared on the targeter's nested Marker (decompiled).
                Target(typeof(OrbitTargeter.Marker).GetMethod("DottedLineUpdate", Any), nameof(TargetLinePostfix)),
                Target(typeof(PatchRendering).GetMethod("MakeVector", Any), nameof(PatchLinePostfix)),
                Target(typeof(KerbalPortraitGallery).GetMethod("GetMaxGalleryCapacity", Any), nameof(GalleryPostfix)),
                Target(typeof(MapNode).GetMethod("UpdateCaptionText", Any), nameof(CaptionPostfix)),
            };
        }

        // A method with the name of its postfix, for Targets.
        private static KeyValuePair<MethodBase, string> Target(MethodBase method, string postfix)
        {
            return new KeyValuePair<MethodBase, string>(method, postfix);
        }

        // Patches every target with its postfix; from ScalingAddon.
        internal static void Install()
        {
            List<HarmonyHook> hooks = new List<HarmonyHook>();
            foreach (KeyValuePair<MethodBase, string> target in Targets())
                hooks.Add(new HarmonyHook(target.Key, null, target.Value, null));
            HarmonyHooks.Install(HarmonyId, typeof(ScalingHooks), hooks);
            Debug.Log(Log.Tag + " UI scaling: the editor gizmos, the orbit lines, the map captions' room and the portrait"
                      + " gallery's room.");
        }

        // The gizmo's screen size, from its prefab, times the scale as the gizmo attaches.
        private static void GizmoPostfix(Component __result)
        {
            HarmonyHooks.Safely("scale-gizmo", "An editor gizmo could not be scaled", () =>
            {
                FieldInfo field = __result is GizmoOffset ? OffsetScaling : __result is GizmoRotate ? RotateScaling : null;
                ScreenSpaceObjectScaling scaling = field != null ? field.GetValue(__result) as ScreenSpaceObjectScaling : null;
                if (scaling != null) scaling.screenSize *= UiScaling.World;
            });
        }

        // The caption's bounds at the size its node is drawn.
        private static void CaptionPostfix(MapNode __instance, ref Bounds ___textBounds)
        {
            float scale = __instance.transform.localScale.x;
            if (Mathf.Abs(scale - 1f) < 0.001f) return;
            ___textBounds = new Bounds(___textBounds.center * scale, ___textBounds.size * scale);
        }

        // An orbit's line at KSP's 5 px times the scale, as it is made.
        private static void OrbitLinePostfix(ref VectorLine l)
        {
            if (l != null) l.lineWidth = OrbitLine * UiScaling.World;
        }

        // Called every frame; the line is made once.
        private static void TargetLinePostfix(ref VectorLine line)
        {
            if (line == null) return;
            float width = OrbitLine * UiScaling.World;
            if (Mathf.Abs(line.lineWidth - width) > 0.01f) line.lineWidth = width;
        }

        // A patch's line at its own width (PatchRendering.lineWidth) times the scale.
        private static void PatchLinePostfix(PatchRendering __instance)
        {
            HarmonyHooks.Safely("scale-patch-line", "An orbit patch's line could not be scaled", () =>
            {
                VectorLine line = PatchLine != null ? PatchLine.GetValue(__instance) as VectorLine : null;
                if (line != null) line.lineWidth = __instance.lineWidth * UiScaling.World;
            });
        }

        // The room in the screen's pixels, the portrait at the UI scale and the crew
        // element's scale (FlightUIModeController.SetUIElementScale, while element
        // scaling is on).
        private static void GalleryPostfix(float ___usableSpace, float portraitWidth, ref int __result)
        {
            float scale = GameSettings.UI_SCALE * (GameSettings.UIELEMENTSCALINGENABLED ? GameSettings.UI_SCALE_CREW : 1f);
            if (scale <= 0f || portraitWidth <= 0f) return;
            __result = Mathf.Max(1, Mathf.FloorToInt(___usableSpace / (portraitWidth * scale)));
        }
    }
}
