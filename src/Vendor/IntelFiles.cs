using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace ReDefinition.Vendor
{
    // Intel's DLLs XeSS needs, as they lie in Intel's own release of the XeSS SDK
    // 3.0.2 on GitHub: libxess.dll for XeSS super resolution, libxess_fg.dll and
    // libxell.dll for XeSS frame generation and XeLL. The player downloads them
    // from Intel's release after accepting Intel's licence (IntelDownloader); they
    // are not part of the package.
    //
    // Every figure below was read from that release's central directory and local
    // headers by byte range. The general purpose flags are 8 (a data descriptor follows the
    // data, so the local headers carry no sizes: NvidiaFiles.Extract takes the
    // sizes from here), and none of the entries has an extra field. The checksums are those of
    // the inflated files, which carry Intel's valid signatures; the versions are
    // libxess.dll 2.0.2.68, libxess_fg.dll 1.3.1.78 and libxell.dll 1.3.2.10. Only
    // the entries' bytes are fetched, not the 77 MB archive; a file that does not
    // come out as the checksum says is not placed.
    //
    // The checking, extracting and placing are NvidiaFiles' (Entry, Extract,
    // PlaceAll): the same zip layout and the same rules.
    //
    // Everything here is free of Unity, for the tests (IntelFilesTests).
    internal static class IntelFiles
    {
        // The XeSS SDK release the files below were read from.
        public const string SdkVersion = "3.0.2";

        // Intel's archive of the release, whose entries' bytes are fetched by range.
        public const string ArchiveUrl = "https://github.com/intel/xess/releases/download/v3.0.2/XeSS_SDK_3.0.2.zip";

        // The release's page and its licence, in the repository at the release's tag;
        // the same text as the notice the download puts next to the files.
        public const string ReleaseUrl = "https://github.com/intel/xess/releases/tag/v3.0.2";
        public const string LicenceUrl = "https://github.com/intel/xess/blob/v3.0.2/LICENSE.txt";
        public const string LicenceName = "Intel Simplified Software License (Version October 2022)";

        // libxess_fg.dll older than this is refused by the proxy (XessFg.cpp, kOldest*):
        // XeSS SDK 2.1.1 ships 1.2.2.
        public const string FrameGenerationFloor = "1.2.2";

        // What a file is fetched for.
        public enum Use
        {
            // libxess.dll: XeSS super resolution.
            SuperResolution,
            // libxess_fg.dll and libxell.dll: XeSS frame generation, which brings XeLL.
            FrameGeneration,
            // The licence text, fetched with a DLL.
            Notice,
        }

        // A file of the release: NvidiaFiles' entry for it, what it is for, and the oldest version that runs.
        public sealed class Item
        {
            // The archive's figures for the file, Use.Both to NvidiaFiles, and its use here.
            public NvidiaFiles.Entry Entry;
            public Use Use;
            // A file older than this does not run (a version, major.minor.build).
            public string MinimumVersion;
        }

        // An item with its entry: Intel's release, a notice for the licence text.
        private static Item Make(string name, string fileName, long offset, long compressedSize, long size, string sha256,
                                 Use use, string minimumVersion = null)
        {
            return new Item
            {
                Entry = new NvidiaFiles.Entry
                {
                    Name = name, FileName = fileName, Offset = offset, CompressedSize = compressedSize, Size = size,
                    Sha256 = sha256, Use = NvidiaFiles.Use.Both, Notice = use == Use.Notice,
                    Release = "Intel's release",
                },
                Use = use,
                MinimumVersion = minimumVersion,
            };
        }

        // Every file of Intel's release the mod uses, with the figures read from it.
        public static readonly Item[] All =
        {
            Make("bin/libxess.dll", "libxess.dll", 405372, 59737353, 77795704,
                 "251659DD84A3E84DE67C886A4186E01F3ECA49B00641906FE38BB6B807E5D5B7", Use.SuperResolution),
            Make("LICENSE.txt", "xess.license.txt", 76491459, 1970, 4194,
                 "784BE42C39A4A4D03CF85D88E15E59D75828FCA773CE45B8DBFFFBD7EAF208DF", Use.Notice),
            Make("bin/libxess_fg.dll", "libxess_fg.dll", 60290790, 13139477, 22957432,
                 "EC5E0C65E075570C6EDE72618BB666D0BE0C2E10B2EA9762C0FE8CB8E375AB27", Use.FrameGeneration,
                 FrameGenerationFloor),
            Make("bin/libxell.dll", "libxell.dll", 267603, 137708, 415368,
                 "D2030DCD694FDA8F2EC7E044B13E6DB8F0B56D4BA9113A5EFAD334E3F3DED8C7", Use.FrameGeneration),
        };

        // Where each use's files lie: where the proxy loads them from -- xessDirectory
        // and xessFgDirectory of its ini, else next to KSP_x64.exe -- and where the
        // download puts them.
        public struct Folders
        {
            // libxess.dll's folder, and that of libxess_fg.dll and libxell.dll.
            public string SuperResolution;
            public string FrameGeneration;
        }

        // The folders from KSP's folder and the ini's xessDirectory and xessFgDirectory, either relative to KSP's
        // folder or rooted.
        public static Folders FoldersOf(string game, string xessDirectory, string xessFgDirectory)
        {
            return new Folders
            {
                SuperResolution = string.IsNullOrEmpty(xessDirectory) ? game : Rooted(game, xessDirectory),
                FrameGeneration = string.IsNullOrEmpty(xessFgDirectory) ? game : Rooted(game, xessFgDirectory),
            };
        }

        // The folder as it stands where rooted, else under KSP's folder.
        private static string Rooted(string game, string folder)
        {
            return Path.IsPathRooted(folder) ? folder : Path.Combine(game, folder);
        }

        // The licence text goes with libxess.dll where that is fetched too, with frame
        // generation otherwise.
        public static string FolderOf(Item item, Folders folders, bool superResolutionFetched)
        {
            if (item.Use == Use.SuperResolution) return folders.SuperResolution;
            if (item.Use == Use.FrameGeneration) return folders.FrameGeneration;
            return superResolutionFetched ? folders.SuperResolution : folders.FrameGeneration;
        }

        // A key's value in the ini's [Proxy] section, as GetPrivateProfileString reads
        // it (the proxy's own reading): the first "key=value" of the section, the value
        // trimmed, lines starting with ; or # skipped. Empty where the key, the section
        // or the file is not there.
        public static string IniValue(string[] lines, string key)
        {
            bool inProxy = false;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[')
                {
                    inProxy = string.Equals(line, "[Proxy]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inProxy) continue;
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                if (!string.Equals(line.Substring(0, equals).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
                string value = line.Substring(equals + 1).Trim();
                if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                    value = value.Substring(1, value.Length - 2);
                return value;
            }
            return "";
        }

        // The key's value in the ini file's [Proxy] section; empty where the file cannot be read.
        public static string IniValue(string iniPath, string key)
        {
            try
            {
                return File.Exists(iniPath) ? IniValue(File.ReadAllLines(iniPath), key) : "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        // The file version of a DLL, such as 2.0.2.68; null where it has none or the
        // file cannot be read. Replaced in the tests, which have no DLLs.
        public static Func<string, Version> ReadVersion = path =>
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
            }
            catch (Exception)
            {
                return null;
            }
        };

        // Major.minor.build of a version, whose missing parts read as 0.
        public static Version Short(Version version)
        {
            return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        }

        // Whether the item's file is in the folder, and where it has a minimum version, of that version or later.
        private static bool Present(string folder, Item item)
        {
            string path = Path.Combine(folder, item.Entry.FileName);
            if (!File.Exists(path)) return false;
            if (item.MinimumVersion == null) return true;
            // A version that cannot be read is let through, as the proxy does.
            Version found = ReadVersion(path);
            return found == null || Short(found) >= new Version(item.MinimumVersion);
        }

        // Of the items, those not on disk as needed: absent, or older than they run.
        // A DLL of any other version stays. The licence text counts only with a DLL
        // missing, so that DLLs in place are not offered a download for a text alone.
        public static List<Item> Missing(Folders folders, IList<Item> items)
        {
            List<Item> missing = new List<Item>();
            bool superResolution = false;
            foreach (Item item in items)
            {
                if (item.Use == Use.Notice || Present(FolderOf(item, folders, false), item)) continue;
                missing.Add(item);
                superResolution |= item.Use == Use.SuperResolution;
            }
            if (missing.Count == 0) return missing;
            foreach (Item item in items)
            {
                if (item.Use == Use.Notice && !Present(FolderOf(item, folders, superResolution), item)) missing.Add(item);
            }
            return missing;
        }

        // What the items serve: the licence text goes with the DLLs, so it is never the
        // only one.
        public static void Uses(IEnumerable<Item> items, out bool superResolution, out bool frameGeneration)
        {
            superResolution = false;
            frameGeneration = false;
            foreach (Item item in items)
            {
                superResolution |= item.Use == Use.SuperResolution;
                frameGeneration |= item.Use == Use.FrameGeneration;
            }
        }

        // The items of one use with the licence text, as a download of that use alone.
        public static List<Item> Only(IEnumerable<Item> items, Use use)
        {
            List<Item> chosen = new List<Item>();
            foreach (Item item in items)
            {
                if (item.Use == use || item.Use == Use.Notice) chosen.Add(item);
            }
            return chosen;
        }

        // The items' archive entries, for NvidiaFiles.
        public static List<NvidiaFiles.Entry> Entries(IEnumerable<Item> items)
        {
            List<NvidiaFiles.Entry> entries = new List<NvidiaFiles.Entry>();
            foreach (Item item in items) entries.Add(item.Entry);
            return entries;
        }

        // The bytes the items' ranges fetch together, for the download's size.
        public static long DownloadSize(IEnumerable<Item> items)
        {
            return NvidiaFiles.DownloadSize(Entries(items));
        }

        // The DLLs that lie in the folders, with their versions, for the settings row:
        // "libxess.dll 2.0.2.68, libxess_fg.dll 1.3.1.78, libxell.dll 1.3.2.10"; null
        // where there is none.
        public static string InstalledText(Folders folders)
        {
            List<string> found = new List<string>();
            foreach (Item item in All)
            {
                if (item.Use == Use.Notice) continue;
                string path = Path.Combine(FolderOf(item, folders, false), item.Entry.FileName);
                if (!File.Exists(path)) continue;
                Version version = ReadVersion(path);
                found.Add(item.Entry.FileName + (version == null ? "" : " " + version));
            }
            return found.Count == 0 ? null : string.Join(", ", found.ToArray());
        }

        // A file with its download size, as the dialog lists it: "libxell.dll (0.1 MB)".
        public static string Describe(Item item)
        {
            return item.Entry.FileName + " ("
                   + (item.Entry.RangeLength / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB)";
        }
    }
}
