using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The planner's first part, "Plan": the three actions on the route as the other rows of the planner show choices (flat, set off by lines,
    // lit where on), and under them what the route is doing. The navigation and the autopilot turn on only once the route is planned.
    internal sealed partial class PlannerView
    {
        // What the window gives the part; null in the editor, which has no flight to plan.
        internal IPlanControls Controls;
        private bool controlsShown = true;

        // The part's rows: the three actions, then the line of what the route does, and the line of what Parsek does.
        private IEnumerable<DialogGUIBase> PlanControlRows()
        {
            float button = Mathf.Floor((width - Divider * 2f - Gap * 4f - RowEnd) / 3f);
            DialogGUIButton plan = Choice("Plan route", () => Controls != null && !Controls.PlanReady, () => Controls.PlanRoute(), button);
            plan.OptionInteractableCondition = () => Controls != null && !Controls.Flying;
            plan.tooltipText = "Plan the whole route from here and draw it on the map.";
            DialogGUIButton navigation = Choice("Navigation", () => Controls != null && Controls.NavigationOn, () => Controls.ToggleNavigation(), button);
            navigation.OptionInteractableCondition = () => Controls != null && !Controls.Flying && (Controls.NavigationOn || Controls.PlanReady);
            navigation.tooltipText = "Keep the plan up to date while you fly the route yourself. Needs the route planned.";
            DialogGUIButton autopilot = Choice("Autopilot", () => Controls != null && Controls.Flying, () => Controls.ToggleAutopilot(), button);
            autopilot.OptionInteractableCondition = () => Controls != null && (Controls.Flying || (Controls.PlanReady && Controls.AutopilotProblem == null));
            autopilot.tooltipText = "Fly the whole route with MechJeb. Needs the route planned.";
            yield return new GatedRow(width, RowHeight + 2f, Gap, new RectOffset(), TextAnchor.MiddleLeft, plan,
                PlannerWidgets.Block(Divider, RowHeight - 4f, PlannerColors.Line), navigation,
                PlannerWidgets.Block(Divider, RowHeight - 4f, PlannerColors.Line), autopilot);

            yield return PlanText(() => Controls != null ? Controls.Status : "", () => Controls != null && Controls.Flying ? Good : Dim);
            yield return PlanText(() => Controls != null ? Controls.ParsekLine : "", () => Dim);
        }

        // One line of text in the part, off while it has none, cut to the room the row has.
        private DialogGUIBase PlanText(System.Func<string> text, System.Func<string> colour)
        {
            int room = Mathf.Max(20, (int)(width / PlanCharWidth));
            DialogGUILabel label = new DialogGUILabel(() =>
            {
                string line = text();
                if (string.IsNullOrEmpty(line)) return "";
                if (line.Length > room) line = line.Substring(0, room - 3) + "...";
                return "<color=" + colour() + ">" + line + "</color>";
            }, width - RowEnd, RowHeight);
            GatedRow row = new GatedRow(width, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft, label);
            row.OptionEnabledCondition = () => !string.IsNullOrEmpty(text());
            return row;
        }
    }
}
