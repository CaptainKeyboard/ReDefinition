using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Window
{
    // Tells other mods that the UI is hidden while KSP shows its black screen
    // between scenes. KSP shows it with a camera of its own and hides only its
    // uGUI (LoadingBufferMask.Show: camera.enabled, UIMasterController.HideUI;
    // decompiled), without the events F2 fires; mods drawing with OnGUI stayed on
    // the black. Here the same events: onHideUI as the black screen comes, again
    // once a new scene has loaded under it (a mod's new instances start shown),
    // and onShowUI as it goes. The Toolbar (ToolbarManager) and MechJeb
    // (MechJebCore) hide by them, as they do for F2; a mod that wants to show
    // something there leaves them unheeded.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class LoadingHidesUi : MonoBehaviour
    {
        private LoadingBufferMask mask;
        private bool hidden;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnLoaded);
        }

        private void OnDestroy()
        {
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLoaded);
        }

        private void Update()
        {
            if (mask == null) mask = FindObjectOfType<LoadingBufferMask>();
            bool black = mask != null && mask.camera != null && mask.camera.enabled;
            if (black == hidden) return;
            hidden = black;
            Debug.Log(Log.Tag + " KSP's loading screen " + (black ? "shown: the UI hidden for other mods." : "gone: the UI shown again."));
            if (black) GameEvents.onHideUI.Fire();
            else GameEvents.onShowUI.Fire();
        }

        private void OnLoaded(GameScenes scene)
        {
            if (hidden) GameEvents.onHideUI.Fire();
        }
    }
}
