using ReDefinition.Modules;
using ReDefinition.Settings;
using ReDefinition.Upscaler;

namespace ReDefinition
{
    // Keeps the other mods' antialiasing in step with the upscaler (HostStack): taken while the
    // upscaler runs, handed back when it stops. The add-on calls Sync once per frame; the
    // diagnostics window shows what the last Apply or Restore said.
    internal sealed class HostStackSync
    {
        // The settings and the rig's lifecycle that decide whether the upscaler runs.
        private readonly SettingsController config;
        private readonly RigLifecycle rigs;

        // Set where a camera's antialiasing changes at once: the map view entered or left.
        private bool duplicatesNow;

        // Sync for the settings and rig given.
        public HostStackSync(SettingsController config, RigLifecycle rigs)
        {
            this.config = config;
            this.rigs = rigs;
        }

        // What the last HostStack.Apply or Restore said; null before the first.
        public string Message { get; private set; }

        // Entering or leaving the map view: the next Sync re-takes the mods' antialiasing at once.
        public void NoteMapToggled()
        {
            duplicatesNow = true;
        }

        // The other mods' antialiasing, and what Scatterer's TAA leaves behind, are
        // taken while the upscaler runs and handed back when it stops (HostStack). It also
        // runs with frame generation alone, as antialiasing at the screen's size
        // (RigLifecycle.NativeForFrameGeneration). Without either, the mods' own antialiasing is the
        // only one there is, one per camera (HostStack.Duplicates). Taken where the
        // cameras it configures exist: once a rig runs, or in flight and the editors.
        // Taken earlier, at the main menu say, HostStack would record Scatterer's state
        // before Scatterer has set itself up. A later scene's new parts are taken
        // each second, with their own record (HostStack.Reassert).
        public void Sync()
        {
            UpscalerRig rig = rigs.Rig;
            bool upscaling = BundledSettings.ProfileChosen && !rigs.PassThroughWanted;
            // Given back before HostStack takes everything.
            HostStack.SyncDuplicates(!upscaling && rig == null && BundledSettings.ProfileChosen && config.Settings.OneAntialiasing, duplicatesNow);
            duplicatesNow = false;
            if (upscaling && !HostStack.Applied
                && (rig != null || HighLogic.LoadedSceneIsFlight || HighLogic.LoadedSceneIsEditor))
                Message = HostStack.Apply();
            else if (!upscaling && HostStack.Applied)
                Message = HostStack.Restore();
        }
    }
}
