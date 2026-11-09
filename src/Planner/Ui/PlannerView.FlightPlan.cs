using System.Collections.Generic;
using ReDefinition.Planner.Autopilot.Planning;
using ReDefinition.Planner.Engineer;
using ReDefinition.Planner.Planning;
using UnityEngine;
using static ReDefinition.Planner.Planning.PlanText;

namespace ReDefinition.Planner.Ui
{
    // The planner's flight plan: the route as the Plan button works it out in flight, step after step, with the orbits it flies. The rows
    // are single-line labels; a long text is broken into several of them, since a label of changing text has no height of its own.
    internal sealed partial class PlannerView
    {
        // The most rows the plan takes, the width one character is taken to need, and what shows where there is no plan. The width was measured on a
        // screenshot of the window: a row of 58 characters filled three quarters of the width.
        private const int MaxPlanLines = 60;
        private const float PlanCharWidth = 5.8f;
        private const string NoPlanHint = "The route has no plan yet: it needs a start and a destination.";

        private bool planShown;

        // The rows' texts, the plan and the UT they were made for.
        private readonly List<string> planLines = new List<string>();
        private RoutePlan linedPlan;
        private double linedAt;
        private bool planLinesMade;

        // The rows: one label each, shown while it has a text.
        private IEnumerable<DialogGUIBase> FlightPlanRows()
        {
            for (int i = 0; i < MaxPlanLines; i++)
            {
                int line = i;
                DialogGUIHorizontalLayout row = new GatedRow(width, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                    new DialogGUILabel(() => PlanLine(line), width - RowEnd, RowHeight));
                row.OptionEnabledCondition = () => PlanLine(line).Length > 0;
                yield return row;
            }
        }

        // What Parsek does with the flight, where Parsek is there.
        private void AddParsekLine()
        {
            IPlanControls controls = model.Controls;
            if (controls != null && !string.IsNullOrEmpty(controls.ParsekLine)) AddWrapped(controls.ParsekLine, Dim, Dim);
        }

        // "5 steps, 3,210 m/s", in red where the plan failed; empty without a plan.
        private string PlanSummary()
        {
            RoutePlan plan = model.Host.Plan;
            if (plan == null) return "";
            string text = plan.Steps.Count + (plan.Steps.Count == 1 ? " step, " : " steps, ") + EngineerFormat.Speed(plan.DeltaV);
            string colour = plan.Failed ? Problem : Good;
            return "<color=" + colour + ">" + text + "</color>";
        }

        // The text of one row; empty while the part is folded (KSP asks every label every frame) or the plan has fewer rows.
        private string PlanLine(int row)
        {
            if (!planShown) return "";
            RefreshPlanLines();
            return row < planLines.Count ? planLines[row] : "";
        }

        // Makes the rows' texts again where the plan is another or a UT second has passed, which moves the times to the burns.
        private void RefreshPlanLines()
        {
            RoutePlan plan = model.Host.Plan;
            IPlanControls controls = model.Controls;
            double now = plan != null ? Planetarium.GetUniversalTime() : 0.0;
            if (planLinesMade && plan == linedPlan && System.Math.Abs(now - linedAt) < 1.0) return;
            planLinesMade = true;
            linedPlan = plan;
            linedAt = now;
            planLines.Clear();
            if (controls != null && !string.IsNullOrEmpty(controls.Status))
                AddWrapped(controls.Status, controls.Flying ? Good : Dim, controls.Flying ? Good : Dim);
            if (plan == null)
            {
                AddWrapped(NoPlanHint, Dim, Dim);
                AddParsekLine();
                return;
            }

            AddWrapped("Total " + EngineerFormat.Speed(plan.DeltaV) + " in " + plan.Steps.Count + (plan.Steps.Count == 1 ? " step" : " steps"), null, null);
            if (plan.Failed) AddWrapped(plan.Ended ?? "The plan failed.", Problem, Problem);
            else if (plan.Ended != null) AddWrapped("Then flown in the flight: " + plan.Ended, Good, Good);
            else AddWrapped("Reaches the last stop.", Good, Good);

            foreach (PlannedStep step in plan.Steps) AddWrapped(PlanStepText(step, now), null, Dim);

            if (plan.Legs.Count > 0) planLines.Add(Coloured("Orbits", Dim));
            foreach (PlannedLeg leg in plan.Legs) AddWrapped(PlanLegText(leg.Orbit), null, Dim);

            AddParsekLine();
            if (planLines.Count > MaxPlanLines)
            {
                int more = planLines.Count - (MaxPlanLines - 1);
                planLines.RemoveRange(MaxPlanLines - 1, planLines.Count - (MaxPlanLines - 1));
                planLines.Add(Coloured("(" + more + " more lines)", Dim));
            }
        }

        // "T+1h 05m Burn 1,234 m/s: why"; a step that is no burn has no time and no speed.
        private static string PlanStepText(PlannedStep step, double now)
        {
            string why = (step.Why ?? "").Replace('\n', ' ');
            if (step.UT <= 0.0) return step.Kind + ": " + why;
            double ahead = step.UT - now;
            string when = (ahead < 0.0 ? "T-" : "T+") + KSPUtil.PrintTimeCompact(System.Math.Abs(ahead), false);
            return when + " " + step.Kind + " " + EngineerFormat.Speed(step.DeltaV.Length) + ": " + why;
        }

        // "Mun 12 km x 12 km"; an orbit that leaves the body has no apoapsis.
        private static string PlanLegText(Orbit orbit)
        {
            string apoapsis = orbit.eccentricity >= 1.0 ? "escape" : EngineerFormat.Distance(orbit.ApA);
            return orbit.referenceBody.bodyName + " " + EngineerFormat.Distance(orbit.PeA) + " x " + apoapsis;
        }

        // The text broken to the row's width, the first row in `first` and the rest in `next`; null is no colour.
        private void AddWrapped(string text, string first, string next)
        {
            List<string> parts = EngineerFormat.Wrap(text, Mathf.Max(20, (int)(width / PlanCharWidth)));
            for (int i = 0; i < parts.Count; i++) planLines.Add(Coloured(parts[i], i == 0 ? first : next));
        }

        private static string Coloured(string text, string colour)
        {
            return colour == null ? text : "<color=" + colour + ">" + text + "</color>";
        }
    }
}
