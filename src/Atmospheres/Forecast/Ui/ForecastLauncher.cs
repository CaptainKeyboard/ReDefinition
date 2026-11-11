using System;
using KSP.UI.Screens;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres.Forecast
{
    // The button that opens the forecast window, in the application launcher of the Space Centre, the Tracking Station and the flight
    // (and its map view). The settings button of the atmospheres (SettingsLauncher) is the existing entry point of this assembly and this
    // follows its pattern. A world button on the Space Centre's buildings through HostSeams.SpaceCentreButton needs a spot on a building
    // that the main mod owns, so the launcher is the entry point both with and without the main mod [VERIFY with the owner].
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    internal sealed class ForecastLauncher : MonoBehaviour
    {
        private ApplicationLauncherButton button;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            GameEvents.onGUIApplicationLauncherReady.Add(OnLauncherReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(OnLauncherDestroyed);
        }

        private void OnDestroy()
        {
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
                    | ApplicationLauncher.AppScenes.TRACKSTATION | ApplicationLauncher.AppScenes.MAPVIEW;
                button = ApplicationLauncher.Instance.AddModApplication(Show, Hide, null, null, null, null, scenes, Icon());
                ForecastWindow.Instance.OnClosed = () =>
                {
                    if (button != null) button.SetFalse(false);
                };
            }
            catch (Exception e)
            {
                AtmospheresLog.WarnOnce("the forecast button could not be added (" + CompatibilityLog.Reason(e) + ")");
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
                ForecastWindow.Instance.Show();
            }
            catch (Exception e)
            {
                AtmospheresLog.WarnOnce("the forecast window could not be opened (" + CompatibilityLog.Reason(e) + ")");
                if (button != null) button.SetFalse(false);
            }
        }

        private void Hide()
        {
            if (ForecastWindow.Instance.IsOpen) ForecastWindow.Instance.Close();
        }

        // A cloud over a sun and an arrow of the forecast, drawn from code in the launcher's colours: no file.
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
                    bool cloud = Disc(x, y, 16, 15, 7) && !Disc(x, y, 16, 15, 5) || Disc(x, y, 24, 13, 6) && !Disc(x, y, 24, 13, 4)
                                 || (y == 8 && x >= 12 && x <= 28);
                    // Rays of a sun at the upper right.
                    bool sun = Disc(x, y, 28, 28, 4) && !Disc(x, y, 28, 28, 2);
                    bool ray = (x == 28 && (y == 21 || y == 35)) || (y == 28 && (x == 21 || x == 35));
                    pixels[y * n + x] = cloud || sun || ray ? line : clear;
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        private static bool Disc(int x, int y, int cx, int cy, int r)
        {
            return r > 0 && (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;
        }
    }
}
