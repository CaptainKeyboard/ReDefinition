using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ReDefinition.Bridges
{
    // dlssg_for_sm86, sdli1995's community project (MIT; NVIDIA's DLSS frame generation
    // runtime and kernels rebuilt from it inside), which runs DLSS frame generation on
    // GeForce RTX 30 and RTX 20: its release zip, which the player downloads from the
    // project's page and puts into KSP's folder, and what is taken out of it next to
    // KSP_x64.exe (AmpereFrameGeneration). Its version.dll is loaded there by KSP and by
    // ReDefinition's dxgi.dll alike; it answers Streamline with a newer GPU and has
    // NVIDIA's runtime run on its own kernels.
    //
    // Taken: version.dll, dlssg_sm86.ini and the dlssg_sm86 folder without its
    // super-resolution files (sr, for DLSS Super Resolution, off in its ini), the licence
    // and notices into that folder. Its layout changes between releases, so no file is
    // pinned: what has to be there is, and every file comes out with the size and CRC-32
    // the zip gives it.
    //
    // Everything here is free of Unity, for the tests (AmperePackageTests).
    internal static class AmperePackage
    {
        public const string ProjectUrl = "https://github.com/sdli1995/dlssg_for_sm86";
        public const string ReleasesUrl = "https://github.com/sdli1995/dlssg_for_sm86/releases/latest";
        public const string ZipPattern = "dlssg-release-*-x64.zip";
        public const string FolderName = "dlssg_sm86";
        public const string Proxy = "version.dll";
        public const string Ini = "dlssg_sm86.ini";

        private const string Runtime = FolderName + "/runtime/nvngx_dlssg.dll";
        private const string Backend = FolderName + "/backend/";
        private const string Kernels = FolderName + "/kernels/";
        private const string SuperResolution = FolderName + "/sr/";
        private const string Logs = FolderName + "/logs/";
        // The licence and notices at the zip's top, into the folder.
        private static readonly string[] Notices = { "LICENSE.md", "THIRD_PARTY_NOTICES.txt" };

        // One file of the zip, as its central directory gives it, and where it goes,
        // relative to KSP's folder with '/' between folders.
        public sealed class Item
        {
            public string Name;
            public string Target;
            public int Flags;
            public int Method;
            public uint Crc;
            public long CompressedSize;
            public long Size;
            public long LocalOffset;
        }

        // Whether the package lies next to KSP_x64.exe.
        public static bool Installed(string game)
        {
            return File.Exists(Path.Combine(game, Proxy)) && File.Exists(Path.Combine(game, Ini))
                   && File.Exists(Path.Combine(game, Runtime.Replace('/', Path.DirectorySeparatorChar)));
        }

        // The newest release zip in KSP's folder, or null.
        public static string FindZip(string game)
        {
            string newest = null;
            DateTime newestTime = DateTime.MinValue;
            foreach (string file in Directory.GetFiles(game, ZipPattern))
            {
                DateTime time = File.GetLastWriteTimeUtc(file);
                if (newest != null && time <= newestTime) continue;
                newest = file;
                newestTime = time;
            }
            return newest;
        }

        // Where a file of the zip goes, or null for one not taken.
        public static string TargetOf(string name)
        {
            if (name == Proxy || name == Ini) return name;
            if (Array.IndexOf(Notices, name) >= 0) return FolderName + "/" + name;
            if (!name.StartsWith(FolderName + "/", StringComparison.Ordinal) || name.EndsWith("/", StringComparison.Ordinal))
                return null;
            if (name.StartsWith(SuperResolution, StringComparison.Ordinal) || name.StartsWith(Logs, StringComparison.Ordinal))
                return null;
            return name;
        }

        // The zip's files that are taken. Throws InvalidDataException with the reason for
        // a zip that is none, or lacks what has to be there.
        public static List<Item> Read(Stream zip)
        {
            // The end of the central directory: at the end, before a comment of at most
            // 64 KB.
            long tail = Math.Min(zip.Length, 22 + 65535);
            byte[] end = new byte[tail];
            zip.Seek(zip.Length - tail, SeekOrigin.Begin);
            if (NvidiaFiles.ReadFully(zip, end) != end.Length) throw new InvalidDataException("the zip could not be read");
            int at = -1;
            for (int i = end.Length - 22; i >= 0; i--)
            {
                if (NvidiaFiles.ReadUInt32(end, i) == 0x06054B50u)
                {
                    at = i;
                    break;
                }
            }
            if (at < 0) throw new InvalidDataException("it is no zip");
            int count = NvidiaFiles.ReadUInt16(end, at + 10);
            uint directorySize = NvidiaFiles.ReadUInt32(end, at + 12);
            uint directoryOffset = NvidiaFiles.ReadUInt32(end, at + 16);
            if (count == 0xFFFF || directoryOffset == 0xFFFFFFFFu || directoryOffset + (long)directorySize > zip.Length)
                throw new InvalidDataException("the zip's directory is not one ReDefinition reads");

            byte[] directory = new byte[directorySize];
            zip.Seek(directoryOffset, SeekOrigin.Begin);
            if (NvidiaFiles.ReadFully(zip, directory) != directory.Length)
                throw new InvalidDataException("the zip's directory could not be read");

            List<Item> items = new List<Item>();
            int p = 0;
            for (int n = 0; n < count; n++)
            {
                if (p + 46 > directory.Length || NvidiaFiles.ReadUInt32(directory, p) != 0x02014B50u)
                    throw new InvalidDataException("the zip's directory is broken");
                int nameLength = NvidiaFiles.ReadUInt16(directory, p + 28);
                int extraLength = NvidiaFiles.ReadUInt16(directory, p + 30);
                int commentLength = NvidiaFiles.ReadUInt16(directory, p + 32);
                if (p + 46 + nameLength > directory.Length) throw new InvalidDataException("the zip's directory is broken");
                string name = Encoding.UTF8.GetString(directory, p + 46, nameLength).Replace('\\', '/');
                Item item = new Item
                {
                    Name = name,
                    Flags = NvidiaFiles.ReadUInt16(directory, p + 8),
                    Method = NvidiaFiles.ReadUInt16(directory, p + 10),
                    Crc = NvidiaFiles.ReadUInt32(directory, p + 16),
                    CompressedSize = NvidiaFiles.ReadUInt32(directory, p + 20),
                    Size = NvidiaFiles.ReadUInt32(directory, p + 24),
                    LocalOffset = NvidiaFiles.ReadUInt32(directory, p + 42),
                };
                p += 46 + nameLength + extraLength + commentLength;

                item.Target = TargetOf(name);
                if (item.Target == null) continue;
                if (name.StartsWith("/", StringComparison.Ordinal) || name.Contains("..") || name.Contains(":"))
                    throw new InvalidDataException(name + ": a path outside KSP's folder");
                if (item.Size == 0xFFFFFFFFu || item.CompressedSize == 0xFFFFFFFFu || item.LocalOffset == 0xFFFFFFFFu)
                    throw new InvalidDataException(name + ": too large for ReDefinition to read");
                if ((item.Flags & 1) != 0) throw new InvalidDataException(name + ": encrypted");
                if (item.Method != 0 && item.Method != 8)
                    throw new InvalidDataException(name + ": packed in a way ReDefinition does not read");
                items.Add(item);
            }

            List<string> missing = new List<string>();
            foreach (string required in new[] { Proxy, Ini, Runtime })
            {
                if (!items.Exists(item => item.Target == required)) missing.Add(required);
            }
            if (!items.Exists(item => item.Target.StartsWith(Backend, StringComparison.Ordinal))) missing.Add(Backend);
            if (!items.Exists(item => item.Target.StartsWith(Kernels, StringComparison.Ordinal))) missing.Add(Kernels);
            if (missing.Count > 0)
                throw new InvalidDataException("it lacks " + string.Join(", ", missing.ToArray())
                                               + " -- another layout than ReDefinition knows");
            return items;
        }

        // One file out of the zip into output, with the size and CRC-32 the zip's
        // directory gives it. Throws InvalidDataException with the reason otherwise;
        // output then holds something that must not be used.
        public static void Extract(Stream zip, Item item, Stream output)
        {
            byte[] header = new byte[30];
            zip.Seek(item.LocalOffset, SeekOrigin.Begin);
            if (NvidiaFiles.ReadFully(zip, header) != header.Length || NvidiaFiles.ReadUInt32(header, 0) != 0x04034B50u)
                throw new InvalidDataException(item.Name + ": no file header where the zip's directory has one");
            zip.Seek(NvidiaFiles.ReadUInt16(header, 26) + NvidiaFiles.ReadUInt16(header, 28), SeekOrigin.Current);

            Stream data = new NvidiaFiles.LimitedStream(zip, item.CompressedSize);
            Stream source = item.Method == 8 ? new DeflateStream(data, CompressionMode.Decompress) : data;
            uint crc = 0xFFFFFFFFu;
            long written = 0;
            try
            {
                byte[] buffer = new byte[81920];
                int n;
                while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += n;
                    if (written > item.Size) throw new InvalidDataException(item.Name + ": more bytes than the zip gives it");
                    crc = Crc32(crc, buffer, n);
                    output.Write(buffer, 0, n);
                }
            }
            finally
            {
                if (source != data) source.Dispose();
            }
            if (written != item.Size)
                throw new InvalidDataException(item.Name + ": " + written + " bytes, " + item.Size + " expected");
            if ((crc ^ 0xFFFFFFFFu) != item.Crc) throw new InvalidDataException(item.Name + ": its checksum does not match");
        }

        // The taken files out of the zip next to KSP_x64.exe, all or none: each first into
        // a partial file beside its target, then placed. A file already there is kept,
        // renamed to name.old (then .old2 and on) -- which Windows allows for a DLL the
        // running game has loaded. Should any step fail, every file placed is taken out
        // again, every renamed one goes back, and the exception says so. Returns how many
        // files were placed.
        public static int Install(string zipPath, string game)
        {
            List<Item> items;
            List<string> partials = new List<string>();
            List<string> targets = new List<string>();
            using (FileStream zip = File.OpenRead(zipPath))
            {
                items = Read(zip);
                try
                {
                    foreach (Item item in items)
                    {
                        string target = Path.Combine(game, item.Target.Replace('/', Path.DirectorySeparatorChar));
                        string partial = target + ".redefinition-install";
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        partials.Add(partial);
                        targets.Add(target);
                        using (FileStream output = File.Create(partial)) Extract(zip, item, output);
                    }
                }
                catch (Exception)
                {
                    foreach (string partial in partials)
                        Delete(partial);
                    throw;
                }
            }

            List<string> placed = new List<string>();
            List<KeyValuePair<string, string>> renamed = new List<KeyValuePair<string, string>>();
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    if (File.Exists(targets[i]))
                    {
                        string backup = targets[i] + ".old";
                        for (int n = 2; File.Exists(backup); n++) backup = targets[i] + ".old" + n;
                        File.Move(targets[i], backup);
                        renamed.Add(new KeyValuePair<string, string>(targets[i], backup));
                    }
                    File.Move(partials[i], targets[i]);
                    placed.Add(targets[i]);
                }
                return placed.Count;
            }
            catch (Exception e)
            {
                List<string> left = new List<string>();
                foreach (string target in placed)
                {
                    if (!Delete(target)) left.Add(Path.GetFileName(target));
                }
                for (int i = renamed.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        File.Move(renamed[i].Value, renamed[i].Key);
                    }
                    catch (Exception)
                    {
                        left.Add(Path.GetFileName(renamed[i].Value));
                    }
                }
                foreach (string partial in partials)
                    Delete(partial);
                throw new IOException(e.Message + (left.Count > 0
                                          ? " -- could not be put back as they were: " + string.Join(", ", left.ToArray())
                                          : " -- every file is as it was"), e);
            }
        }

        private static bool Delete(string file)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static readonly uint[] CrcTable = MakeCrcTable();

        private static uint[] MakeCrcTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        private static uint Crc32(uint crc, byte[] data, int length)
        {
            for (int i = 0; i < length; i++) crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
