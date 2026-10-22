namespace ReDefinition
{
    // ReDefinition's own settings as the settings window's Reset leaves them: each
    // back to its default, but for what is the player's menu and no graphics setting --
    // "Replace original settings", the window's pause, the navball's place, the
    // diagnostics window's pins. The graphics settings -- the upscaler, frame generation,
    // raytracing, low latency -- are the profile's, which the Reset chooses (High) over them.
    internal static class OwnSettingsReset
    {
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
            return reset;
        }
    }
}
