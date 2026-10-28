using System;
using System.Collections.Generic;
using System.IO;

namespace ReDefinition.Migration
{
    // What a migration changed in a player's file, kept in a file of its own until the player has seen it. The notice in the
    // main menu shows all changes at once. It holds the release the file came from and each change with its words. The player
    // keeps the new values, which is how the file stands, or takes the old ones back: each change's value from before.
    internal static class MigrationNotice
    {
        // Free of Unity: one line per change, its parts apart by tabs, a missing value as "-" and a value as "=" before it.
        private const char Tab = '\t';

        // The changes the player has not yet seen, with the release the file came from.
        internal sealed class Pending
        {
            public string From;
            public readonly List<SettingsChange> Changes = new List<SettingsChange>();
        }

        // Writes the changes down, replacing what was there: an earlier notice the player never answered is older than these.
        internal static void Write(string path, string from, IEnumerable<SettingsChange> changes)
        {
            var lines = new List<string> { "from" + Tab + from };
            foreach (SettingsChange change in changes)
                lines.Add(Join(change.Key, Value(change.Before), Value(change.After), change.Text.Replace('\t', ' ').Replace('\n', ' ')));
            File.WriteAllLines(path, lines.ToArray());
        }

        // The changes waiting, or null where there are none.
        internal static Pending Read(string path)
        {
            if (!File.Exists(path)) return null;
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 2) return null;
            var pending = new Pending { From = lines[0].Split(Tab).Length > 1 ? lines[0].Split(Tab)[1] : SettingsMigration.FirstRelease };
            for (int i = 1; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split(Tab);
                if (parts.Length < 4) continue;
                pending.Changes.Add(new SettingsChange(parts[0], Parse(parts[1]), Parse(parts[2]), parts[3]));
            }
            return pending.Changes.Count > 0 ? pending : null;
        }

        // The notice answered or dropped: its file goes.
        internal static void Clear(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // A file that stays is shown again, which does no harm.
            }
        }

        // Puts the values from before back into the values of the file, for "keep my old values": each change's old value under its
        // key, and a key the change removed back. A key the change created under a new name goes.
        internal static void Restore(Pending pending, IDictionary<string, string> values)
        {
            foreach (SettingsChange change in pending.Changes)
            {
                if (change.Before == null) values.Remove(change.Key);
                else values[change.Key] = change.Before;
            }
        }

        private static string Join(string a, string b, string c, string d)
        {
            return a + Tab + b + Tab + c + Tab + d;
        }

        private static string Value(string value)
        {
            return value == null ? "-" : "=" + value;
        }

        private static string Parse(string text)
        {
            return text.StartsWith("=", StringComparison.Ordinal) ? text.Substring(1) : null;
        }
    }
}
