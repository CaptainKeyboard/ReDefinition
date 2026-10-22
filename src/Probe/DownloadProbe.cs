using System;
using System.Collections;
using System.IO;
using ReDefinition.Core;
using ReDefinition.Settings;
using ReDefinition.Window;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Probe
{
    // -redefinitionProbeDownloads: choosing a technique whose files are missing, as the player does -- the
    // dialog it opens (shot), its button clicked, the technique taken once the files are in place, and
    // left as it was where the dialog is cancelled -- for XeSS frame generation (cancelled), DLSS as the
    // upscaler (NVIDIA's files), XeSS as the upscaler (Intel's) and DLSS frame generation on an RTX 30
    // (the package from the zip in KSP's folder). The click on "Accept and download" is the player's
    // acceptance of the vendors' licences: the probe runs only where its caller asked for it.
    internal static class DownloadProbe
    {
        private const float Timeout = 900f;

        public static IEnumerator Run(string folder, string shot)
        {
            yield return WindowShots(folder, shot + "-dl0-before");

            ModuleSetting upscaler = OurModules.Upscaler.Setting("technique");
            ModuleSetting generation = OurModules.FrameGeneration.Setting("technique");

            yield return Choose("1-xess-fg-cancelled", folder, shot, generation, "Xess", new[] { "Cancel" });
            yield return Choose("2-dlss", folder, shot, upscaler, "Dlss", new[] { "Accept and download" });
            yield return WindowShots(folder, shot + "-dl3-after-dlss");
            yield return Choose("4-xess", folder, shot, upscaler, "Xess", new[] { "Accept and download" });
            yield return WindowShots(folder, shot + "-dl5-after-xess");
            yield return Choose("6-dlss-fg", folder, shot, generation, "Dlss", new[] { "Install from zip" });
            yield return WindowShots(folder, shot + "-dl7-after-dlss-fg");
        }

        // Chooses the value as the window's button does: the way to its files, then the continuation. The
        // clicks are the dialogs' buttons in the order they come.
        private static IEnumerator Choose(string name, string folder, string shot, ModuleSetting setting, string value,
                                          string[] clicks)
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            FilesNeeded needed = addon == null ? null : setting.Files(addon.Current(), value);
            if (needed == null)
            {
                Debug.Log(Log.Tag + " Probe: downloads: " + name + ": nothing missing, the choice is taken at once.");
                yield break;
            }
            Debug.Log(Log.Tag + " Probe: downloads: " + name + ": " + needed.Text.Replace('\n', ' '));
            bool continued = false;
            needed.Begin(() => continued = true);

            for (int i = 0; i < clicks.Length; i++)
            {
                yield return new WaitForSecondsRealtime(2f);
                yield return new WaitForEndOfFrame();
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, shot + "-dl-" + name + "-dialog" + (i + 1) + ".png"));
                yield return new WaitForSecondsRealtime(1f);
                if (!ClickButton(clicks[i]))
                {
                    Debug.LogWarning(Log.Tag + " Probe: downloads: " + name + ": no \"" + clicks[i] + "\" button.");
                    yield break;
                }
                // Until the download or the install is over.
                float started = Time.realtimeSinceStartup;
                yield return new WaitForSecondsRealtime(1f);
                while ((NvidiaDownloader.Running || IntelDownloader.Running || AmpereFrameGeneration.Running)
                       && Time.realtimeSinceStartup - started < Timeout)
                    yield return new WaitForSecondsRealtime(1f);
            }
            yield return new WaitForSecondsRealtime(2f);
            Debug.Log(Log.Tag + " Probe: downloads: " + name + ": the choice " + (continued ? "was taken" : "was not taken") + ".");
        }

        // The Display page, top and bottom.
        private static IEnumerator WindowShots(string folder, string name)
        {
            foreach (float position in new[] { 1f, 0f })
            {
                TabScrollList.ProbePosition = position;
                SettingsWindow.OpenOn(SettingCategory.Display);
                yield return new WaitForSecondsRealtime(2f);
                yield return new WaitForEndOfFrame();
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, name + (position > 0.5f ? "-top" : "-bottom") + ".png"));
                yield return new WaitForSecondsRealtime(1f);
            }
            TabScrollList.ProbePosition = null;
        }

        private static bool ClickButton(string text)
        {
            foreach (Button button in UnityEngine.Object.FindObjectsOfType<Button>())
            {
                if (!button.isActiveAndEnabled) continue;
                TMP_Text label = button.GetComponentInChildren<TMP_Text>();
                if (label == null || label.text == null || label.text.IndexOf(text, StringComparison.Ordinal) < 0) continue;
                button.onClick.Invoke();
                return true;
            }
            return false;
        }
    }
}
