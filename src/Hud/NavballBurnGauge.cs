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
    // The gauge right of the navball that the game shows for a maneuver burn shows the navigation's speed in a climb or a descent. The mark in the
    // middle stands for the speed asked for. The bar stands under it where thrust is to be given and over it where less is wanted, and it follows where the
    // speed will be in a few seconds, so that the player sees it coming. The number says how many m/s the speed is over the one asked for or short of
    // it, and the colour says which way and how far. The navigation's maneuver is a node of the vessel (GuideNode). The game's gauge shows it with its
    // own texts and buttons, the X button among them. Only the bar, the number and the colour are the navigation's. Where the vessel has no solver for
    // nodes the gauge is shown without the game's help. With a node of the player's own the game's own burn is shown.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class NavballBurnGauge : MonoBehaviour
    {
        private const string HarmonyId = "ReDefinition.NavballBurnGauge";
        private static bool hooked;
        private static bool reported;
        private static bool failed;
        // Whether the game's update runs for a gauge that the navigation's node is shown on, and the bar is put on it afterwards.
        private static bool guiding;
        private static RotationalGaugeOffsetMarker mark;
        // The colour the game gave the bar, put back when the navigation's bar is gone.
        private static UnityEngine.UI.Image tinted;
        private static Color untinted;
        private static readonly Color Close = new Color(1f, 0.72f, 0.2f);
        private static readonly Color Past = new Color(1f, 0.32f, 0.28f);
        // Thrust is to be given: from yellow to red as more is asked for. Less is wanted: from light blue to violet.
        private static readonly Color MoreFrom = new Color(1f, 0.9f, 0.25f);
        private static readonly Color MoreTo = new Color(1f, 0.25f, 0.2f);
        private static readonly Color LessFrom = new Color(0.45f, 0.85f, 1f);
        private static readonly Color LessTo = new Color(0.7f, 0.4f, 1f);
        // The share of the thrust asked for under which the speed counts as the one asked for, and over which the colour is at its end.
        private const double OnTarget = 0.06;
        private const double FullColour = 0.6;
        // How fast the bar follows what it is to show, in seconds, so that a change in the guidance does not make it jump.
        private const float FollowTime = 0.3f;
        private static float shown = 0.5f;
        private static bool following;
        private static readonly FieldInfo PrefabField = typeof(NavBallBurnVector).GetField("stageMarkerPrefab", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MaskField = typeof(NavBallBurnVector).GetField("stageMarkerMaskRect", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo DirectionField = typeof(NavBallBurnVector).GetField("direction", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo PlaceMarker = typeof(NavBallBurnVector).GetMethod("UpdateNavballBurnVectors", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo OffsetField = typeof(RotationalGaugeOffsetMarker).GetField("offsetObject", BindingFlags.Instance | BindingFlags.NonPublic);
        // The vector whose buttons are hidden while the navigation's bar is shown, and the one the bar is shown on.
        private static NavBallBurnVector buttonsHidden;
        private static NavBallBurnVector driven;
        private static readonly FieldInfo NodeDeltaVField = typeof(NavBallBurnVector).GetField("nodeDeltaV", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo ClearMarkers = typeof(NavBallBurnVector).GetMethod("ClearStageMarkers", BindingFlags.Instance | BindingFlags.NonPublic);

        private void Start()
        {
            failed = false;
            if (hooked) return;
            hooked = true;
            HarmonyHooks.Safely("navball-burn-gauge", "The burn gauge cannot show the navigation's bar", () =>
                HarmonyHooks.Install(HarmonyId, typeof(NavballBurnGauge), new List<HarmonyHook>
                {
                    new HarmonyHook(typeof(NavBallBurnVector).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic), nameof(Drive), nameof(Override), null),
                    new HarmonyHook(typeof(NavBallBurnVector).GetMethod("UpdateDVStageMarkers", BindingFlags.Instance | BindingFlags.NonPublic), nameof(SkipMarkers), null, null),
                }));
        }

        private void OnDestroy()
        {
            Hide();
        }

        // Before the game updates the gauge. Without a node it hides the gauge in every frame, and the gauge's own update never runs.
        // While the navigation's bar is shown the game's update is left out and the bar is set. Otherwise the game's update runs.
        private static bool Drive(NavBallBurnVector __instance, PatchedConicSolver ___solver)
        {
            if (failed) return true;
            try
            {
                guiding = false;
                Vessel vessel = FlightGlobals.ActiveVessel;
                bool board = FlightGlobals.ready && vessel != null && GuidanceBoard.Current(vessel) && GuidanceBoard.HasBar && Expanded(__instance);
                if (board && GuideNode.Holds(vessel))
                {
                    guiding = true;
                    return true;
                }
                if (!board || (___solver != null && ___solver.maneuverNodes.Count > 0))
                {
                    Hide();
                    return true;
                }
                KSP.UI.Screens.HelixGauge gauge = __instance.deltaVGauge;
                if (driven == null) Clear(__instance);
                driven = __instance;
                if (!gauge.gameObject.activeSelf) gauge.gameObject.SetActive(true);
                Paint(__instance, gauge);
                Readout(__instance);
                Direct(__instance);
                Report(gauge);
                return false;
            }
            catch (Exception e)
            {
                failed = true;
                Debug.LogWarning(Log.Tag + " The burn gauge cannot show the navigation's bar: " + e);
                Hide();
                return true;
            }
        }

        // After the game's update of a gauge that shows the navigation's node. The bar, the number and the colour are put over the game's.
        // The texts that tell the burn of the node are taken off, since the node is no burn to time. The game's X button stays.
        private static void Override(NavBallBurnVector __instance)
        {
            if (!guiding || failed) return;
            guiding = false;
            try
            {
                KSP.UI.Screens.HelixGauge gauge = __instance.deltaVGauge;
                driven = __instance;
                Paint(__instance, gauge);
                if (__instance.readoutText != null) __instance.readoutText.text = Number();
                if (__instance.ebtText != null) __instance.ebtText.enabled = false;
                if (__instance.TdnText != null) __instance.TdnText.enabled = false;
                if (__instance.sbtText != null) __instance.sbtText.enabled = false;
                if (__instance.btnWarpTo != null) __instance.btnWarpTo.gameObject.SetActive(false);
                buttonsHidden = __instance;
                Report(gauge);
            }
            catch (Exception e)
            {
                failed = true;
                Debug.LogWarning(Log.Tag + " The burn gauge cannot show the navigation's bar: " + e);
                Hide();
            }
        }

        // The game's marks of the stages that would burn the node: there are none for the navigation's, whose burn is not a burn to time.
        private static bool SkipMarkers()
        {
            return !GuideNode.Holds(FlightGlobals.ActiveVessel);
        }

        // The bar, its colour and the mark in the middle.
        private static void Paint(NavBallBurnVector vector, KSP.UI.Screens.HelixGauge gauge)
        {
            gauge.MinValue = 0f;
            gauge.MaxValue = 1f;
            gauge.currentValue = Follow(Wanted());
            if (gauge.tgtImage != null)
            {
                if (tinted == null)
                {
                    tinted = gauge.tgtImage;
                    untinted = tinted.color;
                }
                tinted.color = Tint();
            }
            Mark(vector, gauge);
        }

        // Where the bar is to stand, 0 to 1, with the mark at a half. Under the mark thrust is to be given, over it less is wanted.
        private static float Wanted()
        {
            if (GuidanceBoard.HasGap) return (float)(0.5 - 0.5 * GuidanceBoard.Need);
            return (float)Math.Max(0.0, Math.Min(1.0, 1.0 - GuidanceBoard.BarFill));
        }

        // The bar moves towards where it is to stand, a part of the way in every frame.
        private static float Follow(float wanted)
        {
            if (!following)
            {
                following = true;
                shown = wanted;
            }
            else shown += (wanted - shown) * (1f - Mathf.Exp(-Time.unscaledDeltaTime / FollowTime));
            return shown;
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

        // The colour of the bar: the game's own where the speed is the one asked for, warm where thrust is to be given and cool where less is wanted.
        // Without a speed asked for it is the level the board gives, amber and red as the vessel gets far from it.
        private static Color Tint()
        {
            if (!GuidanceBoard.HasGap) return GuidanceBoard.BarLevel >= 2 ? Past : GuidanceBoard.BarLevel == 1 ? Close : untinted;
            double need = 1.0 - 2.0 * shown;
            if (Math.Abs(need) < OnTarget) return untinted;
            float share = (float)Math.Min(1.0, (Math.Abs(need) - OnTarget) / (FullColour - OnTarget));
            return need > 0.0 ? Color.Lerp(MoreFrom, MoreTo, share) : Color.Lerp(LessFrom, LessTo, share);
        }

        // What the number on the gauge says: how many m/s the speed is over the one asked for, or short of it, a few seconds from now as the bar has it.
        private static string Number()
        {
            if (!GuidanceBoard.HasGap) return GuidanceBoard.BarText ?? "";
            double gap = GuidanceBoard.Ahead;
            if (Math.Abs(gap) < 1.0) return "0 m/s";
            return Math.Abs(gap).ToString("0") + (gap > 0.0 ? " m/s over" : " m/s short");
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

        // What a node left on the burn gauge is taken off as the navigation's bar starts: the texts, the red part and the marks of its stages.
        private static void Clear(NavBallBurnVector vector)
        {
            if (vector.ebtText != null) vector.ebtText.enabled = false;
            if (vector.TdnText != null) vector.TdnText.enabled = false;
            if (vector.sbtText != null) vector.sbtText.enabled = false;
            if (vector.deltaVGaugeRed != null) vector.deltaVGaugeRed.gameObject.SetActive(false);
            if (ClearMarkers != null) ClearMarkers.Invoke(vector, null);
        }

        // As the navigation's bar stops the gauge is switched off and the game's size of its burn forgotten. The game then sets a node's burn up
        // again from the start, with its texts, and does not take the size of the bar for the burn's.
        private static void Release()
        {
            if (driven == null) return;
            if (driven.deltaVGauge != null) driven.deltaVGauge.gameObject.SetActive(false);
            if (NodeDeltaVField != null) NodeDeltaVField.SetValue(driven, double.MaxValue);
            driven = null;
        }

        private static void Hide()
        {
            Release();
            following = false;
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
