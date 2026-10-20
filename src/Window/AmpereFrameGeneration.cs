using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using KSP.Localization;
using ReDefinition.Bridges;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Window
{
    // DLSS frame generation on GeForce RTX 30 and RTX 20 through sdli1995's
    // dlssg_for_sm86 (AmperePackage), which ReDefinition neither ships nor downloads:
    // offered in the settings window on those GPUs, as NvidiaDownloader offers NVIDIA's
    // files. Its dialog says where to get the release zip and where to put it; the
    // install takes the files out of the zip on a thread of its own. The package runs
    // from KSP's next start on.
    internal static class AmpereFrameGeneration
    {
        public static bool Running { get; private set; }

        // Installed this session: until KSP is restarted, frame generation does not run
        // from it.
        public static bool InstalledNow { get; private set; }

        private static string status;

        // What the settings row says: the install's progress or outcome, null before one.
        public static string Status
        {
            get { return status; }
        }

        // Whether the row stands: on an RTX GPU without DLSS frame generation of its own,
        // or with the package in place -- which then answers NVAPI with a newer GPU.
        public static bool Offered()
        {
            if (Installed()) return true;
            uint architecture;
            uint implementation;
            return FrameGenerationBridge.NvidiaGpu(out architecture, out implementation)
                   && NvidiaFiles.DlssRuns(architecture, implementation) && !NvidiaFiles.FrameGenerationRuns(architecture);
        }

        public static bool Installed()
        {
            try
            {
                return AmperePackage.Installed(NvidiaDownloader.GameFolder);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The release zip in KSP's folder, looked for at most once a second: the row and
        // the dialog ask every frame.
        private static string zip;
        private static float zipLookedFor = float.NegativeInfinity;

        public static string Zip()
        {
            if (Time.realtimeSinceStartup - zipLookedFor < 1f) return zip;
            zipLookedFor = Time.realtimeSinceStartup;
            try
            {
                zip = AmperePackage.FindZip(NvidiaDownloader.GameFolder);
            }
            catch (Exception)
            {
                zip = null;
            }
            return zip;
        }

        // The zip as the row and the dialog name it, grey.
        public static string ZipText()
        {
            string found = Zip();
            return "<color=#9a9a9a>" + (found != null
                ? "Found in KSP's folder: " + Path.GetFileName(found)
                : "No " + AmperePackage.ZipPattern + " in KSP's folder yet") + "</color>";
        }

        // The steps, as the player follows them: the release page, the zip into KSP's
        // folder, the install, a restart.
        public static void Show()
        {
            string message =
                "NVIDIA's DLSS frame generation runs on GeForce RTX 40 and newer. dlssg_for_sm86, a community project"
                + " by sdli1995, runs it on RTX 30 and RTX 20 as well: its version.dll next to KSP_x64.exe has NVIDIA's"
                + " frame generation runtime run on kernels rebuilt for these GPUs. It is neither NVIDIA's nor"
                + " ReDefinition's, and ReDefinition neither ships nor downloads it. It holds NVIDIA's runtime; NVIDIA's"
                + " licences cover NVIDIA's parts, the project's MIT license its own.\n\n"
                + "1. Open the release page and download " + AmperePackage.ZipPattern.Replace("*", "<version>") + ".\n"
                + "2. Put the zip, as it is, into KSP's folder:\n    " + NvidiaDownloader.GameFolder + "\n"
                + "3. Install from the zip: ReDefinition takes version.dll, dlssg_sm86.ini and the dlssg_sm86 folder"
                + " out of it, without its super-resolution files, and places them next to KSP_x64.exe. A file already"
                + " there is kept, renamed to end in .old.\n\n"
                + "DLSS frame generation runs from it after a restart of KSP. Where NVIDIA's files for it are missing,"
                + " the \"NVIDIA DLSS files\" row offers them then. Each generated frame costs GPU time on these cards."
                + " To remove it, delete version.dll, dlssg_sm86.ini and the dlssg_sm86 folder while KSP is closed.";

            DialogGUIButton install = new DialogGUIButton("Install from zip", Start, 200f, 30f, false);
            install.OptionInteractableCondition = () => !Running && Zip() != null;

            DialogGUIBase[] rows =
            {
                new DialogGUIHorizontalLayout(
                    new DialogGUIFlexibleSpace(),
                    DialogOverWindow.Link("Release page", () => Application.OpenURL(AmperePackage.ReleasesUrl), 110f),
                    DialogOverWindow.Link("Project page", () => Application.OpenURL(AmperePackage.ProjectUrl), 110f),
                    new DialogGUIFlexibleSpace()),
                new DialogGUILabel(ZipText, true),
                new DialogGUILabel(StatusText, true),
                new DialogGUIHorizontalLayout(
                    new DialogGUIFlexibleSpace(),
                    install,
                    new DialogGUIButton(Localizer.Format("#autoLOC_174783"), () => { }, 120f, 30f, true),
                    new DialogGUIFlexibleSpace()),
            };

            MultiOptionDialog dialog = new MultiOptionDialog("ReDefinitionAmpereFrameGeneration", message,
                "DLSS frame generation on RTX 30 and RTX 20", HighLogic.UISkin, 520f, rows);
            UnityMouseEvents.Shield(DialogOverWindow.Raise(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), dialog, false, HighLogic.UISkin, false)));
        }

        // The install's progress or outcome, grey, made anew only when it changes.
        private static string shownStatus;
        private static string shownStatusText = "";

        public static string StatusText()
        {
            if (status != shownStatus)
            {
                shownStatus = status;
                shownStatusText = status == null ? "" : "<color=#9a9a9a>" + status + "</color>";
            }
            return shownStatusText;
        }

        public static void Start()
        {
            string found = Zip();
            if (Running || found == null || ReDefinitionAddon.Instance == null) return;
            Running = true;
            ReDefinitionAddon.Instance.StartCoroutine(Run(found));
        }

        // Out of the zip on a thread: about 30 MB of DLLs would otherwise hold the game
        // for a moment.
        private static IEnumerator Run(string found)
        {
            status = "Installing from " + Path.GetFileName(found) + " ...";
            string game = NvidiaDownloader.GameFolder;
            string error = null;
            int placed = 0;
            bool done = false;
            Thread worker = new Thread(() =>
            {
                try
                {
                    placed = AmperePackage.Install(found, game);
                }
                catch (Exception e)
                {
                    error = e.Message;
                }
                done = true;
            });
            worker.IsBackground = true;
            worker.Start();
            while (!Volatile.Read(ref done)) yield return null;

            Running = false;
            if (error != null)
            {
                status = "Not installed: " + Path.GetFileName(found) + ": " + error + ".";
                Debug.LogWarning(Log.Tag + " dlssg_for_sm86: " + status);
            }
            else
            {
                InstalledNow = true;
                status = "Installed, " + placed + " files from " + Path.GetFileName(found)
                         + ". DLSS frame generation runs from it after a restart of KSP.";
                Debug.Log(Log.Tag + " dlssg_for_sm86: " + status);
            }
            ScreenMessages.PostScreenMessage("dlssg_for_sm86: " + status, 8f);
        }
    }
}
