using ReDefinition.Modules;

namespace ReDefinition
{
    // The add-on's part for its settings: the members the rest of the mod calls, each forwarding to
    // SettingsController, which holds the settings and applies their changes.
    public partial class ReDefinitionAddon
    {
        // The diagnostic measurements (OwnSettings.DebugMode), read by the instruments
        // wherever they run.
        internal static bool DebugMode
        {
            get { return SettingsController.DebugMode; }
        }

        // The settings as they stand, for what reads them every frame without a
        // copy -- the UI scaling. Not to be written through.
        internal OwnSettings Live
        {
            get { return config.Settings; }
        }

        // The settings as they are now, as a copy: KSP's settings dialog edits
        // it and hands it back through Apply only on Apply or Accept.
        internal OwnSettings Current()
        {
            return config.Current();
        }

        // What a settings view changed between the copy it was given and the one it hands back,
        // applied through the setters; a rig that needs building is built before this returns.
        internal void Apply(OwnSettings before, OwnSettings after)
        {
            config.Apply(before, after);
        }

        // Switches the upscaler and frame generation off where no graphics profile is chosen.
        internal void EnforceProfile()
        {
            config.EnforceProfile();
        }

        // Where the navball stands on this screen's size; NavballDrag moves it.
        internal float NavballOffset
        {
            get { return config.NavballOffset; }
            set { config.NavballOffset = value; }
        }

        // Whether KSP's Settings buttons open ReDefinition's window.
        internal bool ReplaceKspSettings
        {
            get { return config.ReplaceKspSettings; }
        }

        // Whether the settings window holds the flight while it is open (WindowPause).
        internal bool PauseWhileOpen
        {
            get { return config.PauseWhileOpen; }
            set { config.PauseWhileOpen = value; }
        }

        // Before KSP quits for a restart: the settings on disk now, not at the
        // next check.
        internal void SaveSettingsNow()
        {
            config.SaveSettingsNow();
        }

        // The probe's profile start: the settings the profile left, on disk for the next start.
        internal void SaveSettingsForProbeProfile()
        {
            config.SaveSettingsForProbeProfile();
        }

        // The first start's answer to take control: KSP's Settings buttons open ReDefinition's window.
        internal void TakeKspSettings()
        {
            config.TakeKspSettings();
        }

        // NVIDIA's download placed nvngx_dlss.dll: DLSS is tried at once where it is chosen.
        internal void NvidiaFilesPlaced()
        {
            config.NvidiaFilesPlaced();
        }

        // Intel's download placed libxess.dll: XeSS is tried at once where it is chosen.
        internal void IntelFilesPlaced()
        {
            config.IntelFilesPlaced();
        }
    }
}
