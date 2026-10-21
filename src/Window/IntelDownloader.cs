using System.Collections.Generic;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Threading;
using System;
using KSP.Localization;
using ReDefinition.Bridges;
using ReDefinition.Core;
using UnityEngine.Networking;
using UnityEngine;

namespace ReDefinition.Window
{
    // The player's download of Intel's DLLs (IntelFiles) from Intel's own release
    // of the XeSS SDK on GitHub, as NvidiaDownloader does NVIDIA's: offered in the
    // settings window where they are missing, and started once the player has
    // accepted Intel's licence in the dialog below.
    //
    // First every file's bytes of the archive, by range, each into a file of its
    // own in the system's temporary folder; then, on a thread of its own, every
    // file inflated and checked into a partial file beside its target; only when
    // all of them are right are they placed, all or none (NvidiaFiles.PlaceAll).
    // libxess.dll takes effect at once -- the proxy looks for it again as it
    // changes --, XeSS frame generation after a restart: its DLLs are loaded as KSP
    // makes its swapchain.
    internal static class IntelDownloader
    {
        public static bool Running { get; private set; }

        // Placed this session: the row then offers nothing more.
        public static bool Installed { get; private set; }

        private static string status;
        private static volatile bool downloading;
        private static long received;
        private static long total;
        private static long shownTenths = -1;
        private static string shownProgress;

        // What the settings row says below its button: the download's progress or
        // outcome, else the DLLs in place with their versions, null for neither. The
        // progress text is made anew only when its figure changes: the row asks
        // every frame.
        public static string Status
        {
            get
            {
                if (!downloading) return status ?? installedLine;
                long tenths = Interlocked.Read(ref received) * 10 / (1024 * 1024);
                if (tenths != shownTenths)
                {
                    shownTenths = tenths;
                    shownProgress = "Downloading " + (tenths / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + " of "
                                    + NvidiaDownloader.Megabytes(total) + " MB ...";
                }
                return shownProgress;
            }
        }

        // What is in place, looked at when the settings window builds its rows and
        // when a download ends: not each frame, which would ask the file system.
        private static string installedLine;

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
        public static void Confirm(List<IntelFiles.Item> items)
        {
            if (Running || items.Count == 0) return;

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
                    new DialogGUIButton(Localizer.Format("#autoLOC_174783"), () => { }, 120f, 30f, true),
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

        private static void Start(List<IntelFiles.Item> items)
        {
            if (Running || ReDefinitionAddon.Instance == null) return;
            Running = true;
            ReDefinitionAddon.Instance.StartCoroutine(Guarded(Run(new List<IntelFiles.Item>(items))));
        }

        // The download's range files, for Guarded's clean-up.
        private static List<string> activeRangeFiles = new List<string>();

        // The download, ended with Finish whatever it throws: Unity stops a
        // coroutine that throws without running anything after it, and the button
        // would stay greyed out until KSP restarts.
        private static IEnumerator Guarded(IEnumerator steps)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!steps.MoveNext()) yield break;
                    current = steps.Current;
                }
                catch (Exception e)
                {
                    if (Running) Finish("Download failed: " + e.Message + ".", activeRangeFiles);
                    yield break;
                }
                yield return current;
            }
        }

        private static IEnumerator Run(List<IntelFiles.Item> items)
        {
            Installed = false;
            Interlocked.Exchange(ref received, 0);
            total = IntelFiles.DownloadSize(items);
            shownTenths = -1;
            status = null;
            downloading = true;
            List<NvidiaFiles.Entry> entries = IntelFiles.Entries(items);

            IntelFiles.Folders folders = default(IntelFiles.Folders);
            List<string> rangeFiles = new List<string>();
            activeRangeFiles = rangeFiles;
            try
            {
                folders = Folders();
                string temporary = Path.GetTempPath();
                foreach (NvidiaFiles.Entry entry in entries)
                    rangeFiles.Add(Path.Combine(temporary, "ReDefinition-" + entry.FileName + ".range"));
            }
            catch (Exception e)
            {
                Finish("Download failed, nothing was changed: " + e.Message + ".", rangeFiles);
                yield break;
            }
            Debug.Log(Log.Tag + " Downloading " + entries.Count + " of Intel's files (" + NvidiaDownloader.Megabytes(total)
                      + " MB) from " + IntelFiles.ArchiveUrl + ", the player having accepted Intel's licence.");

            for (int i = 0; i < entries.Count; i++)
            {
                NvidiaFiles.Entry entry = entries[i];
                NvidiaDownloader.RangeHandler handler;
                try
                {
                    handler = new NvidiaDownloader.RangeHandler(rangeFiles[i], entry.RangeLength,
                        n => Interlocked.Add(ref received, n));
                }
                catch (Exception e)
                {
                    Finish("Download failed, nothing was changed: " + entry.FileName + ": " + e.Message + ".", rangeFiles);
                    yield break;
                }

                // Closed on every way out, a throw included: an open range file could
                // not be deleted.
                try
                {
                    using (UnityWebRequest request = new UnityWebRequest(IntelFiles.ArchiveUrl, UnityWebRequest.kHttpVerbGET,
                               handler, null))
                    {
                        request.SetRequestHeader("Range", "bytes=" + entry.RangeStart + "-" + entry.RangeEnd);
                        // A stalled connection ends here rather than never: long enough
                        // for the file at a minute per megabyte.
                        request.timeout = (int)Math.Max(1800L, entry.RangeLength / (1024 * 1024) * 60);
                        yield return request.SendWebRequest();

                        handler.Close();
                        string failure = null;
                        if (handler.Overflow || request.responseCode == 200)
                            failure = "the server sent more than the part of Intel's archive that was asked for";
                        else if (handler.WriteError != null)
                            failure = handler.WriteError;
                        else if (request.isNetworkError || request.isHttpError)
                            failure = request.error + (request.responseCode != 0 ? " (HTTP " + request.responseCode + ")" : "");
                        else if (request.responseCode != 206 || handler.Length != entry.RangeLength)
                            failure = "HTTP " + request.responseCode + ", " + handler.Length + " bytes of " + entry.RangeLength;
                        if (failure != null)
                        {
                            Finish("Download failed, nothing was changed: " + entry.FileName + ": " + failure + ".", rangeFiles);
                            yield break;
                        }
                    }
                }
                finally
                {
                    handler.Close();
                }
            }
            downloading = false;

            // Inflated, checked and placed on a thread: 78 MB of libxess.dll would
            // otherwise hold the game for a moment.
            status = "Checking Intel's files ...";
            string error = null;
            List<string> backups = null;
            bool done = false;
            bool superResolutionFetched;
            bool frameGenerationFetched;
            IntelFiles.Uses(items, out superResolutionFetched, out frameGenerationFetched);
            Thread worker = new Thread(() =>
            {
                List<NvidiaFiles.Placement> placements = new List<NvidiaFiles.Placement>();
                try
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        string target = Path.Combine(IntelFiles.FolderOf(items[i], folders, superResolutionFetched),
                                                     items[i].Entry.FileName);
                        NvidiaFiles.Placement placement = new NvidiaFiles.Placement
                        {
                            Entry = items[i].Entry,
                            Target = target,
                            Partial = NvidiaFiles.PartialPath(target),
                        };
                        placements.Add(placement);
                        // A folder the ini names may not exist yet.
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (FileStream range = File.OpenRead(rangeFiles[i]))
                        using (FileStream output = File.Create(placement.Partial))
                            NvidiaFiles.Extract(items[i].Entry, range, output);
                    }
                    backups = NvidiaFiles.PlaceAll(placements);
                }
                catch (Exception e)
                {
                    error = e.Message;
                    foreach (NvidiaFiles.Placement placement in placements)
                    {
                        try
                        {
                            if (!placement.Placed && File.Exists(placement.Partial)) File.Delete(placement.Partial);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                done = true;
            });
            worker.IsBackground = true;
            worker.Start();
            while (!Volatile.Read(ref done)) yield return null;

            if (error != null)
            {
                Finish("Download failed: " + error + ".", rangeFiles);
                yield break;
            }

            Installed = true;
            Finish("Installed. " + WhenUsed(superResolutionFetched, frameGenerationFetched)
                   + (backups != null && backups.Count > 0 ? " Kept before: " + string.Join(", ", backups.ToArray()) + "." : ""),
                   rangeFiles);
            if (superResolutionFetched && ReDefinitionAddon.Instance != null) ReDefinitionAddon.Instance.IntelFilesPlaced();
        }

        private static void Finish(string outcome, List<string> rangeFiles)
        {
            foreach (string file in rangeFiles)
            {
                try
                {
                    if (File.Exists(file)) File.Delete(file);
                }
                catch (Exception)
                {
                }
            }
            downloading = false;
            Running = false;
            status = outcome;
            Refresh();
            if (Installed) Debug.Log(Log.Tag + " Intel's files: " + outcome);
            else Debug.LogWarning(Log.Tag + " Intel's files: " + outcome);
            ScreenMessages.PostScreenMessage("Intel's XeSS files: " + outcome, 8f);
        }
    }
}
