using System.Collections.Generic;
using ReDefinition.Planner.Planning;

namespace ReDefinition.Planner.Ui
{
    // The planner as the dashboard's VAB page shows and works it (src/Dashboard/EditorTelemetry.cs). The page gets the rows' texts, the lists
    // the buttons fold out and the changes the buttons make from PlannerDashboard; this part hands the view's calls on to it.
    internal sealed partial class PlannerView
    {
        // What the rows show, made again only where their texts may have changed.
        internal Dictionary<string, object> Snapshot()
        {
            return dashboard.Snapshot();
        }

        // The list a button folds out, by what it is for: "body" (stop), "flyer" (leg, role), "tanker" (leg, role), "parts" (section row),
        // "newSection", "deploy" (section row); empty for anything else.
        internal List<Choice> ChoicesFor(string list, int a, int b)
        {
            return dashboard.ChoicesFor(list, a, b);
        }

        // The choice at `index` of that list, where it still reads `label`: the list may have changed since the dashboard showed it.
        internal bool Pick(string list, int a, int b, int index, string label)
        {
            return dashboard.Pick(list, a, b, index, label);
        }

        // A change the dashboard asks for, as the planner's button for it makes it: the name and its numbers. False for one it does not know.
        internal bool Change(string name, string[] args)
        {
            return dashboard.Change(name, args);
        }
    }
}
