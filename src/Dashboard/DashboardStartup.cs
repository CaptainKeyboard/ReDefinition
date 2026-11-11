using KSP.UI.Screens;
using ReDefinition.Core;
using ReDefinition.Ui;
using UnityEngine;

namespace ReDefinition.Dashboard
{
    // The dashboard offers its connect dialog to the host through DashboardHooks, once at the start of the game.
    // Where no host is present it has a button of its own in the application launcher that opens the same dialog with the network switch.
    // The host assigns its seams in its own Awake, also at Startup.Instantly, and wins whichever add-on runs first.
    // The launcher button is made later, when the launcher is ready, and only where the seams say no host is there.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    internal sealed class DashboardStartup : MonoBehaviour
    {
        private ApplicationLauncherButton button;
        private PopupDialog shown;
        private bool reopen;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            string problem = CommonVersionCheck.Problem("ReDefinition Dashboard", typeof(DashboardStartup).Assembly);
            if (problem != null) Debug.LogWarning(Log.Tag + " " + problem);
            if (DashboardHooks.ConnectContent == null) DashboardHooks.ConnectContent = ConnectDialog.Content;
            // The alert feed listens from the start of the game, so that a dashboard that connects late reads the last alerts.
            if (DashboardSettings.WeatherPushOn) WeatherTelemetry.Shared.Attach();
            GameEvents.onGUIApplicationLauncherReady.Add(OnLauncherReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(OnLauncherDestroyed);
        }

        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(OnLauncherReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(OnLauncherDestroyed);
            CloseDialog();
        }

        private void OnLauncherReady()
        {
            if (!HostSeams.StandsAlone())
            {
                Debug.Log(Log.Tag + " Dashboard: launcher button not added, ReDefinition is the host");
                return;
            }
            if (button != null || ApplicationLauncher.Instance == null) return;
            const ApplicationLauncher.AppScenes scenes = ApplicationLauncher.AppScenes.SPACECENTER | ApplicationLauncher.AppScenes.FLIGHT
                | ApplicationLauncher.AppScenes.VAB | ApplicationLauncher.AppScenes.SPH | ApplicationLauncher.AppScenes.TRACKSTATION
                | ApplicationLauncher.AppScenes.MAPVIEW;
            button = ApplicationLauncher.Instance.AddModApplication(OpenDialog, CloseDialog, null, null, null, null, scenes, Icon());
            Debug.Log(Log.Tag + " Dashboard: launcher button added");
        }

        private void OnLauncherDestroyed()
        {
            CloseDialog();
            if (button != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(button);
            button = null;
        }

        // A switch changed what the dialog shows: it opens again on the next frame, not from inside its own control.
        private void Update()
        {
            if (!reopen) return;
            reopen = false;
            OpenDialog();
        }

        private void OpenDialog()
        {
            CloseDialog();
            var rows = new System.Collections.Generic.List<DialogGUIBase>
            {
                new DialogGUIToggle(() => DashboardSettings.NetworkOn, "Dashboard on other devices", on =>
                {
                    DashboardSettings.SetNetwork(on);
                    reopen = true;
                }, 320f, 24f),
            };
            rows.AddRange(ConnectDialog.Content());
            rows.Add(new DialogGUIButton("Close", () => { if (button != null) button.SetFalse(false); }, 320f, 26f, true));
            var dialog = new MultiOptionDialog("ReDefinitionDashboard", "", "Dashboard", HighLogic.UISkin, 340f,
                new DialogGUIVerticalLayout(320f, -1f, 4f, new RectOffset(6, 6, 6, 6), TextAnchor.UpperLeft, rows.ToArray()));
            shown = DialogWindow.Show(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), dialog, false, HighLogic.UISkin, true));
        }

        private void CloseDialog()
        {
            if (shown != null) shown.Dismiss();
            shown = null;
        }

        // A phone outline in the launcher's colours, drawn once.
        private static Texture2D Icon()
        {
            const int n = 38;
            var texture = new Texture2D(n, n, TextureFormat.ARGB32, false);
            var pixels = new Color32[n * n];
            var clear = new Color32(0, 0, 0, 0);
            var line = new Color32(235, 235, 235, 255);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    bool body = x >= 11 && x <= 26 && y >= 4 && y <= 33;
                    bool edge = body && (x <= 12 || x >= 25 || y <= 5 || y >= 32);
                    bool speaker = y == 29 && x >= 16 && x <= 21;
                    bool screen = body && y >= 9 && y <= 26 && x >= 15 && x <= 22 && (y == 9 || y == 26 || x == 15 || x == 22);
                    pixels[y * n + x] = edge || speaker || screen ? line : clear;
                }
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }
    }
}
