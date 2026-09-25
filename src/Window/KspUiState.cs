using KSP.UI;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Window
{
    // Whether KSP's interface shows, as the mods read it.
    //
    // KSP keeps it in UIMasterController.isUIShowing: false when the game starts, and
    // set only by GameEvents.onShowUI and onHideUI (decompiled), which F2 and pausing
    // fire. A mod that fires onHideUI and shows the interface again without onShowUI
    // leaves it false while the interface is on screen -- QuickIVA, which starts a
    // flight in IVA. Mods that draw only while it shows then open no window: TUFX asks
    // UIMasterController.IsUIShowing, Firefly keeps its own flag from the two events
    // (both decompiled), and ReDefinition's Advanced buttons open nothing.
    //
    // Where KSP's main canvas is on and the flag says hidden, onShowUI is fired. It
    // changes nothing on screen and brings the flag and every listener back in step.
    // With the interface hidden by F2 the canvases are off, and it stays hidden.
    internal static class KspUiState
    {
        private static bool logged;

        internal static void ResyncShown()
        {
            UIMasterController ui = UIMasterController.Instance;
            if (ui == null || ui.IsUIShowing || ui.mainCanvas == null || !ui.mainCanvas.enabled) return;
            if (!logged)
            {
                logged = true;
                Debug.Log(Log.Tag + " KSP's interface is on screen but marked hidden, as a mod left it; marked shown"
                          + " again, so the mods that draw only while it shows open their windows.");
            }
            GameEvents.onShowUI.Fire();
        }
    }
}
