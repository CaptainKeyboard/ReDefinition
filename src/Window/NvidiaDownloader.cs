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
    // The player's download of NVIDIA's DLLs (NvidiaFiles) from NVIDIA's own
    // release on GitHub: offered in the settings window where the GPU can use them
    // and they are missing, and started once the player has accepted NVIDIA's
    // licences in the dialog below.
    //
    // First every file's bytes of the archive, by range, each into a file of its
    // own in the system's temporary folder; then, on a thread of its own, every
    // file inflated and checked into a partial file beside its target; only when
    // all of them are right are they placed, all or none (NvidiaFiles.PlaceAll). DLSS
    // takes them at once; Streamline is loaded as KSP starts, so frame generation
    // after a restart.
    internal static class NvidiaDownloader
    {
        // Whether a download, its check or its placing is under way: a second one is not started meanwhile.
        public static bool Running { get; private set; }

        // Placed this session: until KSP is restarted, DLSS frame generation does not
        // run from them.
        public static bool Installed { get; private set; }

        // The outcome for the row; whether bytes are coming in; the bytes received (from the download's thread) and those
        // expected; and the progress text with the tenth of a megabyte it was made for.
        private static string status;
        private static volatile bool downloading;
        private static long received;
        private static long total;
        private static long shownTenths = -1;
        private static string shownProgress;

        // What the settings row says: the download's progress or outcome, null
        // before one. The progress text is made anew only when its figure changes:
        // the row asks every frame.
        public static string Status
        {
            get
            {
                if (!downloading) return status;
                long tenths = Interlocked.Read(ref received) * 10 / (1024 * 1024);
                if (tenths != shownTenths)
                {
                    shownTenths = tenths;
                    shownProgress = "Downloading " + (tenths / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + " of "
                                    + Megabytes(total) + " MB ...";
                }
                return shownProgress;
            }
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
            onInstalled = installed;

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
                    new DialogGUIButton(Localizer.Format("#autoLOC_174783"), () => { onInstalled = null; }, 120f, 30f, true),
                    new DialogGUIFlexibleSpace()),
            };

            MultiOptionDialog dialog = new MultiOptionDialog("ReDefinitionNvidiaDownload", message, "NVIDIA's DLSS files",
                HighLogic.UISkin, 520f, rows);
            UnityMouseEvents.Shield(DialogOverWindow.Raise(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), dialog, false, HighLogic.UISkin, false)));
        }

        // What waits for the files: called when they are in place, dropped where the player cancels.
        private static Action onInstalled;

        // Starts the download as a coroutine on the add-on, unless one runs already.
        private static void Start(List<NvidiaFiles.Entry> entries)
        {
            if (Running || ReDefinitionAddon.Instance == null) return;
            Running = true;
            ReDefinitionAddon.Instance.StartCoroutine(Guarded(Run(new List<NvidiaFiles.Entry>(entries))));
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

        // Fetches every file's range from the archive, then inflates, checks and places them on a thread; any failure
        // ends it through Finish with nothing changed.
        private static IEnumerator Run(List<NvidiaFiles.Entry> entries)
        {
            Installed = false;
            Interlocked.Exchange(ref received, 0);
            total = NvidiaFiles.DownloadSize(entries);
            shownTenths = -1;
            downloading = true;

            NvidiaFiles.Folders folders = default(NvidiaFiles.Folders);
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
            Debug.Log(Log.Tag + " Downloading " + entries.Count + " of NVIDIA's files (" + Megabytes(total)
                      + " MB) from " + NvidiaFiles.ArchiveUrl + ", the player having accepted NVIDIA's licences.");

            for (int i = 0; i < entries.Count; i++)
            {
                NvidiaFiles.Entry entry = entries[i];
                RangeHandler handler;
                try
                {
                    handler = new RangeHandler(rangeFiles[i], entry.RangeLength, n => Interlocked.Add(ref received, n));
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
                    using (UnityWebRequest request = new UnityWebRequest(NvidiaFiles.ArchiveUrl, UnityWebRequest.kHttpVerbGET,
                               handler, null))
                    {
                        request.SetRequestHeader("Range", "bytes=" + entry.RangeStart + "-" + entry.RangeEnd);
                        // A stalled connection ends here rather than never: long enough
                        // for 30 MB over a slow line.
                        request.timeout = 1800;
                        yield return request.SendWebRequest();

                        handler.Close();
                        string failure = null;
                        if (handler.Overflow || request.responseCode == 200)
                            failure = "the server sent more than the part of NVIDIA's archive that was asked for";
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

            // Inflated, checked and placed on a thread: 59 MB of DLSS would otherwise
            // hold the game for a moment.
            status = "Checking NVIDIA's files ...";
            string error = null;
            List<string> backups = null;
            bool done = false;
            bool dlssFetched;
            bool streamlineFetched;
            bool frameGenerationFetched;
            Uses(entries, out dlssFetched, out streamlineFetched, out frameGenerationFetched);
            Thread worker = new Thread(() =>
            {
                List<NvidiaFiles.Placement> placements = new List<NvidiaFiles.Placement>();
                try
                {
                    for (int i = 0; i < entries.Count; i++)
                    {
                        string target = Path.Combine(NvidiaFiles.FolderOf(entries[i], folders, dlssFetched),
                                                     entries[i].FileName);
                        NvidiaFiles.Placement placement = new NvidiaFiles.Placement
                        {
                            Entry = entries[i],
                            Target = target,
                            Partial = NvidiaFiles.PartialPath(target),
                        };
                        placements.Add(placement);
                        // A folder the ini names may not exist yet.
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (FileStream range = File.OpenRead(rangeFiles[i]))
                        using (FileStream output = File.Create(placement.Partial))
                            NvidiaFiles.Extract(entries[i], range, output);
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
            // Streamline loads its plugins when KSP starts: Ray Reconstruction and DLSS frame generation wait for it.
            if (streamlineFetched || frameGenerationFetched)
                SettingsWindow.NoteRestart("files.nvidia", "NVIDIA's files for Ray Reconstruction and frame generation", "NVIDIA");
            Finish("Installed. " + WhenUsed(dlssFetched, streamlineFetched, frameGenerationFetched)
                   + (backups != null && backups.Count > 0 ? " Kept before: " + string.Join(", ", backups.ToArray()) + "." : ""),
                   rangeFiles);
            if (dlssFetched && ReDefinitionAddon.Instance != null) ReDefinitionAddon.Instance.NvidiaFilesPlaced();
        }

        // Ends the download: the range files deleted, the outcome logged, put on the screen and kept for the row, and the
        // continuation called where the files are in place.
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
            if (Installed) Debug.Log(Log.Tag + " NVIDIA's files: " + outcome);
            else Debug.LogWarning(Log.Tag + " NVIDIA's files: " + outcome);
            ScreenMessages.PostScreenMessage("NVIDIA's DLSS files: " + outcome, 8f);
            Action finished = onInstalled;
            onInstalled = null;
            if (Installed && finished != null) finished();
        }

        // The part of the archive for one file, into a temporary file: a server that
        // sends more than asked for -- the whole archive, having ignored the range --
        // is stopped at once. IntelDownloader fetches the same way.
        internal sealed class RangeHandler : DownloadHandlerScript
        {
            // The bytes asked for, what each piece is reported to, and the temporary file, closed once the request is done.
            private readonly long expected;
            private readonly Action<int> onData;
            private FileStream file;

            // onData is told how many bytes each piece held, for the download's progress.
            public RangeHandler(string path, long expected, Action<int> onData) : base(new byte[64 * 1024])
            {
                this.expected = expected;
                this.onData = onData;
                file = File.Create(path);
            }

            // The bytes written so far.
            public long Length { get; private set; }
            // Whether the server sent more than asked for.
            public bool Overflow { get; private set; }
            // Why writing the file failed; null where it did not.
            public string WriteError { get; private set; }

            // Closes the temporary file, once.
            public void Close()
            {
                if (file == null) return;
                file.Dispose();
                file = null;
            }

            // Writes a piece; false stops the request where it would go past the range or the write fails.
            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength <= 0) return true;
                if (file == null || Length + dataLength > expected)
                {
                    Overflow = file != null;
                    return false;
                }
                try
                {
                    file.Write(data, 0, dataLength);
                }
                catch (Exception e)
                {
                    WriteError = e.Message;
                    return false;
                }
                Length += dataLength;
                onData(dataLength);
                return true;
            }
        }
    }
}
