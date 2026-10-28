using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReDefinition.Migration
{
    // The migration of a player's settings file to this release. The steps of Migrations that the file has not been through run
    // once, in order, on its values. The file is kept as it was, under the release it came from. What the steps changed is written
    // down for the notice the player is shown (MigrationNotice). A file from a newer release than this one is put back as it was
    // before that release changed it. Free of Unity and KSP: it works on the values as text and on files.
    internal static class SettingsMigration
    {
        // The keys the file keeps beside the settings: the release that last wrote it, and the ids of the steps it has been through.
        internal const string VersionKey = "version", AppliedKey = "migrations";

        // The release a file without a version comes from: the last one before the migration existed.
        internal const string FirstRelease = "0.1.3";

        // The release this code is: three numbers of the assembly's version.
        internal static string Current
        {
            get { return typeof(SettingsMigration).Assembly.GetName().Version.ToString(3); }
        }

        // The ids a file has been through, as the file lists them.
        internal static HashSet<string> Applied(IDictionary<string, string> values)
        {
            string text;
            return values.TryGetValue(AppliedKey, out text) && text.Length > 0
                ? new HashSet<string>(text.Split(','), StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        }

        // The release a file was written by.
        internal static string From(IDictionary<string, string> values)
        {
            string text;
            return values.TryGetValue(VersionKey, out text) && text.Length > 0 ? text : FirstRelease;
        }

        // Which of two releases is newer: negative, zero or positive as `a` is older than, the same as or newer than `b`; an
        // unreadable number counts as 0.
        internal static int Compare(string a, string b)
        {
            Version x, y;
            if (!Version.TryParse(a, out x)) x = new Version(0, 0);
            if (!Version.TryParse(b, out y)) y = new Version(0, 0);
            return x.CompareTo(y);
        }

        // Runs the steps the values have not been through, in order, and notes them as done; the changes they made, in order. The
        // values then carry this release and the list of steps done.
        internal static List<SettingsChange> Run(IDictionary<string, string> values, IEnumerable<SettingsStep> steps, string version)
        {
            HashSet<string> applied = Applied(values);
            var changes = new List<SettingsChange>();
            foreach (SettingsStep step in steps)
            {
                if (applied.Contains(step.Id)) continue;
                step.Apply(values, changes);
                applied.Add(step.Id);
            }
            values[AppliedKey] = string.Join(",", applied.OrderBy(id => id, StringComparer.Ordinal));
            values[VersionKey] = version;
            return changes;
        }

        // The values of a fresh file, which no step is for: it holds this release and all the steps as done.
        internal static void Fresh(IDictionary<string, string> values, IEnumerable<SettingsStep> steps, string version)
        {
            values[AppliedKey] = string.Join(",", steps.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal));
            values[VersionKey] = version;
        }

        // The file as it was before the migration, kept next to it under the release it came from.
        internal static string BackupPath(string path, string release)
        {
            return path + ".bak-" + release;
        }

        // Copies the file to its backup; where that cannot be done, the reason, otherwise null.
        internal static string Backup(string path, string release)
        {
            try
            {
                File.Copy(path, BackupPath(path, release), true);
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        // For a file from a newer release than this one: the file as it was before that release, put back, and the newer one kept
        // as `.bak-newer`. False where there is no such backup, and the newer file stays.
        internal static bool RestoreOlder(string path, string release)
        {
            string backup = BackupPath(path, release);
            if (!File.Exists(backup)) return false;
            try
            {
                File.Copy(path, path + ".bak-newer", true);
                File.Copy(backup, path, true);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
