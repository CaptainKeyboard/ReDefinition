using ReDefinition.Core;
using ReDefinition.Settings;
using UnityEngine;

namespace ReDefinition.Window
{
    // Toolbar buttons ReDefinition hides for good, whatever the bundling, where its
    // settings window holds what the button did (ToolbarTakeover.KeepOwn).
    //
    // Persistent Rotation's button only shows and hides its window
    // (Interface.OnClick toggles disabled; its source read at 1.9.2.1), which the
    // row "Persistent Rotation window" does here (Mods/PersistentRotation.cfg).
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class ToolbarHides : MonoBehaviour
    {
        // Its ToolbarControl namespace (Interface.MODID).
        private const string PersistentRotation = "PersistentRotation";
        // The row in its registration that shows and hides the window.
        private const string WindowRow = "persistentrotation.window";

        // Hides Persistent Rotation's button while the row that takes its place is registered, where the mod is installed.
        private void Awake()
        {
            // Hidden only while the row is there to take its place: in another build of the
            // mod whose member the row cannot reach, the button stays.
            if (TypeLookup.Find("PersistentRotation.Interface") != null)
                ToolbarTakeover.KeepOwn(PersistentRotation, () => BundledSettings.Find(WindowRow) == null);
        }
    }
}
