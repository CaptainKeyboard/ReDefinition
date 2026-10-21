namespace ReDefinition
{
    // ReDefinition's own settings as the settings window's Reset leaves them: each
    // back to its default, frame generation off, but for what is the player's
    // hardware or menu and no quality -- the upscaling technique, the raytracing and
    // its Ray Reconstruction, "Replace original settings", the window's pause, the
    // navball's place, the diagnostics window's pins -- and,
    // once a profile is chosen, whether the upscaler is on
    // (ModuleSetting.OnlyActivates).
    internal static class OwnSettingsReset
    {
        internal static OwnSettings From(OwnSettings now, bool active)
        {
            OwnSettings reset = new OwnSettings();
            if (now == null) return reset;
            reset.Backend = now.Backend;
            reset.Raytracing = now.Raytracing;
            reset.RaytracingBounces = now.RaytracingBounces;
            reset.RayReconstruction = now.RayReconstruction;
            reset.ReplaceKspSettings = now.ReplaceKspSettings;
            reset.PauseWhileOpen = now.PauseWhileOpen;
            reset.NavballOffset = now.NavballOffset;
            reset.NavballScreen = now.NavballScreen;
            reset.DiagnosticsPins = now.DiagnosticsPins;
            reset.DiagnosticsPage = now.DiagnosticsPage;
            reset.DiagnosticsX = now.DiagnosticsX;
            reset.DiagnosticsY = now.DiagnosticsY;
            if (active) reset.Enabled = now.Enabled;
            return reset;
        }
    }
}
