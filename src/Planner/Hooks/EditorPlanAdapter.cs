using System.Collections.Generic;
using ReDefinition.Core;
using ReDefinition.Planner.Engineer;

namespace ReDefinition.Planner
{
    // The planner's page in the open editor as the dashboard reads it. Every call reads the live editor; nothing is kept here.
    internal sealed class EditorPlanAdapter : EditorPlanHook
    {
        public override Dictionary<string, object> Snapshot()
        {
            PlannerView view = View();
            return view != null ? view.Snapshot() : null;
        }

        public override bool Change(string name, string[] args)
        {
            PlannerView view = View();
            return view != null && view.Change(name, args);
        }

        public override List<ChoiceInfo> ChoicesFor(string list, int a, int b)
        {
            List<ChoiceInfo> infos = new List<ChoiceInfo>();
            PlannerView view = View();
            if (view == null) return infos;
            foreach (Choice choice in view.ChoicesFor(list, a, b))
                infos.Add(new ChoiceInfo { Label = choice.Label, Tooltip = choice.Tooltip, Current = choice.Current, Indent = choice.Indent });
            return infos;
        }

        public override bool Pick(string list, int a, int b, int index, string label)
        {
            PlannerView view = View();
            return view != null && view.Pick(list, a, b, index, label);
        }

        // The Engineer's planner in the open editor, null where there is none.
        private static PlannerView View()
        {
            return EditorEngineer.Instance != null ? EditorEngineer.Instance.PlannerForDashboard() : null;
        }
    }
}
