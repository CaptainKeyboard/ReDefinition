using UnityEngine;

namespace ReDefinition.Planner.Planning
{
    // The colours the planner draws with: one per stage, in firing order, and the
    // red of a leg no stage pays for. Picked apart from green, which marks the
    // parts on the vessel, and from each other.
    internal static class PlannerColors
    {
        // The stages' colours, by their place in firing order, repeated past the eighth.
        private static readonly Color[] Stages =
        {
            new Color(0.36f, 0.61f, 1f), new Color(0.94f, 0.69f, 0.29f), new Color(0.69f, 0.49f, 1f),
            new Color(0.31f, 0.82f, 0.85f), new Color(1f, 0.49f, 0.71f), new Color(0.78f, 0.70f, 0.48f),
            new Color(0.54f, 0.63f, 0.72f), new Color(0.95f, 0.85f, 0.40f),
        };

        // What no stage covers; the timeline and dividers; what is left over.
        internal static readonly Color Short = new Color(0.90f, 0.28f, 0.30f);
        internal static readonly Color Line = new Color(0.55f, 0.55f, 0.58f);
        internal static readonly Color Spare = new Color(0.40f, 0.40f, 0.43f);
        // Behind a step: a little lighter than the panel, whatever theme it has.
        internal static readonly Color StepBackground = new Color(1f, 1f, 1f, 0.07f);

        // The colour of the stage at that place in firing order.
        internal static Color Stage(int order)
        {
            return Stages[((order % Stages.Length) + Stages.Length) % Stages.Length];
        }

        // The colour as rich text takes it: "#5c9cff".
        internal static string Hex(Color color)
        {
            return "#" + ColorUtility.ToHtmlStringRGB(color);
        }
    }
}
