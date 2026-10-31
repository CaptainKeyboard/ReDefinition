using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The planner's part "Guidance": the navigation and the autopilot as two choices that turn each other off, shown as the planner shows its
    // other choices (flat, set off by a line, lit where on). They can be turned on once the route is planned, which the planner does by itself.
    internal sealed partial class PlannerView
    {
        // What the window gives the part; null in the editor, which has no flight to plan.
        internal IPlanControls Controls;
        private bool controlsShown = true;

        // What the heading says while the part is folded or not: which one is on.
        private string GuidanceSummary()
        {
            if (Controls == null) return "";
            if (Controls.Flying) return "<color=" + Good + ">Autopilot</color>";
            if (Controls.NavigationOn) return "<color=" + Good + ">Navigation</color>";
            return "<color=" + Dim + ">Off</color>";
        }

        // The part's one row: the two choices side by side.
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
        }
    }
}
