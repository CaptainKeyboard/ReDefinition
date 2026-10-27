using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Window
{
    // Keeps the Toolbar's buttons and MechJeb's menu off KSP's black screen between
    // scenes. KSP shows it with a camera of its own and hides only its uGUI
    // (LoadingBufferMask.Show: camera.enabled, UIMasterController.HideUI;
    // decompiled); the two draw with OnGUI and stayed on the black. Their OnGUI is
    // skipped while that camera is on (ToolbarManager.OnGUI, MechJebCore.OnGUI,
    // decompiled from the installed DLLs), and nothing else: other mods, and what
    // a mod wants to show on the loading screen, draw as they do.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class LoadingScreenWindows : MonoBehaviour
    {
        // Every instance method, public or not: what the two OnGUI lookups ask for.
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        // KSP's loading screen, once found, and the frame it was last looked for in (where the field below is missing).
        private static LoadingBufferMask mask;
        private static int lookedAt = -1;

        // LoadingBufferMask sets its internal static Instance in Start (decompiled), and there is one mask: the
        // field gives the screen the search over the scene would find, and costs a read where nothing is found yet.
        private static readonly FieldInfo instanceField = typeof(LoadingBufferMask).GetField("Instance",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        // Kept across scenes; installs the prefix on each of the two mods that is installed.
        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Skip("Toolbar.ToolbarManager", "the Toolbar's buttons");
            Skip("MuMech.MechJebCore", "MechJeb's menu");
        }

        // Installs the prefix on the type's OnGUI where the mod is installed; a failure is a warning.
        private static void Skip(string type, string what)
        {
            System.Type found = TypeLookup.Find(type);
            if (found == null) return;
            HarmonyHooks.Safely("loading-screen-" + found.Name, what + " show on KSP's loading screen", () =>
            {
                HarmonyHooks.Install("ReDefinition.LoadingScreen." + found.Name, typeof(LoadingScreenWindows), new List<HarmonyHook>
                {
                    new HarmonyHook(found.GetMethod("OnGUI", Instance), nameof(DrawUnlessLoading), null, null),
                });
                Debug.Log(Log.Tag + " Kept off KSP's loading screen: " + what + ".");
            });
        }

        // Harmony prefix: false skips the mod's OnGUI.
        private static bool DrawUnlessLoading()
        {
            if (mask == null) mask = FindMask();
            return mask == null || mask.camera == null || !mask.camera.enabled;
        }

        // The loading screen where it stands: its Instance, else (in a KSP without that field) a search of the scene
        // once a frame.
        private static LoadingBufferMask FindMask()
        {
            if (instanceField != null) return instanceField.GetValue(null) as LoadingBufferMask;
            if (Time.frameCount == lookedAt) return null;
            lookedAt = Time.frameCount;
            return FindObjectOfType<LoadingBufferMask>();
        }
    }
}
