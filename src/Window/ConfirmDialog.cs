using UnityEngine;

namespace ReDefinition.Window
{
    // A question in the middle of the screen with a button that does it and KSP's
    // Cancel, shielded from clicks through it (UnityMouseEvents.Shield).
    internal static class ConfirmDialog
    {
        internal static void Show(string name, string title, string message, float width, string act, Callback onAct,
                                  float buttonWidth)
        {
            MultiOptionDialog question = new MultiOptionDialog(name, message, title, HighLogic.UISkin, width,
                new DialogGUIHorizontalLayout(
                    new DialogGUIButton(act, onAct, buttonWidth, 30f, true),
                    new DialogGUIFlexibleSpace(),
                    new DialogGUIButton(KSP.Localization.Localizer.Format("#autoLOC_174783"), () => { }, buttonWidth, 30f, true)));
            UnityMouseEvents.Shield(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), question, false,
                HighLogic.UISkin));
        }

        // Over the settings window (DialogOverWindow), as its own questions stand:
        // the button that does it above KSP's Cancel.
        internal static void OverWindow(string name, string title, string message, float width, string act,
                                        Callback onAct)
        {
            MultiOptionDialog question = new MultiOptionDialog(name, message, title, HighLogic.UISkin, width,
                new DialogGUIButton(act, onAct, true),
                new DialogGUIButton(KSP.Localization.Localizer.Format("#autoLOC_174783"), () => { }, true));
            UnityMouseEvents.Shield(DialogOverWindow.Raise(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), question, false, HighLogic.UISkin, false)));
        }

        // Whether the game can be saved now (FlightGlobals.ClearToSave); where not,
        // KSP's reason on the screen, `action` what could not be done.
        internal static bool ClearToSave(string action)
        {
            ClearToSaveStatus clear = FlightGlobals.ClearToSave();
            if (clear == ClearToSaveStatus.CLEAR) return true;
            ScreenMessages.PostScreenMessage(FlightGlobals.GetNotClearToSaveStatusReason(clear, action), 5f,
                ScreenMessageStyle.UPPER_CENTER);
            return false;
        }
    }
}
