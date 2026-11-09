using ReDefinition.Ui;
using UnityEngine;

namespace ReDefinition.Planner.Ui
{
    // The planner's own two switches, offered from the Live menu where ReDefinition, whose settings window holds them, is not installed.
    internal static class PlannerSettingsDialog
    {
        private static PopupDialog shown;

        internal static void Open()
        {
            if (shown != null) shown.Dismiss();
            PlannerSettings.Load();
            var dialog = new MultiOptionDialog("ReDefinitionPlannerSettings", "", "Planner Settings", HighLogic.UISkin, 260f,
                new DialogGUIVerticalLayout(240f, -1f, 4f, new RectOffset(6, 6, 6, 6), TextAnchor.UpperLeft,
                    new DialogGUIToggle(() => PlannerSettings.BackgroundRoute, "Fly the Route in the Background", on => PlannerSettings.BackgroundRoute = on, 240f, 24f),
                    new DialogGUIToggle(() => PlannerSettings.WorldButtons, "Buttons in the World", on => PlannerSettings.WorldButtons = on, 240f, 24f),
                    new DialogGUIButton("Close", PlannerSettings.Save, 240f, 26f, true)));
            shown = DialogWindow.Show(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), dialog, false, HighLogic.UISkin, true));
        }
    }
}
