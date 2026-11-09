namespace ReDefinition.Ui
{
    // The one way a dialog of ours is opened, in ReDefinition and in the planner alike. Clicks on it stay on it (WindowShield).
    // It stays within the screen at KSP's UI scale (ScreenFit). Its controls are off the keyboard's navigation (UiHygiene.MouseOnly).
    internal static class DialogWindow
    {
        // The dialog as given, now one of ours.
        internal static PopupDialog Show(PopupDialog dialog)
        {
            if (dialog == null) return null;
            WindowShield.Shield(dialog);
            ScreenFit.Add(dialog);
            UiHygiene.MouseOnly(dialog.gameObject);
            return dialog;
        }
    }
}
