using System.Collections.Generic;
using System.IO;
using System;
using KSP.Localization;
using ReDefinition.Core;
using ReDefinition.Ui;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition.Vendor
{
    // The player's download of Intel's DLLs (IntelFiles) from Intel's own release
    // of the XeSS SDK on GitHub, as NvidiaDownloader does NVIDIA's. It is offered in the
    // settings window where they are missing, and started once the player has
    // accepted Intel's licence in the dialog below.
    //
    // The download first fetches every file's bytes of the archive, by range, each into a file of its
    // own in the system's temporary folder. Then, on a thread of its own, it inflates and
    // checks every file into a partial file beside its target. It places them only when
    // all of them are right, all or none (NvidiaFiles.PlaceAll).
    // Once placed, libxess.dll takes effect at once (the proxy looks for it again as it
    // changes), XeSS frame generation after a restart: its DLLs are loaded as KSP
    // makes its swapchain.
    internal static class IntelDownloader
    {
        // The download this class describes to the player: what it fetches and says, and the progress it shows.
        // Nothing is fetched until the player has accepted Intel's licence (Confirm). A stalled connection ends after
        // the file at a minute per megabyte, and not under 1800 seconds. What is in place is looked at again as the
        // outcome is kept.
        private static readonly ArchiveDownload download = new ArchiveDownload("Intel's", IntelFiles.ArchiveUrl,
            "Intel's licence", "Intel's XeSS files", length => (int)Math.Max(1800L, length / (1024 * 1024) * 60), Refresh);

        // Whether a download, its check or its placing is under way: a second one is not started meanwhile.
        public static bool Running
        {
            get { return download.Running; }
        }

        // Placed this session: the row then offers nothing more.
        public static bool Installed
        {
            get { return download.Installed; }
        }

        // What the settings row says below its button: the download's progress or
        // outcome, else the DLLs in place with their versions, null for neither.
        public static string Status
        {
            get { return download.Status(installedLine); }
        }

        // What is in place, looked at when the settings window builds its rows and
        // when a download ends: not each frame, which would ask the file system.
        private static string installedLine;

        // Looks at what of Intel's files is in place, for the row's line.
        public static void Refresh()
        {
            try
            {
                string text = IntelFiles.InstalledText(Folders());
                installedLine = text == null ? null : "In place: " + text + ".";
            }
            catch (Exception e)
            {
                installedLine = null;
                Debug.LogWarning(Log.Tag + " Intel's files could not be looked for: " + e.Message);
            }
        }

        // Where the proxy loads each use's files from: its ini's xessDirectory and
        // xessFgDirectory, or next to KSP_x64.exe.
        public static IntelFiles.Folders Folders()
        {
            string game = NvidiaDownloader.GameFolder;
            string ini = Path.Combine(game, "ReDefinitionProxy.ini");
            return IntelFiles.FoldersOf(game, IntelFiles.IniValue(ini, "xessDirectory"),
                                        IntelFiles.IniValue(ini, "xessFgDirectory"));
        }

        // What of Intel's files the folders lack.
        public static List<IntelFiles.Item> Needed()
        {
            try
            {
                return IntelFiles.Missing(Folders(), IntelFiles.All);
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Intel's files could not be looked for: " + e.Message);
                return new List<IntelFiles.Item>();
            }
        }

        // What the files are for, in the dialog's and the row's words.
        public static string Purpose(bool superResolution, bool frameGeneration)
        {
            return superResolution && frameGeneration ? "XeSS super resolution and XeSS frame generation"
                : frameGeneration ? "XeSS frame generation"
                : "XeSS super resolution";
        }

        // When they are used: libxess.dll at once (XessUpscaler looks for it again as
        // it changes), XeSS frame generation by a swapchain made after they are there.
        public static string WhenUsed(bool superResolution, bool frameGeneration)
        {
            string later = "XeSS frame generation runs from them after a restart of KSP: on an Intel GPU with"
                           + " frameGenerationTechnique=auto, on another GPU with frameGenerationTechnique=xess in"
                           + " ReDefinitionProxy.ini.";
            return !frameGeneration ? "XeSS (DLL) can use libxess.dll at once."
                : !superResolution ? later
                : "XeSS (DLL) can use libxess.dll at once. " + later;
        }

        // The question before anything is fetched: what, from where, under which
        // licence -- a click away -- and that downloading accepts it.
        public static void Confirm(List<IntelFiles.Item> items, Action installed = null)
        {
            if (Running || items.Count == 0) return;
            download.Continuation = installed;

            bool superResolution;
            bool frameGeneration;
            IntelFiles.Uses(items, out superResolution, out frameGeneration);
            List<string> names = new List<string>();
            foreach (IntelFiles.Item item in items)
            {
                if (item.Use != IntelFiles.Use.Notice) names.Add(IntelFiles.Describe(item));
            }

            List<string> places = new List<string>();
            try
            {
                IntelFiles.Folders folders = Folders();
                if (superResolution) places.Add(folders.SuperResolution);
                if (frameGeneration && !places.Contains(folders.FrameGeneration)) places.Add(folders.FrameGeneration);
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Intel's folders could not be named: " + e.Message);
            }

            string message =
                "ReDefinition can fetch Intel's files for " + Purpose(superResolution, frameGeneration)
                + " from Intel's own release of the XeSS SDK " + IntelFiles.SdkVersion + " on GitHub, and place them where"
                + " ReDefinition's dxgi.dll loads them from"
                + (places.Count > 0 ? ": " + string.Join(" and ", places.ToArray()) : "")
                + ".\n\n"
                + string.Join(", ", names.ToArray()) + " -- " + NvidiaDownloader.Megabytes(IntelFiles.DownloadSize(items))
                + " MB in all.\n\n"
                + "These files are Intel's, and using them is subject to the " + IntelFiles.LicenceName
                + ", which the link below opens and which is placed with the files as xess.license.txt."
                + " By downloading you accept this licence, and confirm that you may: that you are of legal age, or have"
                + " a parent's or guardian's consent.\n\n"
                + "Every file is checked against Intel's release before anything is placed, and all are placed or none."
                + " A different file already there is kept, renamed to end in .old. "
                + WhenUsed(superResolution, frameGeneration);

            List<DialogGUIBase> rows = new List<DialogGUIBase>
            {
                new DialogGUIHorizontalLayout(
                    new DialogGUIFlexibleSpace(),
                    DialogOverWindow.Link("Intel Simplified Software License", () => Application.OpenURL(IntelFiles.LicenceUrl),
                        210f),
                    DialogOverWindow.Link("XeSS SDK release", () => Application.OpenURL(IntelFiles.ReleaseUrl), 130f),
                    new DialogGUIFlexibleSpace()),
                new DialogGUIHorizontalLayout(
                    new DialogGUIFlexibleSpace(),
                    new DialogGUIButton("Accept and download", () => Start(items), 200f, 30f, true),
                    new DialogGUIButton(Localizer.Format("#autoLOC_174783"), () => { download.Continuation = null; }, 120f, 30f, true),
                    new DialogGUIFlexibleSpace()),
            };
            // libxess.dll is four fifths of it: the player who wants only XeSS super
            // resolution, or only frame generation, takes that one (the dialog's text
            // names each file with its size).
            if (superResolution && frameGeneration)
            {
                List<IntelFiles.Item> superResolutionOnly = IntelFiles.Only(items, IntelFiles.Use.SuperResolution);
                List<IntelFiles.Item> frameGenerationOnly = IntelFiles.Only(items, IntelFiles.Use.FrameGeneration);
                rows.Insert(1, new DialogGUIHorizontalLayout(
                    new DialogGUIFlexibleSpace(),
                    new DialogGUIButton("Accept, super resolution only", () => Start(superResolutionOnly), 230f, 30f, true),
                    new DialogGUIButton("Accept, frame generation only", () => Start(frameGenerationOnly), 230f, 30f, true),
                    new DialogGUIFlexibleSpace()));
            }

            MultiOptionDialog dialog = new MultiOptionDialog("ReDefinitionIntelDownload", message, "Intel's XeSS files",
                HighLogic.UISkin, 520f, rows.ToArray());
            UnityMouseEvents.Shield(DialogOverWindow.Raise(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), dialog, false, HighLogic.UISkin, false)));
        }

        // Starts the download of the items, unless one runs already. libxess.dll takes effect at once; XeSS frame
        // generation's swapchain is made when KSP starts.
        private static void Start(List<IntelFiles.Item> items)
        {
            List<IntelFiles.Item> fetched = new List<IntelFiles.Item>(items);
            bool superResolution;
            bool frameGeneration;
            IntelFiles.Uses(fetched, out superResolution, out frameGeneration);
            download.Start(new ArchiveDownload.Job
            {
                Entries = IntelFiles.Entries(fetched),
                Folders = () =>
                {
                    IntelFiles.Folders folders = Folders();
                    return i => IntelFiles.FolderOf(fetched[i], folders, superResolution);
                },
                WhenUsed = WhenUsed(superResolution, frameGeneration),
                Placed = () =>
                {
                    if (frameGeneration)
                        SettingsWindow.NoteRestart("files.intel", "Intel's files for XeSS frame generation", "Intel");
                },
                Told = () =>
                {
                    if (superResolution && ReDefinitionAddon.Instance != null) ReDefinitionAddon.Instance.IntelFilesPlaced();
                },
            });
        }
    }
}
