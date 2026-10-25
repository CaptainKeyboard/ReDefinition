using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ReDefinition.Bridges
{
    // The choices of dlssg_for_sm86's dlssg_sm86.ini (AmperePackage) the settings window
    // offers, and the editing of that file in place: its comments, which are the
    // project's own instructions, stay as they are. The file is read by the package's
    // version.dll when KSP starts, so a change takes effect after a restart.
    //
    // Everything here is free of Unity, for the tests (AmpereOptionsTests).
    internal static class AmpereOptions
    {
        // One key of the ini the window offers as a row.
        public sealed class Option
        {
            // The [section] it stands in and its key, both matched without regard to case.
            public string Section;
            public string Key;
            // The values the file takes, in the order the window steps through them, with the
            // labels it shows for them.
            public string[] Values;
            public string[] Labels;
            // What the package does without the key (its factory ini).
            public string Default;
            public string Title;
            public string Tooltip;
        }

        // [DLSSG] Preset, [FrameGeneration] MaxGeneratedFrames, [SuperResolution] Mode and
        // [Logging] FrameTiming of dlssg_for_sm86 0.4.0 (its INSTALL.en.md and the comments
        // in its dlssg_sm86.ini).
        public static readonly Option Preset = new Option
        {
            Section = "DLSSG", Key = "Preset",
            Values = new[] { "Auto", "A", "B", "C" },
            Labels = new[] { "Auto", "A", "B", "C (Lite)" },
            Default = "Auto",
            Title = "DLSS-G preset",
            Tooltip = "How DLSS frame generation blends its frames on an RTX 30 or 20. C (Lite) costs about half "
                      + "the GPU time of Auto at the same look. Takes effect after a restart.",
        };

        // The most frames the package generates per rendered frame: 1 to 5, shown as 2X to 6X.
        public static readonly Option MaxGeneratedFrames = new Option
        {
            Section = "FrameGeneration", Key = "MaxGeneratedFrames",
            Values = new[] { "1", "2", "3", "4", "5" },
            Labels = new[] { "2X", "3X", "4X", "5X", "6X" },
            Default = "3",
            Title = "DLSS-G multiplier",
            Tooltip = "The most frames shown per rendered frame the package allows: 2X is one generated frame,\n"
                      + "6X is five. The package's factory value is 4X; installing it from the zip here sets 6X.\n"
                      + "The Frame generation row chooses how many are shown, up to this, at once; only a higher\n"
                      + "ceiling needs a restart of KSP. A generated frame costs GPU time only while it is shown.\n"
                      + "Fewer are shown under V-Sync where the refresh rate allows fewer.",
        };

        // What an install sets the ceiling to: the most the package's runtime supports (6X, five
        // generated frames), so that the row's choice has room; the cost is in the frames shown.
        public const string InstalledCeiling = "5";
        // The ceiling the package's own ini has.
        public const string FactoryCeiling = "3";
        // The preset an install sets where the package's ini has its own, Auto: C, which measured
        // +4 to 8 percent frame rate and less latency with the HUD and the scene unchanged
        // (docs/development/benchmarks.md, tools/hud_probe.py).
        public const string FactoryPreset = "Auto";
        public const string InstalledPreset = "C";

        // Whether the package's INT8 kernels run DLSS's presets L and M.
        public static readonly Option SuperResolution = new Option
        {
            Section = "SuperResolution", Key = "Mode",
            Values = new[] { "Off", "On" },
            Labels = new[] { "Off", "On" },
            Default = "Off",
            Title = "DLSS super resolution",
            Tooltip = "On: where DLSS runs its preset L or M (usually Performance and Ultra Performance),\n"
                      + "the package's INT8 kernels run it; no preset is changed. RTX 30 and RTX 20 only.\n"
                      + "Needs the package's sr folder (about 180 MB: nvngx_dlss.dll 310.7.129, which\n"
                      + "replaces the DLSS file in use, and sr_kernels.dll); the button next to it takes it\n"
                      + "out of the zip. Does nothing while Ray Reconstruction runs.\n"
                      + "Applies after a restart of KSP.",
        };

        // Whether the package logs the GPU time of each frame generation evaluate.
        public static readonly Option FrameTiming = new Option
        {
            Section = "Logging", Key = "FrameTiming",
            Values = new[] { "0", "1" },
            Labels = new[] { "Off", "On" },
            Default = "0",
            Title = "DLSS-G frame timing",
            Tooltip = "On: the package writes the GPU time of every frame generation evaluate, per rendered\n"
                      + "frame, to frames_<pid>.jsonl in dlssg_sm86\\logs. It changes no pixel and costs\n"
                      + "a few microseconds per evaluate.\n"
                      + "Applies after a restart of KSP.",
        };

        // Every option, in the order the window shows them and Snapshot takes them.
        public static readonly Option[] All = { Preset, MaxGeneratedFrames, SuperResolution, FrameTiming };

        // The file's value for the option, or the option's default where the file, the
        // section or the key is not there. Null for a file that cannot be read.
        public static string Read(string path, Option option)
        {
            string text = ReadText(path, out _);
            if (text == null) return null;
            return Find(text, option.Section, option.Key) ?? option.Default;
        }

        // The values the file at path sets explicitly for the options the window manages, taken
        // before an install replaces the file. Null where there is no file: a first install.
        public static Dictionary<Option, string> Snapshot(string path)
        {
            if (!File.Exists(path)) return null;
            Dictionary<Option, string> values = new Dictionary<Option, string>();
            string text = ReadText(path, out _);
            if (text == null) return values;
            foreach (Option option in All)
            {
                string value = Find(text, option.Section, option.Key);
                if (value != null) values[option] = value;
            }
            return values;
        }

        // The ini after an install placed the package's factory file. A first install (no
        // snapshot) raises the factory's ceiling and preset to the install defaults; a value the
        // player set stays. A reinstall puts the player's values from before back, so that
        // neither a chosen preset nor a lowered ceiling is lost to the factory file, nor
        // mistaken for it.
        public static void AfterInstall(string path, Dictionary<Option, string> previous)
        {
            if (previous == null)
            {
                if (Read(path, MaxGeneratedFrames) == FactoryCeiling) Write(path, MaxGeneratedFrames, InstalledCeiling);
                if (Read(path, Preset) == FactoryPreset) Write(path, Preset, InstalledPreset);
                return;
            }
            foreach (KeyValuePair<Option, string> pair in previous)
                Write(path, pair.Key, pair.Value);
        }

        // The index of the option's value in Values, or of its default where the file holds
        // none of them.
        public static int IndexOf(Option option, string value)
        {
            for (int i = 0; i < option.Values.Length; i++)
                if (string.Equals(option.Values[i], value, StringComparison.OrdinalIgnoreCase)) return i;
            return Array.IndexOf(option.Values, option.Default);
        }

        // The option set to the value in the file, the rest of the file as it was: the
        // key's line where there is one in its section, else a line at the section's end,
        // else the section at the end of the file. Returns false for a file that cannot be
        // read or written.
        public static bool Write(string path, Option option, string value)
        {
            try
            {
                bool bom;
                string text = ReadText(path, out bom);
                if (text == null) return false;
                string edited = Set(text, option.Section, option.Key, value);
                if (edited != text)
                    File.WriteAllText(path, edited, new UTF8Encoding(bom));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The file's text without its UTF-8 byte order mark, and whether it had one; null for a file that cannot
        // be read.
        private static string ReadText(string path, out bool bom)
        {
            bom = false;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                return new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            }
            catch (Exception)
            {
                return null;
            }
        }

        // A line of the text: where it starts and where the next one does.
        private struct Span
        {
            // The index of the line's first character, and of the first after its line end.
            public int Start;
            public int End;
        }

        // The lines of the text with their ends: each line's start and the start of the
        // next, so that a replaced line keeps its own ending.
        private static Span[] Lines(string text)
        {
            System.Collections.Generic.List<Span> lines = new System.Collections.Generic.List<Span>();
            int start = 0;
            while (start < text.Length)
            {
                int end = text.IndexOf('\n', start);
                end = end < 0 ? text.Length : end + 1;
                lines.Add(new Span { Start = start, End = end });
                start = end;
            }
            return lines.ToArray();
        }

        // The line's text without its ending.
        private static string Content(string text, Span line)
        {
            return text.Substring(line.Start, line.End - line.Start).TrimEnd('\r', '\n');
        }

        // Whether the line is a [section], and its name trimmed.
        private static bool IsSection(string content, out string name)
        {
            string trimmed = content.Trim();
            name = null;
            if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[trimmed.Length - 1] != ']') return false;
            name = trimmed.Substring(1, trimmed.Length - 2).Trim();
            return true;
        }

        // key=value of an uncommented line; the value trimmed.
        private static bool IsKey(string content, string key, out string value)
        {
            value = null;
            string trimmed = content.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == ';' || trimmed[0] == '#') return false;
            int equals = trimmed.IndexOf('=');
            if (equals <= 0) return false;
            if (!string.Equals(trimmed.Substring(0, equals).Trim(), key, StringComparison.OrdinalIgnoreCase)) return false;
            value = trimmed.Substring(equals + 1).Trim();
            return true;
        }

        // The key's value in the section, the last where the section has it twice; null where it has none.
        private static string Find(string text, string section, string key)
        {
            bool inside = false;
            string found = null;
            foreach (Span line in Lines(text))
            {
                string content = Content(text, line);
                string name;
                if (IsSection(content, out name))
                {
                    inside = string.Equals(name, section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                string value;
                // The last one wins, as GetPrivateProfileString reads a key written twice.
                if (inside && IsKey(content, key, out value)) found = value;
            }
            return found;
        }

        // The text with the key set to the value: its last line in the section where there is one, else a new
        // line after the section's last content, else the section at the end of the text. Line endings as the
        // text has them.
        internal static string Set(string text, string section, string key, string value)
        {
            string ending = text.Contains("\r\n") || text.Length == 0 ? "\r\n" : "\n";
            Span[] lines = Lines(text);
            int sectionLine = -1;
            int lastContent = -1;
            int keyLine = -1;
            bool inside = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string content = Content(text, lines[i]);
                string name;
                if (IsSection(content, out name))
                {
                    if (inside) break;
                    inside = string.Equals(name, section, StringComparison.OrdinalIgnoreCase);
                    if (inside)
                    {
                        sectionLine = i;
                        lastContent = i;
                    }
                    continue;
                }
                if (!inside) continue;
                string existing;
                if (IsKey(content, key, out existing)) keyLine = i;
                if (content.Trim().Length > 0) lastContent = i;
            }

            if (keyLine >= 0)
            {
                Span line = lines[keyLine];
                string content = Content(text, line);
                int equals = content.IndexOf('=');
                string tail = text.Substring(line.Start + content.Length, line.End - line.Start - content.Length);
                return text.Substring(0, line.Start) + content.Substring(0, equals + 1) + value + tail
                       + text.Substring(line.End);
            }
            string added = key + "=" + value;
            if (sectionLine >= 0)
            {
                // After the section's last line of content, with that line's ending (the last
                // line of a file may have none).
                Span at = lines[lastContent];
                string before = text.Substring(0, at.End);
                if (!before.EndsWith("\n", StringComparison.Ordinal)) before += ending;
                return before + added + ending + text.Substring(at.End);
            }
            string head = text;
            if (head.Length > 0 && !head.EndsWith("\n", StringComparison.Ordinal)) head += ending;
            if (head.Length > 0) head += ending;
            return head + "[" + section + "]" + ending + added + ending;
        }
    }
}
