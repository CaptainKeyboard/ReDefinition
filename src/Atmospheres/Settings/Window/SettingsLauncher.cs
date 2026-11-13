using System;
using KSP.UI.Screens;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The button in the application launcher that opens the settings window of the atmospheres, in every scene of the game that has a
    // launcher. It is made when the launcher is ready and taken away when the launcher is destroyed. The window is made on first use.
    // Where the main mod is installed its own settings window may hold the same settings; the button stays, as the two edit the same
    // table and the same file [VERIFY with the owner whether the button is hidden then].
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    internal sealed class SettingsLauncher : MonoBehaviour
    {
        private ApplicationLauncherButton button;
        private SettingsWindow window;

        private void Awake()
        {
            // Without the main mod (HostGate) the atmospheres do not start: no button.
            if (!HostGate.Open) return;
            DontDestroyOnLoad(gameObject);
            GameEvents.onGUIApplicationLauncherReady.Add(OnLauncherReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(OnLauncherDestroyed);
        }

        private void OnDestroy()
        {
            if (!HostGate.Open) return;
            GameEvents.onGUIApplicationLauncherReady.Remove(OnLauncherReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(OnLauncherDestroyed);
            Hide();
        }

        private void OnLauncherReady()
        {
            try
            {
                if (button != null || ApplicationLauncher.Instance == null) return;
                const ApplicationLauncher.AppScenes scenes = ApplicationLauncher.AppScenes.SPACECENTER | ApplicationLauncher.AppScenes.FLIGHT
                    | ApplicationLauncher.AppScenes.VAB | ApplicationLauncher.AppScenes.SPH | ApplicationLauncher.AppScenes.TRACKSTATION
                    | ApplicationLauncher.AppScenes.MAPVIEW;
                button = ApplicationLauncher.Instance.AddModApplication(Show, Hide, null, null, null, null, scenes, Icon());
            }
            catch (Exception e)
            {
                AtmospheresLog.WarnOnce("the settings button could not be added (" + CompatibilityLog.Reason(e) + ")");
            }
        }

        private void OnLauncherDestroyed()
        {
            Hide();
            if (button != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(button);
            button = null;
        }

        private void Show()
        {
            try
            {
                if (window == null)
                {
                    window = new SettingsWindow(WindowModel.ForGame());
                    // The window's own Close button puts the launcher's button back.
                    window.OnClosed = () =>
                    {
                        if (button != null) button.SetFalse(false);
                    };
                }
                window.Show();
            }
            catch (Exception e)
            {
                AtmospheresLog.WarnOnce("the settings window could not be opened (" + CompatibilityLog.Reason(e) + ")");
                if (button != null) button.SetFalse(false);
            }
        }

        private void Hide()
        {
            if (window != null) window.Close();
        }

        // A cloud with falling flakes in the launcher's colours, drawn once.
        private static Texture2D Icon()
        {
            const int n = 38;
            var texture = new Texture2D(n, n, TextureFormat.ARGB32, false);
            var pixels = new Color32[n * n];
            var clear = new Color32(0, 0, 0, 0);
            var line = new Color32(235, 235, 235, 255);
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    // Two bumps on a flat base for the cloud, the outline only; three flakes below it.
                    bool inside = InCloud(x, y, 0) && !InCloud(x, y, -2);
                    bool flake = (y == 8 || y == 9) && (x == 12 || x == 19 || x == 26) || (y == 5 && (x == 15 || x == 22));
                    pixels[y * n + x] = inside || flake ? line : clear;
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        // The cloud's shape grown by `grow` pixels (negative shrinks it): a base, a small left bump and a large right bump.
        private static bool InCloud(int x, int y, int grow)
        {
            bool bump1 = Disc(x, y, 14, 22, 5 + grow);
            bool bump2 = Disc(x, y, 22, 24, 7 + grow);
            bool bump3 = Disc(x, y, 29, 20, 4 + grow);
            bool bar = x >= 9 - grow && x <= 33 + grow && y >= 14 - grow && y <= 19 + grow;
            return bump1 || bump2 || bump3 || bar;
        }

        private static bool Disc(int x, int y, int cx, int cy, int r)
        {
            return r > 0 && (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;
        }
    }
}
