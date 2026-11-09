using System.Collections.Generic;
using UnityEngine;
using static ReDefinition.Engineer.PlanText;

namespace ReDefinition.Engineer
{
    // The planner's part "Guidance": the navigation and the autopilot as two choices that turn each other off, shown as the planner shows its
    // other choices (flat, set off by a line, lit where on). They can be turned on once the route is planned, which the planner does by itself.
    internal sealed partial class PlannerView
    {
        private bool controlsShown = true;

        // What the heading says while the part is folded or not: which one is on.
        private string GuidanceSummary()
        {
            if (Controls == null) return "";
            if (Controls.Flying) return "<color=" + Good + ">Autopilot</color>";
            if (Controls.NavigationOn) return "<color=" + Good + ">Navigation</color>";
            return "<color=" + Dim + ">Off</color>";
        }

        // The part's rows: the navigation and the autopilot side by side, undo and clear, and MechJeb's switches in two rows.
        private IEnumerable<DialogGUIBase> GuidanceRows()
        {
            float button = Mathf.Floor((width - Divider - Gap * 2f - RowEnd) / 2f);
            DialogGUIButton navigation = Choice("Navigation", () => Controls != null && Controls.NavigationOn, () => Controls.ToggleNavigation(), button);
            navigation.OptionInteractableCondition = () => Controls != null && (Controls.NavigationOn || Controls.Flying || Controls.PlanReady);
            navigation.tooltipText = "Keep the plan up to date while you fly the route yourself.";
            DialogGUIButton autopilot = Choice("Autopilot", () => Controls != null && Controls.Flying, () => Controls.ToggleAutopilot(), button);
            autopilot.OptionInteractableCondition = () => Controls != null && (Controls.Flying || (Controls.PlanReady && Controls.AutopilotProblem == null));
            autopilot.tooltipText = "Fly the whole route with MechJeb.";
            yield return new GatedRow(width, RowHeight + 2f, Gap, new RectOffset(), TextAnchor.MiddleLeft, navigation,
                PlannerWidgets.Block(Divider, RowHeight - 4f, PlannerColors.Line), autopilot);
            DialogGUIButton undo = Choice("Undo", () => false, () => model.Undo(), button);
            undo.OptionInteractableCondition = () => model.CanUndo && Controls != null && !Controls.Flying;
            undo.tooltipText = "Bring back the route the last choice replaced.";
            DialogGUIButton clear = Choice("Clear", () => false, () => ClearPlan(), button);
            clear.OptionInteractableCondition = () => !model.RouteEmpty && Controls != null;
            clear.tooltipText = "Empty the route; a flight under way is stopped.";
            yield return new GatedRow(width, RowHeight + 2f, Gap, new RectOffset(), TextAnchor.MiddleLeft, undo,
                PlannerWidgets.Block(Divider, RowHeight - 4f, PlannerColors.Line), clear);
            yield return SwitchRow(button, "Auto Staging", MechJebSwitch.AutoStaging, "MechJeb stages by itself when the stage runs dry.",
                "Deploy on Arrival", MechJebSwitch.LimitPressure, false, "Put out the solar panels, antennas and radiators where the route ends.");
            yield return SwitchRow(button, "Limit Pressure", MechJebSwitch.LimitPressure, "Hold the throttle back above MechJeb's dynamic pressure limit.",
                "Limit Acceleration", MechJebSwitch.LimitAcceleration, true, "Hold the throttle back above MechJeb's acceleration limit.");
        }

        // The route emptied, after the flight under way is stopped: the player can always delete the plan.
        private void ClearPlan()
        {
            if (Controls != null && Controls.Flying) Controls.ToggleAutopilot();
            model.ClearRoute();
        }

        // A destination's cross: the stop removed, or, where only the start and one destination are left, the route emptied.
        private void RemoveOrClear(int index)
        {
            if (model.Ready() && model.State.Stops.Count <= 2) ClearPlan();
            else model.RemoveStop(index);
        }

        // Two choices side by side: MechJeb's own switch on the left; on the right another of MechJeb's (`rightIsMechJeb`) or the planner's own choice
        // to put the folded parts out on arrival.
        private GatedRow SwitchRow(float button, string leftLabel, MechJebSwitch left, string leftTip, string rightLabel, MechJebSwitch right,
            bool rightIsMechJeb, string rightTip)
        {
            DialogGUIButton first = Choice(leftLabel, () => Controls != null && Controls.IsOn(left), () => Controls.Toggle(left), button);
            first.OptionInteractableCondition = () => Controls != null && Controls.Has(left) && Controls.AutopilotProblem == null;
            first.tooltipText = leftTip;
            DialogGUIButton second;
            if (rightIsMechJeb)
            {
                second = Choice(rightLabel, () => Controls != null && Controls.IsOn(right), () => Controls.Toggle(right), button);
                second.OptionInteractableCondition = () => Controls != null && Controls.Has(right) && Controls.AutopilotProblem == null;
            }
            else
            {
                second = Choice(rightLabel, () => Controls != null && Controls.DeployOnArrival, () => Controls.ToggleDeployOnArrival(), button);
                second.OptionInteractableCondition = () => Controls != null;
            }
            second.tooltipText = rightTip;
            return new GatedRow(width, RowHeight + 2f, Gap, new RectOffset(), TextAnchor.MiddleLeft, first,
                PlannerWidgets.Block(Divider, RowHeight - 4f, PlannerColors.Line), second);
        }
    }
}
