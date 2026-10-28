using System.Collections.Generic;

namespace ReDefinition.Migration
{
    // What a step did to one setting of a player's file: the key, the value before (null where there was none) and after (null
    // where it was removed), and the words the player is told.
    internal sealed class SettingsChange
    {
        public readonly string Key, Before, After, Text;

        public SettingsChange(string key, string before, string after, string text)
        {
            Key = key;
            Before = before;
            After = after;
            Text = text;
        }
    }

    // One change to ReDefinition's settings between two releases, and what becomes of a player's file because of it. A step runs
    // once on a file from a release before it (SettingsMigration), on the settings as text, and says what it did.
    internal abstract class SettingsStep
    {
        // A name that never changes, for the list of steps a file has been through; and the words for the player.
        public readonly string Id, Text;

        protected SettingsStep(string id, string text)
        {
            Id = id;
            Text = text;
        }

        // Carries the step out on the values of a file, key to text, and notes each change.
        public abstract void Apply(IDictionary<string, string> values, List<SettingsChange> changes);
    }

    // A setting that is gone: its key leaves the file.
    internal sealed class RemovedSetting : SettingsStep
    {
        private readonly string key;

        // The key that leaves the file.
        public string Key
        {
            get { return key; }
        }

        public RemovedSetting(string id, string key, string text) : base(id, text)
        {
            this.key = key;
        }

        public override void Apply(IDictionary<string, string> values, List<SettingsChange> changes)
        {
            string before;
            if (!values.TryGetValue(key, out before)) return;
            values.Remove(key);
            changes.Add(new SettingsChange(key, before, null, Text));
        }
    }

    // A setting under a new name: its value moves to the new key, unless the file has the new key already.
    internal sealed class RenamedSetting : SettingsStep
    {
        private readonly string from, to;

        public RenamedSetting(string id, string from, string to, string text) : base(id, text)
        {
            this.from = from;
            this.to = to;
        }

        public override void Apply(IDictionary<string, string> values, List<SettingsChange> changes)
        {
            string before;
            if (!values.TryGetValue(from, out before)) return;
            values.Remove(from);
            if (!values.ContainsKey(to)) values[to] = before;
            changes.Add(new SettingsChange(to, before, values[to], Text));
        }
    }

    // A new standard value: where the file still holds the old standard, it takes the new one; a value the player chose stays.
    internal sealed class ChangedDefault : SettingsStep
    {
        private readonly string key, oldDefault, newDefault;

        public ChangedDefault(string id, string key, string oldDefault, string newDefault, string text) : base(id, text)
        {
            this.key = key;
            this.oldDefault = oldDefault;
            this.newDefault = newDefault;
        }

        public override void Apply(IDictionary<string, string> values, List<SettingsChange> changes)
        {
            string before;
            if (!values.TryGetValue(key, out before) || before != oldDefault) return;
            values[key] = newDefault;
            changes.Add(new SettingsChange(key, before, newDefault, Text));
        }
    }

    // A setting whose values have other names or another scale: each value in the map becomes the one it maps to; a value not in the
    // map stays. An old standard in the map moves to the new standard as well, which is how a changed standard goes with it.
    internal sealed class ConvertedSetting : SettingsStep
    {
        private readonly string key;
        private readonly Dictionary<string, string> map;

        public ConvertedSetting(string id, string key, Dictionary<string, string> map, string text) : base(id, text)
        {
            this.key = key;
            this.map = map;
        }

        public override void Apply(IDictionary<string, string> values, List<SettingsChange> changes)
        {
            string before, after;
            if (!values.TryGetValue(key, out before) || !map.TryGetValue(before, out after) || after == before) return;
            values[key] = after;
            changes.Add(new SettingsChange(key, before, after, Text));
        }
    }

    // A change that keeps every player's value on purpose, said so: a new standard that only a fresh file takes, or a setting
    // whose meaning changed in a way a value need not follow.
    internal sealed class KeptSetting : SettingsStep
    {
        public KeptSetting(string id, string text) : base(id, text)
        {
        }

        public override void Apply(IDictionary<string, string> values, List<SettingsChange> changes)
        {
        }
    }
}
