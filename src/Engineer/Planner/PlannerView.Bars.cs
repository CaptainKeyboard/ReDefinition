using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The bars of the route's steps and of the summary, and the stages' colours.
    internal sealed partial class PlannerView
    {
        // ------------------------------------------------------------------ bars

        // Each leg's bar on one scale, the longest leg filling the room left of
        // its figures; the summary's bar on the scale of what is needed or had,
        // whichever is more.
        private void UpdateBars()
        {
            if (!Worked()) return;
            // What the step's row leaves the bar: the line, the place, who flies, the
            // figure and "...", with the gaps between them, and a seam of one unit
            // between its pieces.
            // Six elements and five gaps besides who flies, to end where a stop's
            // cross does.
            float room = Mathf.Max(40f, width - Timeline - Place - (HasSections() ? VehicleButton + 4f : 0f)
                                        - StepSpeed_ - Square - 4f * 5f - RowEnd);
            double longest = 1.0;
            foreach (double value in budget.Needed) if (value > longest) longest = value;
            for (int leg = 0; leg < MaxStops; leg++)
                for (int role = 0; role < RouteStop.Roles; role++)
                {
                    SegmentBar bar = stepBars[leg, role];
                    if (bar == null) continue;
                    List<KeyValuePair<float, Color>> pieces = new List<KeyValuePair<float, Color>>();
                    int index = Slot(leg, (StepRole)role);
                    if (index >= 0)
                    {
                        int count = budget.Uses.FindAll(use => use.Leg == index).Count + (budget.Short[index] > 0.5 ? 1 : 0);
                        float barRoom = Mathf.Max(20f, room - count);
                        foreach (StageBudget.StageUse use in budget.Uses)
                            if (use.Leg == index) pieces.Add(Piece(use.DeltaV / longest * barRoom, StageColour(use.Stage)));
                        if (budget.Short[index] > 0.5) pieces.Add(Piece(budget.Short[index] / longest * barRoom, PlannerColors.Short));
                    }
                    bar.Set(pieces);
                    // The bar's pieces of a unit or more, with a seam of one between them.
                    float used = 0f;
                    int drawn = 0;
                    foreach (KeyValuePair<float, Color> piece in pieces)
                        if (piece.Key >= 1f) { used += piece.Key; drawn++; }
                    used += Mathf.Max(0, drawn - 1);
                    if (barFillers[leg, role] != null) barFillers[leg, role].Set(Mathf.Max(1f, room - used));
                }

            UpdateSwatches();

            if (totalBar == null) return;
            List<KeyValuePair<double, Color>> parts = new List<KeyValuePair<double, Color>>();
            for (int i = 0; i < stages.Count; i++)
            {
                double used = 0.0;
                foreach (StageBudget.StageUse use in budget.Uses) if (use.Stage == stages[i].Number) used += use.DeltaV;
                if (used > 0.5) parts.Add(new KeyValuePair<double, Color>(used, PlannerColors.Stage(i)));
            }
            if (budget.TotalLeft > 0.5) parts.Add(new KeyValuePair<double, Color>(budget.TotalLeft, PlannerColors.Spare));
            if (budget.TotalShort > 0.5) parts.Add(new KeyValuePair<double, Color>(budget.TotalShort, PlannerColors.Short));
            double sum = 0.0;
            foreach (KeyValuePair<double, Color> part in parts) sum += part.Key;
            double scale = System.Math.Max(1.0, System.Math.Max(sum, System.Math.Max(TotalNeeded(), TotalHas())));
            float usable = Mathf.Max(20f, width - RowEnd - parts.Count);
            List<KeyValuePair<float, Color>> total = new List<KeyValuePair<float, Color>>();
            foreach (KeyValuePair<double, Color> part in parts) total.Add(Piece(part.Key / scale * usable, part.Value));
            totalBar.Set(total);
        }

        // A piece of a bar: its width in units and its colour.
        private static KeyValuePair<float, Color> Piece(double pieceWidth, Color color)
        {
            return new KeyValuePair<float, Color>((float)pieceWidth, color);
        }

        // The stage's colour on the bars, by its place among the stages with delta-v; the spare colour for another.
        private Color StageColour(int number)
        {
            int at = stages.FindIndex(row => row.Number == number);
            return at >= 0 ? PlannerColors.Stage(at) : PlannerColors.Spare;
        }
    }
}
