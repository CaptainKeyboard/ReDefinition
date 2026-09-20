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
        public static bool Running { get; private set; }

        // Placed this session: until KSP is restarted, DLSS frame generation does not
        // run from them.
        public static bool Installed { get; private set; }

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

        // Which uses the entries serve.
        public static void Uses(IEnumerable<NvidiaFiles.Entry> entries, out bool dlss, out bool frameGeneration)
        {
            dlss = false;
            frameGeneration = false;
            foreach (NvidiaFiles.Entry entry in entries)
            {
                dlss |= entry.Use == NvidiaFiles.Use.Dlss;
                frameGeneration |= entry.Use == NvidiaFiles.Use.FrameGeneration;
            }
        }

        // What the files are for, in the dialog's and the row's words.
        public static string Purpose(bool dlss, bool frameGeneration)
        {
            return dlss && frameGeneration ? "DLSS and DLSS frame generation"
                : frameGeneration ? "DLSS frame generation"
                : "DLSS";
        }

        // When they are used: nvngx_dlss.dll at once (ReDefinitionAddon.NvidiaFilesPlaced),
        // Streamline only by a swapchain made after it is there -- KSP makes one as
        // it starts.
        public static string WhenUsed(bool dlss, bool frameGeneration)
        {
            return !frameGeneration ? "DLSS can use them at once."
                : !dlss ? "DLSS frame generation runs from them after a restart of KSP."
                : "DLSS can use them at once, DLSS frame generation after a restart of KSP.";
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
                return NvidiaFiles.Missing(Folders(), NvidiaFiles.For(architecture, implementation));
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " NVIDIA's files could not be looked for: " + e.Message);
                return new List<NvidiaFiles.Entry>();
            }
        }

        public static string Megabytes(long bytes)
        {
            return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);
        }

        // The question before anything is fetched: what, from where, under which of
        // NVIDIA's licences -- each one a click away -- and that downloading accepts
        // them.
        public static void Confirm(List<NvidiaFiles.Entry> entries)
        {
            if (Running || entries.Count == 0) return;

            bool dlss;
            bool frameGeneration;
            Uses(entries, out dlss, out frameGeneration);
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
                if (frameGeneration && !places.Contains(folders.FrameGeneration)) places.Add(folders.FrameGeneration);
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " NVIDIA's folders could not be named: " + e.Message);
            }

            string message =
                "ReDefinition can fetch NVIDIA's files for " + Purpose(dlss, frameGeneration)
                + " on this GPU from NVIDIA's own release of the Streamline SDK 2.14.1 on GitHub, and place them where"
                + " ReDefinition's dxgi.dll loads them from"
                + (places.Count > 0 ? ": " + string.Join(" and ", places.ToArray()) : "")
                + ".\n\n"
                + string.Join(", ", names.ToArray()) + " -- " + Megabytes(NvidiaFiles.DownloadSize(entries)) + " MB.\n\n"
                + "These files are NVIDIA's, and using them is subject to NVIDIA's"
                + " licences: "
                + (frameGeneration
                    ? "the NVIDIA RTX SDKs License, NVIDIA's SDK license agreement for Reflex, Streamline's MIT license"
                      + " and the licences of the third-party parts Streamline lists."
                    : "the NVIDIA RTX SDKs License.")
                + " Their texts are placed with the files. By downloading you accept these licences, and confirm that"
                + " you may: that you are of legal age, or have a parent's or guardian's consent.\n\n"
                + "Every file is checked against NVIDIA's release before anything is placed, and all are placed or none."
                + " A different file already there is kept, renamed to end in .old. " + WhenUsed(dlss, frameGeneration)
                + (frameGeneration
                    ? " DLSS frame generation also needs hardware-accelerated GPU scheduling on in Windows' graphics"
                      + " settings."
                    : "");

            List<DialogGUIBase> buttons = new List<DialogGUIBase>
            {
                new DialogGUIButton("RTX SDKs License", () => Application.OpenURL(NvidiaFiles.RtxLicenceUrl), false),
            };
            if (frameGeneration)
            {
                buttons.Add(new DialogGUIButton("Reflex license", () => Application.OpenURL(NvidiaFiles.ReflexLicenceUrl),
                    false));
                buttons.Add(new DialogGUIButton("Streamline license",
                    () => Application.OpenURL(NvidiaFiles.StreamlineLicenceUrl), false));
                buttons.Add(new DialogGUIButton("Third-party licenses",
                    () => Application.OpenURL(NvidiaFiles.ThirdPartyLicencesUrl), false));
            }
            buttons.Add(new DialogGUIButton("Accept and download", () => Start(entries), true));
            buttons.Add(new DialogGUIButton(Localizer.Format("#autoLOC_149514"), () => { }, true));

            MultiOptionDialog dialog = new MultiOptionDialog("ReDefinitionNvidiaDownload", message, "NVIDIA's DLSS files",
                HighLogic.UISkin, 520f, buttons.ToArray());
            UnityMouseEvents.Shield(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                dialog, false, HighLogic.UISkin));
        }

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
                    handler = new RangeHandler(rangeFiles[i], entry.RangeLength);
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
            bool frameGenerationFetched;
            Uses(entries, out dlssFetched, out frameGenerationFetched);
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
            Finish("Installed. " + WhenUsed(dlssFetched, frameGenerationFetched)
                   + (backups != null && backups.Count > 0 ? " Kept before: " + string.Join(", ", backups.ToArray()) + "." : ""),
                   rangeFiles);
            if (dlssFetched && ReDefinitionAddon.Instance != null) ReDefinitionAddon.Instance.NvidiaFilesPlaced();
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
            if (Installed) Debug.Log(Log.Tag + " NVIDIA's files: " + outcome);
            else Debug.LogWarning(Log.Tag + " NVIDIA's files: " + outcome);
            ScreenMessages.PostScreenMessage("NVIDIA's DLSS files: " + outcome, 8f);
        }

        // The part of the archive for one file, into a temporary file: a server that
        // sends more than asked for -- the whole archive, having ignored the range --
        // is stopped at once.
        private sealed class RangeHandler : DownloadHandlerScript
        {
            private readonly long expected;
            private FileStream file;

            public RangeHandler(string path, long expected) : base(new byte[64 * 1024])
            {
                this.expected = expected;
                file = File.Create(path);
            }

            public long Length { get; private set; }
            public bool Overflow { get; private set; }
            public string WriteError { get; private set; }

            public void Close()
            {
                if (file == null) return;
                file.Dispose();
                file = null;
            }

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
                Interlocked.Add(ref received, dataLength);
                return true;
            }
        }
    }
}
