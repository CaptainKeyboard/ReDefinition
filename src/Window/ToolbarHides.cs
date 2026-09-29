using ReDefinition.Scaling;
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

        private void Awake()
        {
            if (UiScaling.TypeOf("PersistentRotation.Interface") != null)
                ToolbarTakeover.KeepOwn(PersistentRotation, () => false);
        }
    }
}
