using System.Collections.Generic;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Threading;
using System;
using ReDefinition.Core;
using ReDefinition.Window;
using UnityEngine.Networking;
using UnityEngine;

namespace ReDefinition.Vendor
{
    // The download NvidiaDownloader and IntelDownloader share.
    // It fetches the files of a vendor's release archive on GitHub, each by range into a file of its own in the
    // system's temporary folder. Then it inflates, checks and places them on a thread of its own, all or none
    // (NvidiaFiles.PlaceAll).
    // The downloaders differ in what they fetch, where they place it and what they say.
    // They differ in how long a stalled request may last and in what they do once the files are in place.
    // An instance holds the progress of one vendor's download.
    internal sealed class ArchiveDownload
    {
        // What one download fetches and does: the files, where each is placed, and what follows.
        internal sealed class Job
        {
            // The archive entries to fetch, in the order their files are placed.
            public List<NvidiaFiles.Entry> Entries;
            // Names the folders the files go to, once the download starts, and gives each entry's folder by its index;
            // it throws where the folders cannot be named, which ends the download with nothing changed.
            public Func<Func<int, string>> Folders;
            // The sentence the outcome starts with once the files are in place: when they are used.
            public string WhenUsed;
            // Called when the files are in place, before the outcome is told, and after it.
            public Action Placed;
            public Action Told;
        }

        // Who the files are from, in the texts ("NVIDIA's"), the archive's URL, the licence the player accepted as the log
        // names it, and the screen message's lead.
        private readonly string owner;
        private readonly string archiveUrl;
        private readonly string licence;
        private readonly string screenLead;
        // How long a stalled request may last in seconds, by the length of the range it fetches.
        private readonly Func<long, int> timeout;
        // Called when the outcome is kept, before it is logged.
        private readonly Action outcomeKept;

        // The outcome for the row; whether bytes are coming in; the bytes received (from the download's thread) and those
        // expected; and the progress text with the tenth of a megabyte it was made for.
        private string status;
        private volatile bool downloading;
        private long received;
        private long total;
        private long shownTenths = -1;
        private string shownProgress;

        // The download's range files, for Guarded's clean-up.
        private List<string> activeRangeFiles = new List<string>();

        // What waits for the files: called when they are in place, dropped where the player cancels.
        public Action Continuation;

        // A download of that vendor's files. `outcomeKept` may be null.
        public ArchiveDownload(string owner, string archiveUrl, string licence, string screenLead, Func<long, int> timeout,
                               Action outcomeKept)
        {
            this.owner = owner;
            this.archiveUrl = archiveUrl;
            this.licence = licence;
            this.screenLead = screenLead;
            this.timeout = timeout;
            this.outcomeKept = outcomeKept;
        }

        // Whether a download, its check or its placing is under way: a second one is not started meanwhile.
        public bool Running { get; private set; }

        // Placed this session.
        public bool Installed { get; private set; }

        // What the settings row says: the download's progress or outcome, `idle` before one. The progress text is made
        // anew only when its figure changes: the row asks every frame.
        public string Status(string idle)
        {
            if (!downloading) return status ?? idle;
            long tenths = Interlocked.Read(ref received) * 10 / (1024 * 1024);
            if (tenths != shownTenths)
            {
                shownTenths = tenths;
                shownProgress = "Downloading " + (tenths / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + " of "
                                + NvidiaDownloader.Megabytes(total) + " MB ...";
            }
            return shownProgress;
        }

        // Starts the download as a coroutine on the add-on, unless one runs already.
        public void Start(Job job)
        {
            if (Running || ReDefinitionAddon.Instance == null) return;
            Running = true;
            ReDefinitionAddon.Instance.StartCoroutine(Guarded(Run(job)));
        }

        // The download, ended with Finish whatever it throws: Unity stops a
        // coroutine that throws without running anything after it, and the button
        // would stay greyed out until KSP restarts.
        private IEnumerator Guarded(IEnumerator steps)
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
        private IEnumerator Run(Job job)
        {
            List<NvidiaFiles.Entry> entries = job.Entries;
            Installed = false;
            Interlocked.Exchange(ref received, 0);
            total = NvidiaFiles.DownloadSize(entries);
            shownTenths = -1;
            status = null;
            downloading = true;

            Func<int, string> folderOf = null;
            List<string> rangeFiles = new List<string>();
            activeRangeFiles = rangeFiles;
            try
            {
                folderOf = job.Folders();
                string temporary = Path.GetTempPath();
                foreach (NvidiaFiles.Entry entry in entries)
                    rangeFiles.Add(Path.Combine(temporary, "ReDefinition-" + entry.FileName + ".range"));
            }
            catch (Exception e)
            {
                Finish("Download failed, nothing was changed: " + e.Message + ".", rangeFiles);
                yield break;
            }
            Debug.Log(Log.Tag + " Downloading " + entries.Count + " of " + owner + " files (" + NvidiaDownloader.Megabytes(total)
                      + " MB) from " + archiveUrl + ", the player having accepted " + licence + ".");

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
                    using (UnityWebRequest request = new UnityWebRequest(archiveUrl, UnityWebRequest.kHttpVerbGET,
                               handler, null))
                    {
                        request.SetRequestHeader("Range", "bytes=" + entry.RangeStart + "-" + entry.RangeEnd);
                        // A stalled connection ends here rather than never.
                        request.timeout = timeout(entry.RangeLength);
                        yield return request.SendWebRequest();

                        handler.Close();
                        string failure = null;
                        if (handler.Overflow || request.responseCode == 200)
                            failure = "the server sent more than the part of " + owner + " archive that was asked for";
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

            // Inflated, checked and placed on a thread: the DLLs would otherwise hold the game for a moment.
            status = "Checking " + owner + " files ...";
            string error = null;
            List<string> backups = null;
            bool done = false;
            Thread worker = new Thread(() =>
            {
                List<NvidiaFiles.Placement> placements = new List<NvidiaFiles.Placement>();
                try
                {
                    for (int i = 0; i < entries.Count; i++)
                    {
                        string target = Path.Combine(folderOf(i), entries[i].FileName);
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
            if (job.Placed != null) job.Placed();
            Finish("Installed. " + job.WhenUsed
                   + (backups != null && backups.Count > 0 ? " Kept before: " + string.Join(", ", backups.ToArray()) + "." : ""),
                   rangeFiles);
            if (job.Told != null) job.Told();
        }

        // Ends the download: the range files deleted, the outcome logged, put on the screen and kept for the row, and the
        // continuation called where the files are in place.
        private void Finish(string outcome, List<string> rangeFiles)
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
            if (outcomeKept != null) outcomeKept();
            if (Installed) Debug.Log(Log.Tag + " " + owner + " files: " + outcome);
            else Debug.LogWarning(Log.Tag + " " + owner + " files: " + outcome);
            ScreenMessages.PostScreenMessage(screenLead + ": " + outcome, 8f);
            Action finished = Continuation;
            Continuation = null;
            if (Installed && finished != null) finished();
        }

        // The part of the archive for one file, into a temporary file: a server that
        // sends more than asked for -- the whole archive, having ignored the range --
        // is stopped at once.
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
