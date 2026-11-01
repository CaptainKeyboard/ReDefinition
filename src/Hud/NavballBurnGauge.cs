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
    // The number says how many m/s the speed is over it or short of it, and the bar's colour says which way and how far. With a node on the vessel the game's burn is shown.
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
        private static readonly Color Close = new Color(1f, 0.72f, 0.2f);
        private static readonly Color Past = new Color(1f, 0.32f, 0.28f);
        // More thrust is asked for: from yellow to red as it is asked for more. Less thrust: from light blue to violet.
        private static readonly Color MoreFrom = new Color(1f, 0.9f, 0.25f);
        private static readonly Color MoreTo = new Color(1f, 0.25f, 0.2f);
        private static readonly Color LessFrom = new Color(0.45f, 0.85f, 1f);
        private static readonly Color LessTo = new Color(0.7f, 0.4f, 1f);
        // The share of the thrust asked for under which the speed counts as the one asked for, and over which the colour is at its end.
        private const double OnTarget = 0.06;
        private const double FullColour = 0.6;
        private static readonly FieldInfo PrefabField = typeof(NavBallBurnVector).GetField("stageMarkerPrefab", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MaskField = typeof(NavBallBurnVector).GetField("stageMarkerMaskRect", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo DirectionField = typeof(NavBallBurnVector).GetField("direction", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo PlaceMarker = typeof(NavBallBurnVector).GetMethod("UpdateNavballBurnVectors", BindingFlags.Instance | BindingFlags.NonPublic);
        // The vector whose buttons are hidden while the navigation's bar is shown.
        private static NavBallBurnVector buttonsHidden;
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
                double fill = GuidanceBoard.HasGap ? 0.5 + 0.5 * GuidanceBoard.Need : GuidanceBoard.BarFill;
                gauge.currentValue = (float)Math.Max(0.0, Math.Min(1.0, fill));
                if (gauge.tgtImage != null)
                {
                    if (tinted == null)
                    {
                        tinted = gauge.tgtImage;
                        untinted = tinted.color;
                    }
                    tinted.color = Tint();
                }
                Mark(__instance, gauge);
                Readout(__instance);
                Direct(__instance);
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

        // The colour of the bar: the game's own where the speed is the one asked for, warm where more thrust is asked for and cool where less is.
        // Without a speed asked for it is the level the board gives, amber and red as the vessel gets far from it.
        private static Color Tint()
        {
            if (!GuidanceBoard.HasGap) return GuidanceBoard.BarLevel >= 2 ? Past : GuidanceBoard.BarLevel == 1 ? Close : untinted;
            double need = GuidanceBoard.Need;
            if (Math.Abs(need) < OnTarget) return untinted;
            float share = (float)Math.Min(1.0, (Math.Abs(need) - OnTarget) / (FullColour - OnTarget));
            return need > 0.0 ? Color.Lerp(MoreFrom, MoreTo, share) : Color.Lerp(LessFrom, LessTo, share);
        }

        // What the number on the gauge says: how many m/s the speed is over the one asked for, or short of it.
        private static string Number()
        {
            if (!GuidanceBoard.HasGap) return GuidanceBoard.BarText ?? "";
            double gap = GuidanceBoard.SpeedGap;
            if (Math.Abs(gap) < 1.0) return "0 m/s";
            return Math.Abs(gap).ToString("0") + (gap > 0.0 ? " m/s over" : " m/s short");
        }

        // The number on the gauge. The buttons beside it are for a node, which there is none of.
        private static void Readout(NavBallBurnVector vector)
        {
            if (vector.readoutText != null) vector.readoutText.text = Number();
            if (vector.btnAccept != null) vector.btnAccept.gameObject.SetActive(false);
            if (vector.btnDismiss != null) vector.btnDismiss.gameObject.SetActive(false);
            if (vector.btnWarpTo != null) vector.btnWarpTo.gameObject.SetActive(false);
            buttonsHidden = vector;
        }

        // The navball's blue marker of a burn stands where the navigation asks the nose to point, as it stands on a node's burn.
        private static void Direct(NavBallBurnVector vector)
        {
            if (!GuidanceBoard.HasDirection || DirectionField == null || PlaceMarker == null) return;
            DirectionField.SetValue(vector, (Vector3)GuidanceBoard.Direction);
            PlaceMarker.Invoke(vector, null);
        }

        private static void Hide()
        {
            if (buttonsHidden != null)
            {
                if (buttonsHidden.btnWarpTo != null) buttonsHidden.btnWarpTo.gameObject.SetActive(true);
                buttonsHidden = null;
            }
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
