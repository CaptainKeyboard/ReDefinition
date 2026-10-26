namespace ReDefinition.Modules
{
    // ReDefinition's own settings as the settings window's Reset leaves them. Each is
    // back to its default, but for what is the player's menu and no graphics setting.
    // These are "Replace original settings", the window's pause, the navball's place, the
    // diagnostics window's pins, and the dashboard's key, which the phones that
    // opened it hold. The graphics settings (the upscaler, frame generation,
    // raytracing, low latency) are the profile's, which the Reset chooses (High) over them.
    internal static class OwnSettingsReset
    {
        // Fresh defaults with the player's own settings above taken over from now; defaults alone where there is none.
        internal static OwnSettings From(OwnSettings now, bool active)
        {
            OwnSettings reset = new OwnSettings();
            if (now == null) return reset;
            reset.ReplaceKspSettings = now.ReplaceKspSettings;
            reset.PauseWhileOpen = now.PauseWhileOpen;
            reset.NavballOffset = now.NavballOffset;
            reset.NavballScreen = now.NavballScreen;
            reset.DiagnosticsPins = now.DiagnosticsPins;
            reset.DiagnosticsPage = now.DiagnosticsPage;
            reset.DiagnosticsX = now.DiagnosticsX;
            reset.DiagnosticsY = now.DiagnosticsY;
            reset.DashboardKey = now.DashboardKey;
            return reset;
        }
    }
}
