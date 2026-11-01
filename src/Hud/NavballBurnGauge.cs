using System;
using System.Collections.Generic;
using System.Reflection;
using KSP.UI;
using KSP.UI.Screens;
using KSP.UI.Screens.Flight;
using ReDefinition.Autopilot.Guidance;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Hud
{
    // The gauge right of the navball that the game shows for a maneuver burn shows the navigation's bar in a climb or a descent, as the round gauge does.
    // A mark in the middle stands for the speed the guidance asks for. The bar stands over it where the throttle is to go up and under it where it is to go down.
    // The bar turns amber and red as the vessel gets far from the speed. With a node on the vessel the game's own burn is shown.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class NavballBurnGauge : MonoBehaviour
    {
        private const string HarmonyId = "ReDefinition.NavballBurnGauge";
        private static bool hooked;
        private static bool reported;
        private static bool failed;
        private static RotationalGaugeOffsetMarker mark;
        // The colour the game gave the bar, put back when the navigation's bar is gone.
        private static UnityEngine.UI.Image tinted;
        private static Color untinted;
        private static readonly Color Fine = new Color(0.95f, 0.95f, 0.95f);
        private static readonly Color Close = new Color(1f, 0.72f, 0.2f);
        private static readonly Color Past = new Color(1f, 0.32f, 0.28f);
        private static readonly FieldInfo PrefabField = typeof(NavBallBurnVector).GetField("stageMarkerPrefab", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MaskField = typeof(NavBallBurnVector).GetField("stageMarkerMaskRect", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo OffsetField = typeof(RotationalGaugeOffsetMarker).GetField("offsetObject", BindingFlags.Instance | BindingFlags.NonPublic);

        private void Start()
        {
            failed = false;
            if (hooked) return;
            hooked = true;
            HarmonyHooks.Safely("navball-burn-gauge", "The burn gauge cannot show the navigation's bar", () =>
                HarmonyHooks.Install(HarmonyId, typeof(NavballBurnGauge), new List<HarmonyHook>
                {
                    new HarmonyHook(typeof(NavBallBurnVector).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic), null, nameof(Drive), null),
                }));
        }

        private void OnDestroy()
        {
            Hide();
        }

        // After the game has updated the gauge, which it hides without a node: the bar is set and the gauge shown.
        private static void Drive(NavBallBurnVector __instance, PatchedConicSolver ___solver)
        {
            if (failed) return;
            try
            {
                Vessel vessel = FlightGlobals.ActiveVessel;
                bool show = FlightGlobals.ready && vessel != null && GuidanceBoard.Current(vessel) && GuidanceBoard.HasBar
                            && (___solver == null || ___solver.maneuverNodes.Count == 0) && Expanded(__instance);
                if (!show)
                {
                    Hide();
                    return;
                }
                KSP.UI.Screens.HelixGauge gauge = __instance.deltaVGauge;
                if (!gauge.gameObject.activeSelf) gauge.gameObject.SetActive(true);
                gauge.MinValue = 0f;
                gauge.MaxValue = 1f;
                gauge.currentValue = (float)Math.Max(0.0, Math.Min(1.0, GuidanceBoard.BarFill));
                if (gauge.tgtImage != null)
                {
                    if (tinted == null)
                    {
                        tinted = gauge.tgtImage;
                        untinted = tinted.color;
                    }
                    tinted.color = GuidanceBoard.BarLevel >= 2 ? Past : GuidanceBoard.BarLevel == 1 ? Close : Fine;
                }
                Mark(__instance, gauge);
                Report(gauge);
            }
            catch (Exception e)
            {
                failed = true;
                Debug.LogWarning(Log.Tag + " The burn gauge cannot show the navigation's bar: " + e);
                Hide();
            }
        }

        // Whether the navball is open: the game shows its burn gauge only then.
        private static bool Expanded(NavBallBurnVector vector)
        {
            UIPanelTransitionToggle[] groups = vector.navBallCollapseGroups;
            if (groups == null) return false;
            foreach (UIPanelTransitionToggle group in groups)
                if (group != null && !group.expanded) return false;
            return true;
        }

        // The mark in the middle, made as the game makes the marks of its stages: a copy of their prefab on the gauge.
        private static void Mark(NavBallBurnVector vector, KSP.UI.Screens.HelixGauge gauge)
        {
            if (mark == null)
            {
                var prefab = PrefabField.GetValue(vector) as GameObject;
                if (prefab == null || vector.StageMarkersParent == null) return;
                mark = UnityEngine.Object.Instantiate(prefab).GetComponent<RotationalGaugeOffsetMarker>();
                if (mark == null) return;
                mark.transform.SetParent(vector.StageMarkersParent);
                mark.transform.localPosition = Vector3.zero;
                mark.transform.localScale = Vector3.one;
                var offset = OffsetField.GetValue(mark) as GameObject;
                if (offset != null)
                {
                    offset.transform.SetParent(vector.StageMarkersParent);
                    offset.transform.localPosition = Vector3.zero;
                    offset.transform.localScale = Vector3.one;
                }
                var soft = mark.GetComponent<SoftMasking.SoftMask>();
                if (soft != null) soft.separateMask = MaskField.GetValue(vector) as RectTransform;
                mark.responsiveness = 1000f;
                mark.SetTextField("", 0);
                mark.ToggleOffsetMarker(true);
            }
            mark.maxValue = gauge.MaxValue;
            mark.SetValue(0.5f);
        }

        private static void Hide()
        {
            if (tinted != null)
            {
                tinted.color = untinted;
                tinted = null;
            }
            if (mark == null) return;
            var offset = OffsetField.GetValue(mark) as GameObject;
            if (offset != null) UnityEngine.Object.Destroy(offset);
            UnityEngine.Object.Destroy(mark.gameObject);
            mark = null;
        }

        // What the gauge's prefab is made like, once: the fill and the angles the bar and the mark move over.
        private static void Report(KSP.UI.Screens.HelixGauge gauge)
        {
            if (reported) return;
            reported = true;
            Debug.Log(Log.Tag + " The burn gauge shows the navigation's bar: fill " + gauge.FillatMinValue + " to " + gauge.FillatMaxValue + ", angle " + gauge.angleAtMinValue + " to "
                      + gauge.angleAtMaxValue + ", active in the scene " + gauge.gameObject.activeInHierarchy + ", mark " + (mark != null ? mark.minRot + " to " + mark.maxRot : "none") + ".");
        }
    }
}
