using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ReDefinition.Bridges
{
    // NVIDIA's DLLs DLSS and DLSS frame generation need, as they lie in NVIDIA's
    // own release of the Streamline SDK 2.14.1 on GitHub, and what the player's
    // GPU gets of them. The player downloads them from NVIDIA's release after
    // accepting NVIDIA's licences (NvidiaDownloader).
    //
    // Every figure below was read from that release: the entry's offset, sizes and
    // name from the archive's central directory and local headers (general purpose
    // flags 0 -- no data descriptor -- and no extra field in any of them), the
    // checksums from the files of the same release, whose DLLs carry NVIDIA's valid
    // signatures. Only the entries' bytes are fetched, not the 276 MB archive; a
    // file that does not come out as the checksum says is not placed.
    //
    // Everything here is free of Unity, for the tests (NvidiaFilesTests).
    internal static class NvidiaFiles
    {
        public const string ArchiveUrl =
            "https://github.com/NVIDIA-RTX/Streamline/releases/download/v2.14.1/streamline-sdk-v2.14.1.zip";

        // The licences, in the repository at the release's tag; the same texts as
        // the notices the download puts next to the files.
        public const string RtxLicenceUrl =
            "https://github.com/NVIDIA-RTX/Streamline/blob/v2.14.1/external/ngx-sdk/license.txt";
        public const string ReflexLicenceUrl =
            "https://github.com/NVIDIA-RTX/Streamline/blob/v2.14.1/external/reflex-sdk-vk/reflex.license.txt";
        public const string StreamlineLicenceUrl = "https://github.com/NVIDIA-RTX/Streamline/blob/v2.14.1/license.txt";
        public const string ThirdPartyLicencesUrl =
            "https://github.com/NVIDIA-RTX/Streamline/blob/v2.14.1/3rd-party-licenses.md";

        // NV_GPU_ARCHITECTURE_ID and NV_GPU_ARCH_IMPLEMENTATION_ID (nvapi.h). What NGX
        // asks of the GPU, as it logged it: "minHW 0x160" for DLSS, "minHW 0x190" for
        // DLSS frame generation. Of Turing, TU102, TU104 and TU106 are the RTX 20
        // series; TU116 and TU117, the GTX 16 series, have no DLSS. Architecture ids
        // from 0xE0000000 on are Tegra's.
        public const uint Turing = 0x160;
        public const uint Ada = 0x190;
        private const uint TegraFrom = 0xE0000000;
        private static readonly uint[] RtxTuring = { 0x2, 0x4, 0x6 };

        public enum Use
        {
            // nvngx_dlss.dll: DLSS itself.
            Dlss,
            // Streamline and nvngx_dlssg.dll: DLSS frame generation.
            FrameGeneration,
            // A licence text for both.
            Both,
        }

        public sealed class Entry
        {
            public string Name;         // in the archive
            public string FileName;     // on disk
            public long Offset;         // of its local header
            public long CompressedSize;
            public long Size;
            public string Sha256;
            public Use Use;
            // A licence text, fetched with a DLL of a use it covers.
            public bool Notice;
            // Only Streamline 2.14.1 runs (Streamline.h): a file of another version
            // or content counts as missing. NGX's DLLs the player brought, whatever
            // their version, stay.
            public bool ExactVersion;

            // The bytes of the archive that hold it: local header, name, data.
            public long RangeStart { get { return Offset; } }
            public long RangeEnd { get { return Offset + 30 + Encoding.UTF8.GetByteCount(Name) + CompressedSize - 1; } }
            public long RangeLength { get { return RangeEnd - RangeStart + 1; } }
        }

        public static readonly Entry[] All =
        {
            new Entry
            {
                Name = "bin/x64/nvngx_dlss.dll", FileName = "nvngx_dlss.dll", Offset = 2277765, CompressedSize = 31123319,
                Size = 58956912, Sha256 = "3975567B8943C53ACCE397F2B72380092F84F162D00B0D2C7D08A1025C563983", Use = Use.Dlss,
            },
            new Entry
            {
                Name = "bin/x64/nvngx_dlss.license.txt", FileName = "nvngx_dlss.license.txt", Offset = 33401136,
                CompressedSize = 9256, Size = 26727,
                Sha256 = "3027F23CA5A46DD9CB8183FBD522983A86F64D7DAAC5982912BF9F214671F294", Use = Use.Both, Notice = true,
            },
            new Entry
            {
                Name = "bin/x64/sl.interposer.dll", FileName = "sl.interposer.dll", Offset = 60121610,
                CompressedSize = 284550, Size = 652928,
                Sha256 = "8C87C9499461DA561EDD529AA9BF7831D67D7B94EBB1C1A5ED54EF4934E1EA4C", Use = Use.FrameGeneration,
                ExactVersion = true,
            },
            new Entry
            {
                Name = "bin/x64/sl.common.dll", FileName = "sl.common.dll", Offset = 58700957, CompressedSize = 390298,
                Size = 843392, Sha256 = "82924A8954DD671E09351C5DE0EB87AD0EB25B944CC9F9AB955CA1D9950DE15D",
                Use = Use.FrameGeneration, ExactVersion = true,
            },
            new Entry
            {
                Name = "bin/x64/sl.dlss_g.dll", FileName = "sl.dlss_g.dll", Offset = 59635666, CompressedSize = 290880,
                Size = 636032, Sha256 = "F4A6B2B14DCC0B1485989E430D3B4E3A44AC1800B92BA1AD74F476E64FB2B09C",
                Use = Use.FrameGeneration, ExactVersion = true,
            },
            new Entry
            {
                Name = "bin/x64/sl.reflex.dll", FileName = "sl.reflex.dll", Offset = 61262806, CompressedSize = 180983,
                Size = 388736, Sha256 = "0CE9725E3E03EA9E7F81D008B57F33EE365973D2E349131C8B1C3E3378FE2DB0",
                Use = Use.FrameGeneration, ExactVersion = true,
            },
            new Entry
            {
                Name = "bin/x64/sl.pcl.dll", FileName = "sl.pcl.dll", Offset = 61095493, CompressedSize = 167265,
                Size = 360064, Sha256 = "F13D51CFA05F4CD514DF2026049E2DB8ADF359221713170AD386FD499915B582",
                Use = Use.FrameGeneration, ExactVersion = true,
            },
            new Entry
            {
                Name = "bin/x64/nvngx_dlssg.dll", FileName = "nvngx_dlssg.dll", Offset = 54975783,
                CompressedSize = 3717448, Size = 7460976,
                Sha256 = "FF6E90EB78B827927DFF5B4ECC6B1C870C2E9BCA29ED9F48C7D348CC9E170B82", Use = Use.FrameGeneration,
            },
            new Entry
            {
                Name = "bin/x64/reflex.license.txt", FileName = "reflex.license.txt", Offset = 58693284,
                CompressedSize = 7617, Size = 20504,
                Sha256 = "EBF83C07FB3B2939908C3795D887AFDE3161C89A28BA391724EFC784CE1BDABE", Use = Use.FrameGeneration,
                Notice = true,
            },
            new Entry
            {
                Name = "license.txt", FileName = "sl.license.txt", Offset = 275801866, CompressedSize = 883, Size = 1591,
                Sha256 = "7B6F23E7D6F3AD6292F9308D2B42CDC3D82AE4E9B2ABB55F230279D83BEDD43D", Use = Use.FrameGeneration,
                Notice = true,
            },
            new Entry
            {
                Name = "3rd-party-licenses.md", FileName = "sl.3rd-party-licenses.md", Offset = 275782640,
                CompressedSize = 2574, Size = 14682,
                Sha256 = "CB251639994465F31D2178C16ACEDF1FF4F9CEF1C370A09782ED36D4C86C1DCA", Use = Use.FrameGeneration,
                Notice = true,
            },
        };

        public static bool DlssRuns(uint architecture, uint implementation)
        {
            if (architecture == 0 || architecture >= TegraFrom) return false;
            return architecture > Turing || architecture == Turing && Array.IndexOf(RtxTuring, implementation) >= 0;
        }

        public static bool FrameGenerationRuns(uint architecture)
        {
            return architecture >= Ada && architecture < TegraFrom;
        }

        // What a GPU can use: DLSS on an RTX GPU from Turing on, frame generation from
        // Ada on, and the licence texts of what it gets. Nothing for another GPU, or
        // an unknown one (0).
        public static List<Entry> For(uint architecture, uint implementation)
        {
            bool dlss = DlssRuns(architecture, implementation);
            bool frameGeneration = FrameGenerationRuns(architecture);
            List<Entry> entries = new List<Entry>();
            foreach (Entry entry in All)
            {
                bool wanted = entry.Use == Use.Dlss ? dlss
                    : entry.Use == Use.FrameGeneration ? frameGeneration
                    : dlss || frameGeneration;
                if (wanted) entries.Add(entry);
            }
            return entries;
        }

        // Where each use's files lie: where the proxy loads them from, and where the
        // download puts them. NGX searches a second folder for nvngx_dlss.dll after
        // the first, the one next to KSP_x64.exe (Dlss.cpp), where one found counts
        // too.
        public struct Folders
        {
            public string Dlss;
            public string DlssAlso;
            public string FrameGeneration;
        }

        // The folder of an entry. A licence text for both goes with DLSS where
        // nvngx_dlss.dll is fetched too, with frame generation otherwise.
        public static string FolderOf(Entry entry, Folders folders, bool dlssFetched)
        {
            if (entry.Use == Use.Dlss) return folders.Dlss;
            if (entry.Use == Use.FrameGeneration) return folders.FrameGeneration;
            return dlssFetched ? folders.Dlss : folders.FrameGeneration;
        }

        // Of the entries, those not on disk as needed: absent, or for an exact
        // version not NVIDIA's release. A licence text counts only with a DLL of a
        // use it covers missing, so that DLLs in place are not offered a download
        // for a text alone.
        public static List<Entry> Missing(Folders folders, IList<Entry> entries)
        {
            List<Entry> missing = new List<Entry>();
            bool dlss = false;
            bool frameGeneration = false;
            foreach (Entry entry in entries)
            {
                if (entry.Notice || Present(FolderOf(entry, folders, false), entry)) continue;
                if (entry.Use == Use.Dlss && !string.IsNullOrEmpty(folders.DlssAlso) && Present(folders.DlssAlso, entry))
                    continue;
                missing.Add(entry);
                dlss |= entry.Use == Use.Dlss;
                frameGeneration |= entry.Use == Use.FrameGeneration;
            }
            foreach (Entry entry in entries)
            {
                if (!entry.Notice) continue;
                bool covered = entry.Use == Use.Dlss ? dlss : entry.Use == Use.FrameGeneration ? frameGeneration
                    : dlss || frameGeneration;
                if (covered && !Present(FolderOf(entry, folders, dlss), entry)) missing.Add(entry);
            }
            return missing;
        }

        // An exact version's checksum, known per file as it was when read: the
        // settings window asks each time it opens.
        private static readonly Dictionary<string, KeyValuePair<string, string>> checkedFiles =
            new Dictionary<string, KeyValuePair<string, string>>();

        private static bool Present(string folder, Entry entry)
        {
            FileInfo file = new FileInfo(Path.Combine(folder, entry.FileName));
            if (!file.Exists) return false;
            if (!entry.ExactVersion) return true;
            if (file.Length != entry.Size) return false;

            string stamp = file.Length + "@" + file.LastWriteTimeUtc.Ticks;
            KeyValuePair<string, string> known;
            lock (checkedFiles)
            {
                if (checkedFiles.TryGetValue(file.FullName, out known) && known.Key == stamp)
                    return known.Value == entry.Sha256;
            }
            string hash;
            using (FileStream stream = file.OpenRead()) hash = Sha256(stream);
            lock (checkedFiles) checkedFiles[file.FullName] = new KeyValuePair<string, string>(stamp, hash);
            return hash == entry.Sha256;
        }

        public static long DownloadSize(IEnumerable<Entry> entries)
        {
            long total = 0;
            foreach (Entry entry in entries) total += entry.RangeLength;
            return total;
        }

        // The file out of the archive's bytes RangeStart to RangeEnd, read from range
        // and written to output: the local header as the release has it, the data
        // inflated, its size and checksum as they have to be. Throws
        // InvalidDataException with the reason otherwise; output then holds
        // something that must not be used.
        public static void Extract(Entry entry, Stream range, Stream output)
        {
            // Complete first: inflating can come out whole from data that lacks its
            // last bits.
            if (range.CanSeek && range.Length - range.Position != entry.RangeLength)
                throw new InvalidDataException(entry.FileName + ": " + (range.Length - range.Position)
                                               + " bytes of the archive arrived, " + entry.RangeLength + " expected");

            byte[] name = Encoding.UTF8.GetBytes(entry.Name);
            byte[] header = new byte[30 + name.Length];
            if (ReadFully(range, header) != header.Length)
                throw new InvalidDataException(entry.FileName + ": the archive's part ended in its header");

            if (ReadUInt32(header, 0) != 0x04034B50u)
                throw new InvalidDataException(entry.FileName + ": no local file header where the release has one");
            int flags = ReadUInt16(header, 6);
            int method = ReadUInt16(header, 8);
            uint compressedSize = ReadUInt32(header, 18);
            uint size = ReadUInt32(header, 22);
            int nameLength = ReadUInt16(header, 26);
            int extraLength = ReadUInt16(header, 28);
            if (flags != 0 || method != 8 || compressedSize != entry.CompressedSize || size != entry.Size
                || nameLength != name.Length || extraLength != 0)
                throw new InvalidDataException(entry.FileName + ": the archive's header differs from the release's");
            for (int i = 0; i < name.Length; i++)
            {
                if (header[30 + i] != name[i])
                    throw new InvalidDataException(entry.FileName + ": another file stands where the release has it");
            }

            long written = 0;
            using (SHA256 sha = SHA256.Create())
            using (DeflateStream inflate = new DeflateStream(new LimitedStream(range, entry.CompressedSize),
                                                             CompressionMode.Decompress))
            {
                byte[] buffer = new byte[81920];
                int n;
                while ((n = inflate.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += n;
                    if (written > entry.Size)
                        throw new InvalidDataException(entry.FileName + ": more bytes inflated than the release has");
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    output.Write(buffer, 0, n);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                if (written != entry.Size)
                    throw new InvalidDataException(entry.FileName + ": " + written + " bytes inflated, " + entry.Size
                                                   + " expected");
                if (Hex(sha.Hash) != entry.Sha256)
                    throw new InvalidDataException(entry.FileName + ": its checksum is not that of NVIDIA's release");
            }
        }

        // A file checked by Extract into Partial, to go to Target.
        public sealed class Placement
        {
            public Entry Entry;
            public string Partial;
            public string Target;
            // Set while placing: where a different file already there was moved, and
            // whether this file went in.
            public string Backup;
            public bool Placed;
        }

        public static string PartialPath(string target)
        {
            return target + ".redefinition-download";
        }

        // Every file into place, or none. A different file already there is kept,
        // renamed to name.old (then .old2 and on); one that is the same stays as it
        // is. Should any step fail, every file placed so far is
        // taken out again, every renamed one goes back under its name, and the
        // exception says so. Returns the backups' names.
        public static List<string> PlaceAll(IList<Placement> placements)
        {
            List<string> backups = new List<string>();
            try
            {
                foreach (Placement placement in placements)
                {
                    if (File.Exists(placement.Target))
                    {
                        string existing;
                        using (FileStream stream = File.OpenRead(placement.Target)) existing = Sha256(stream);
                        if (existing == placement.Entry.Sha256)
                        {
                            File.Delete(placement.Partial);
                            continue;
                        }
                        string backup = placement.Target + ".old";
                        for (int n = 2; File.Exists(backup); n++) backup = placement.Target + ".old" + n;
                        File.Move(placement.Target, backup);
                        placement.Backup = backup;
                    }
                    File.Move(placement.Partial, placement.Target);
                    placement.Placed = true;
                    if (placement.Backup != null) backups.Add(Path.GetFileName(placement.Backup));
                }
                return backups;
            }
            catch (Exception e)
            {
                List<string> left = new List<string>();
                for (int i = placements.Count - 1; i >= 0; i--)
                {
                    Placement placement = placements[i];
                    try
                    {
                        // Placed by this call a moment ago: taken out again.
                        if (placement.Placed) File.Delete(placement.Target);
                        if (placement.Backup != null) File.Move(placement.Backup, placement.Target);
                    }
                    catch (Exception)
                    {
                        left.Add(Path.GetFileName(placement.Backup ?? placement.Target));
                    }
                    try
                    {
                        if (File.Exists(placement.Partial)) File.Delete(placement.Partial);
                    }
                    catch (Exception)
                    {
                        left.Add(Path.GetFileName(placement.Partial));
                    }
                }
                throw new IOException(e.Message + (left.Count > 0
                                          ? " -- could not be put back as they were: " + string.Join(", ", left.ToArray())
                                          : " -- every file is as it was"), e);
            }
        }

        public static string Sha256(Stream stream)
        {
            using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(stream));
        }

        public static string Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(data));
        }

        private static string Hex(byte[] hash)
        {
            StringBuilder text = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) text.Append(b.ToString("X2"));
            return text.ToString();
        }

        private static int ReadFully(Stream stream, byte[] buffer)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return read;
        }

        private static int ReadUInt16(byte[] bytes, int at)
        {
            return bytes[at] | bytes[at + 1] << 8;
        }

        private static uint ReadUInt32(byte[] bytes, int at)
        {
            return (uint)(bytes[at] | bytes[at + 1] << 8 | bytes[at + 2] << 16 | bytes[at + 3] << 24);
        }

        // At most length bytes of a stream, left open: the compressed data, so that
        // inflating cannot read past it.
        private sealed class LimitedStream : Stream
        {
            private readonly Stream inner;
            private long left;

            public LimitedStream(Stream inner, long length)
            {
                this.inner = inner;
                left = length;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (left <= 0) return 0;
                int n = inner.Read(buffer, offset, (int)Math.Min(count, left));
                left -= n;
                return n;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
