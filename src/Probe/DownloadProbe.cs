using System;
using System.Collections;
using System.IO;
using ReDefinition.Core;
using ReDefinition.Modules;
using ReDefinition.Settings;
using ReDefinition.Ui;
using ReDefinition.Vendor;
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
        // The longest wait for one download or install to end, in seconds.
        private const float Timeout = 900f;

        // The run: the Display page shot before, each technique chosen in turn with its dialogs' buttons clicked, and
        // the buttons' states and the page shot after.
        public static IEnumerator Run(string folder, string shot)
        {
            ModuleSetting upscaler = OurModules.Upscaler.Setting("technique");
            ModuleSetting generation = OurModules.FrameGeneration.Setting("technique");

            yield return new WaitForSecondsRealtime(2f);
            LogButtons("before", upscaler, generation);
            yield return WindowShots(folder, shot + "-dl0-before");

            yield return Choose("1-xess-fg-cancelled", folder, shot, generation, "Xess", new[] { "Cancel" });
            yield return Choose("2-dlss", folder, shot, upscaler, "Dlss", new[] { "Accept and download" });
            yield return WindowShots(folder, shot + "-dl3-after-dlss");
            yield return Choose("4-xess", folder, shot, upscaler, "Xess", new[] { "Accept and download" });
            yield return WindowShots(folder, shot + "-dl5-after-xess");
            yield return Choose("6-dlss-fg", folder, shot, generation, "Dlss", new[] { "Install from zip", "Accept and download" });
            yield return Choose("8-xess-fg", folder, shot, generation, "Xess", new string[0]);
            yield return new WaitForSecondsRealtime(2f);
            LogButtons("after", upscaler, generation);
            yield return WindowShots(folder, shot + "-dl9-after-all");
        }

        // Each button of the two technique rows: greyed out and why, waiting for files, or ready.
        private static void LogButtons(string when, params ModuleSetting[] rows)
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon == null) return;
            OwnSettings now = addon.Current();
            Debug.Log(Log.Tag + " Probe: buttons " + when + ": frame generation's Auto stands for "
                      + AutoSettings.FrameGenerationTechnique(FrameGenerationTechniqueSetting.Auto,
                          TechniqueAvailability.Current())
                      + ", the upscaler's for " + AutoSettings.ResolvedBackend + ".");
            foreach (ModuleSetting row in rows)
                foreach (string choice in row.Choices)
                {
                    string reason = row.Unavailable == null ? null : row.Unavailable(now, choice);
                    FilesNeeded files = reason != null || row.Files == null ? null : row.Files(now, choice);
                    Debug.Log(Log.Tag + " Probe: buttons " + when + ": " + row.Title + " " + choice + ": "
                              + (reason != null ? "greyed out (" + reason + ")"
                                 : files != null ? "lit, waits for files (" + files.Text + ")" : "lit, ready"));
                }
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

        // Clicks the first active button whose label holds the text; false where there is none.
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
