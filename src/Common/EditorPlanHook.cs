using System.Collections.Generic;

namespace ReDefinition.Core
{
    // One entry of a list the planner folds out: its text and tooltip, whether it is the current choice, and its level (a moon under its planet).
    public sealed class ChoiceInfo
    {
        public string Label;
        public string Tooltip;
        public bool Current;
        public float Indent;
    }

    // What the host reads and asks of the planner's page in the editor, for the dashboard.
    // An abstract class with defaults, for the same reason as GuidanceHook. The defaults say "no planner here".
    public abstract class EditorPlanHook
    {
        // The rows of the planner's plan as one snapshot, made again only where their texts may have changed; null where the open editor has no planner.
        // The host compares snapshots by reference to find out whether anything changed.
        public virtual Dictionary<string, object> Snapshot() { return null; }

        // A change of the plan, by its name and numbers. False for one the planner does not know.
        public virtual bool Change(string name, string[] args) { return false; }

        // The list a planner button folds out, by what it is for; empty for any other.
        public virtual List<ChoiceInfo> ChoicesFor(string list, int a, int b) { return new List<ChoiceInfo>(); }

        // The choice at `index` of that list, where it still reads `label`: the list may have changed since it was shown. False if not.
        public virtual bool Pick(string list, int a, int b, int index, string label) { return false; }
    }
}
