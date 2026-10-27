using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System;
using KSP.Localization;
using ReDefinition.Proxy;
using ReDefinition.Core;
using ReDefinition.Ui;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition.Vendor
{
    // The player's download of NVIDIA's DLLs (NvidiaFiles) from NVIDIA's own
    // release on GitHub. It is offered in the settings window where the GPU can use them
    // and they are missing, and started once the player has accepted NVIDIA's
    // licences in the dialog below.
    //
    // The download first fetches every file's bytes of the archive, by range, each into a file of its
    // own in the system's temporary folder. Then, on a thread of its own, it inflates and
    // checks every file into a partial file beside its target. It places them only when
    // all of them are right, all or none (NvidiaFiles.PlaceAll). DLSS
    // takes them at once; Streamline is loaded as KSP starts, so frame generation
    // after a restart.
    internal static class NvidiaDownloader
    {
        // The download this class describes to the player: what it fetches and says, and the progress it shows.
        // Nothing is fetched until the player has accepted NVIDIA's licences (Confirm). A stalled connection ends after
        // 1800 seconds, long enough for 30 MB over a slow line.
        private static readonly ArchiveDownload download = new ArchiveDownload("NVIDIA's", NvidiaFiles.ArchiveUrl,
            "NVIDIA's licences", "NVIDIA's DLSS files", length => 1800, null);

        // Whether a download, its check or its placing is under way: a second one is not started meanwhile.
        public static bool Running
        {
            get { return download.Running; }
        }

        // Placed this session: until KSP is restarted, DLSS frame generation does not
        // run from them.
        public static bool Installed
        {
            get { return download.Installed; }
        }

        // What the settings row says: the download's progress or outcome, null before one.
        public static string Status
        {
            get { return download.Status(null); }
        }

        // KSP's folder, beside KSP_x64.exe.
        public static string GameFolder
        {
            get { return Path.GetFullPath(KSPUtil.ApplicationRootPath); }
        }

        // Where the proxy loads each use's files from: its ini's folders, or next to
        // KSP_x64.exe -- where NGX looks for nvngx_dlss.dll in any case.
        public static NvidiaFiles.Folders Folders()
        {
            string dlss;
            string streamline;
            FrameGenerationBridge.NvidiaDirectories(out dlss, out streamline);
            string game = GameFolder;
            return new NvidiaFiles.Folders
            {
                Dlss = string.IsNullOrEmpty(dlss) ? game : dlss,
                DlssAlso = game,
                FrameGeneration = string.IsNullOrEmpty(streamline) ? game : streamline,
            };
        }

        // Which uses the entries serve: DLSS, Streamline (Ray Reconstruction, and frame
        // generation's base) and frame generation.
        public static void Uses(IEnumerable<NvidiaFiles.Entry> entries, out bool dlss, out bool streamline,
                                out bool frameGeneration)
        {
            dlss = false;
            streamline = false;
            frameGeneration = false;
            foreach (NvidiaFiles.Entry entry in entries)
            {
                if (entry.Notice) continue;
                dlss |= entry.Use == NvidiaFiles.Use.Dlss;
                streamline |= entry.Use == NvidiaFiles.Use.Streamline || entry.Use == NvidiaFiles.Use.FrameGeneration;
                frameGeneration |= entry.Use == NvidiaFiles.Use.FrameGeneration;
            }
        }

        // What the files are for, in the dialog's and the row's words.
        public static string Purpose(bool dlss, bool streamline, bool frameGeneration)
        {
            List<string> uses = new List<string>();
            if (dlss) uses.Add("DLSS");
            if (frameGeneration) uses.Add("DLSS frame generation");
            if (streamline) uses.Add("DLSS Ray Reconstruction");
            if (uses.Count == 0) return "DLSS";
            return uses.Count == 1 ? uses[0]
                : string.Join(", ", uses.GetRange(0, uses.Count - 1).ToArray()) + " and " + uses[uses.Count - 1];
        }

        // When they are used: nvngx_dlss.dll at once (ReDefinitionAddon.NvidiaFilesPlaced),
        // Streamline only by a swapchain made after it is there -- KSP makes one as
        // it starts. Ray Reconstruction without frame generation only with
        // frameGeneration=0 in ReDefinitionProxy.ini.
        public static string WhenUsed(bool dlss, bool streamline, bool frameGeneration)
        {
            string later = frameGeneration ? "DLSS frame generation and Ray Reconstruction run from them after a restart"
                                             + " of KSP."
                : "DLSS Ray Reconstruction runs from them after a restart of KSP, with frameGeneration=0 in"
                  + " ReDefinitionProxy.ini on this GPU.";
            return !streamline ? "DLSS can use them at once."
                : !dlss ? later
                : "DLSS can use them at once. " + later;
        }

        // What this GPU can use and its folders lack.
        public static List<NvidiaFiles.Entry> Needed()
        {
            uint architecture;
            uint implementation;
            if (!FrameGenerationBridge.NvidiaGpu(out architecture, out implementation))
                return new List<NvidiaFiles.Entry>();
            try
            {
                return NvidiaFiles.Missing(Folders(), NvidiaFiles.For(architecture, implementation,
                    AmpereFrameGeneration.Installed()));
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " NVIDIA's files could not be looked for: " + e.Message);
                return new List<NvidiaFiles.Entry>();
            }
        }

        // A byte count in megabytes, to one decimal.
        public static string Megabytes(long bytes)
        {
            return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);
        }

        // The question before anything is fetched: what, from where, under which of
        // NVIDIA's licences -- each one a click away -- and that downloading accepts
        // them.
        public static void Confirm(List<NvidiaFiles.Entry> entries, Action installed = null)
        {
            if (Running || entries.Count == 0) return;
            download.Continuation = installed;

            bool dlss;
            bool streamline;
            bool frameGeneration;
            Uses(entries, out dlss, out streamline, out frameGeneration);
            List<string> names = new List<string>();
            foreach (NvidiaFiles.Entry entry in entries)
            {
                if (!entry.Notice) names.Add(entry.FileName);
            }

            List<string> places = new List<string>();
            try
            {
                NvidiaFiles.Folders folders = Folders();
                if (dlss) places.Add(folders.Dlss);
                if (streamline && !places.Contains(folders.FrameGeneration)) places.Add(folders.FrameGeneration);
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " NVIDIA's folders could not be named: " + e.Message);
            }

            string message =
                "ReDefinition can fetch NVIDIA's files for " + Purpose(dlss, streamline, frameGeneration)
                + " on this GPU from NVIDIA's own release of the Streamline SDK 2.14.16 on GitHub, and place them where"
                + " ReDefinition's dxgi.dll loads them from"
                + (places.Count > 0 ? ": " + string.Join(" and ", places.ToArray()) : "")
                + ".\n\n"
                + string.Join(", ", names.ToArray()) + " -- " + Megabytes(NvidiaFiles.DownloadSize(entries)) + " MB.\n\n"
                + "These files are NVIDIA's, and using them is subject to NVIDIA's"
                + " licences: "
                + (frameGeneration
                    ? "the NVIDIA RTX SDKs License, NVIDIA's SDK license agreement for Reflex, Streamline's MIT license"
                      + " and the licences of the third-party parts Streamline lists."
                    : streamline
                    ? "the NVIDIA RTX SDKs License, Streamline's MIT license and the licences of the third-party parts"
                      + " Streamline lists."
                    : "the NVIDIA RTX SDKs License.")
                + " Their texts are placed with the files. By downloading you accept these licences, and confirm that"
                + " you may: that you are of legal age, or have a parent's or guardian's consent.\n\n"
                + "Every file is checked against NVIDIA's release before anything is placed, and all are placed or none."
                + " A different file already there is kept, renamed to end in .old. "
                + WhenUsed(dlss, streamline, frameGeneration)
                + (frameGeneration
                    ? " DLSS frame generation also needs hardware-accelerated GPU scheduling on in Windows' graphics"
                      + " settings."
                    : "");

            // The licences as links in one row, the two answers as buttons below.
            List<DialogGUIBase> links = new List<DialogGUIBase>
            {
                new DialogGUIFlexibleSpace(),
                DialogOverWindow.Link("RTX SDKs License", () => Application.OpenURL(NvidiaFiles.RtxLicenceUrl), 120f),
            };
            if (frameGeneration)
                links.Add(DialogOverWindow.Link("Reflex license", () => Application.OpenURL(NvidiaFiles.ReflexLicenceUrl),
                    100f));
            if (streamline)
            {
                links.Add(DialogOverWindow.Link("Streamline license",
                    () => Application.OpenURL(NvidiaFiles.StreamlineLicenceUrl), 120f));
                links.Add(DialogOverWindow.Link("Third-party licenses",
                    () => Application.OpenURL(NvidiaFiles.ThirdPartyLicencesUrl), 130f));
            }
            links.Add(new DialogGUIFlexibleSpace());

            DialogGUIBase[] rows =
            {
                new DialogGUIHorizontalLayout(links.ToArray()),
                new DialogGUIHorizontalLayout(
                    new DialogGUIFlexibleSpace(),
                    new DialogGUIButton("Accept and download", () => Start(entries), 200f, 30f, true),
                    new DialogGUIButton(Localizer.Format("#autoLOC_174783"), () => { download.Continuation = null; }, 120f, 30f, true),
                    new DialogGUIFlexibleSpace()),
            };

            MultiOptionDialog dialog = new MultiOptionDialog("ReDefinitionNvidiaDownload", message, "NVIDIA's DLSS files",
                HighLogic.UISkin, 520f, rows);
            UnityMouseEvents.Shield(DialogOverWindow.Raise(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), dialog, false, HighLogic.UISkin, false)));
        }

        // Starts the download of the entries, unless one runs already. DLSS takes its files at once; Streamline loads its
        // plugins when KSP starts, so Ray Reconstruction and DLSS frame generation wait for a restart.
        private static void Start(List<NvidiaFiles.Entry> entries)
        {
            List<NvidiaFiles.Entry> fetched = new List<NvidiaFiles.Entry>(entries);
            bool dlss;
            bool streamline;
            bool frameGeneration;
            Uses(fetched, out dlss, out streamline, out frameGeneration);
            download.Start(new ArchiveDownload.Job
            {
                Entries = fetched,
                Folders = () =>
                {
                    NvidiaFiles.Folders folders = Folders();
                    return i => NvidiaFiles.FolderOf(fetched[i], folders, dlss);
                },
                WhenUsed = WhenUsed(dlss, streamline, frameGeneration),
                Placed = () =>
                {
                    if (streamline || frameGeneration)
                        SettingsWindow.NoteRestart("files.nvidia", "NVIDIA's files for Ray Reconstruction and frame generation",
                            "NVIDIA");
                },
                Told = () =>
                {
                    if (dlss && ReDefinitionAddon.Instance != null) ReDefinitionAddon.Instance.NvidiaFilesPlaced();
                },
            });
        }
    }
}
