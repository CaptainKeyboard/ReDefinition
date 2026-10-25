using System.Collections;
using ReDefinition.Bridges;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Window
{
    // The main menu's word, once a game, where Windows refused the dxgi.dll proxy
    // next to KSP_x64.exe (ProxyLoad): without it, DLSS, AMD's upscaler, frame
    // generation and raytracing are missing, and nothing else would say why.
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class ProxyNotice : MonoBehaviour
    {
        // Two seconds after the main menu opens: the notice where Windows refused the proxy.
        private IEnumerator Start()
        {
            // After the menu has settled, beside BundleNotice's question.
            yield return new WaitForSecondsRealtime(2f);

            string refused = ProxyLoad.Refused;
            if (refused == null) yield break;

            MultiOptionDialog dialog = new MultiOptionDialog("ReDefinitionProxyRefused",
                refused + "\n\nWithout the proxy, DLSS, AMD's upscaler, frame generation and raytracing are not available;"
                + " FSR 3 upscaling runs.",
                "ReDefinition's dxgi.dll was blocked", HighLogic.UISkin, 480f,
                new DialogGUIButton("OK", () => { }, true));
            UnityMouseEvents.Shield(ScreenFit.Add(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                dialog, false, HighLogic.UISkin)));
            Debug.Log(Log.Tag + " Main menu notice: the dxgi.dll proxy was blocked.");
        }
    }
}
