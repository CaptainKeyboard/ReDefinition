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
    // -redefinitionProbeDownloads: the settings window's downloads as the player runs them -- the question
    // each asks (shot), "Accept and download" clicked, the window while it runs and the rows after -- for
    // NVIDIA's files, Intel's, and the RTX 30 package from the zip in KSP's folder. The click is the
    // player's acceptance of the vendors' licences: the probe runs it only where its caller asked for it.
    internal static class DownloadProbe
    {
        private const float Timeout = 900f;

        public static IEnumerator Run(string folder, string shot)
        {
            yield return WindowShots(folder, shot + "-dl0-before");

            yield return Download("nvidia", folder, shot,
                () => NvidiaDownloader.Needed().Count > 0,
                () => NvidiaDownloader.Confirm(NvidiaDownloader.Needed()),
                () => NvidiaDownloader.Running, () => NvidiaDownloader.Status, true);
            yield return WindowShots(folder, shot + "-dl2-after-nvidia");

            IntelDownloader.Refresh();
            yield return Download("intel", folder, shot,
                () => IntelDownloader.Needed().Count > 0,
                () => IntelDownloader.Confirm(IntelDownloader.Needed()),
                () => IntelDownloader.Running, () => IntelDownloader.Status, true);
            yield return WindowShots(folder, shot + "-dl4-after-intel");

            yield return Download("package", folder, shot,
                () => AmpereFrameGeneration.Zip() != null && !AmpereFrameGeneration.Installed(),
                AmpereFrameGeneration.Start,
                () => AmpereFrameGeneration.Running, () => AmpereFrameGeneration.StatusText(), false);
            yield return WindowShots(folder, shot + "-dl6-after-package");
        }

        private static IEnumerator Download(string name, string folder, string shot, Func<bool> hasWork, Action begin,
                                            Func<bool> running, Func<string> status, bool dialog)
        {
            if (!hasWork())
            {
                Debug.Log(Log.Tag + " Probe: downloads: nothing to fetch for " + name + ".");
                yield break;
            }
            begin();
            if (dialog)
            {
                yield return new WaitForSecondsRealtime(2f);
                yield return new WaitForEndOfFrame();
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, shot + "-dl1-" + name + "-question.png"));
                yield return new WaitForSecondsRealtime(1f);
                if (!ClickButton("Accept and download"))
                {
                    Debug.LogWarning(Log.Tag + " Probe: downloads: no \"Accept and download\" button for " + name + ".");
                    yield break;
                }
            }
            float started = Time.realtimeSinceStartup;
            bool progressShown = false;
            while (Time.realtimeSinceStartup - started < Timeout)
            {
                yield return new WaitForSecondsRealtime(1f);
                if (!progressShown && Time.realtimeSinceStartup - started > 4f)
                {
                    progressShown = true;
                    yield return WindowShots(folder, shot + "-dl-progress-" + name, true);
                }
                if (!running()) break;
            }
            Debug.Log(Log.Tag + " Probe: downloads: " + name + " ended after " + (Time.realtimeSinceStartup - started).ToString("0")
                      + " s, " + (running() ? "still running" : "status \"" + status() + "\"") + ".");
            yield return new WaitForSecondsRealtime(2f);
        }

        // The Display page, top and bottom (the progress once: the top).
        private static IEnumerator WindowShots(string folder, string name, bool topOnly = false)
        {
            foreach (float position in topOnly ? new[] { 1f } : new[] { 1f, 0f })
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
