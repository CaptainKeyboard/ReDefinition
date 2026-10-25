using System.Collections.Generic;
using System;
using ReDefinition.Settings;

namespace ReDefinition.Tests
{
    // A mod as the store sees one: its settings held in a dictionary, read and
    // written as the mod's own fields would be, and a save routine that counts.
    internal sealed class FakeMod : IBundledMod
    {
        // The settings' values and write counts by name, the settings made, and the members dropped.
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        public readonly Dictionary<string, int> Writes = new Dictionary<string, int>();
        private readonly List<BundledSetting> settings = new List<BundledSetting>();
        private readonly List<string> dropped = new List<string>();

        // How often the mod's save routine ran, and whether it fails the next time.
        public int Saves;
        public bool SaveFails;
        // Thrown by the next write, once.
        public Exception WriteFails;

        // A fake mod of that id whose values last as `saving` says.
        public FakeMod(string id, SettingsSaving saving)
        {
            Id = id;
            Saving = saving;
        }

        // The id the test gave, the prefix of its settings' keys.
        public string Id { get; private set; }
        // The mod's name is its id.
        public string ModName { get { return Id; } }
        // Always installed, in the shape the store expects.
        public bool IsInstalled { get { return true; } }
        // The members dropped: none, unless a test adds some.
        public IList<string> DroppedMembers { get { return dropped; } }
        // No toolbar button to take over.
        public string ButtonAssembly { get { return null; } }
        // No button made through ToolbarControl.
        public string ToolbarControlNamespace { get { return null; } }
        // No own settings window to open.
        public string OwnWindow { get { return null; } }
        // No own settings window, so no type for it.
        public Type OwnWindowType { get { return null; } }
        // The settings Add made, in their order.
        public IList<BundledSetting> Settings { get { return settings; } }
        // The build whose defaults apply, as the test sets it.
        public string Build { get; set; }
        // No version to check the defaults against.
        public string Version { get { return null; } }
        // How a value set here lasts, as the test gave it.
        public SettingsSaving Saving { get; private set; }

        // Counts a save, or fails like a full disk where SaveFails says so.
        public void Save()
        {
            if (SaveFails) throw new InvalidOperationException("the disk is full");
            Saves++;
        }

        // Installs no hooks: there is no mod's window to watch.
        public void InstallHooks()
        {
        }

        // How often the setting of that name was written.
        public int WritesOf(string name)
        {
            int count;
            return Writes.TryGetValue(name, out count) ? count : 0;
        }

        // Adds a toggle setting of that name holding the value, read and written through Values, its writes
        // counted, the next write throwing WriteFails where that is set.
        public BundledSetting Add(string name, string value)
        {
            Values[name] = value;
            BundledSetting setting = new BundledSetting
            {
                Key = Id + "." + name,
                Title = name,
                Control = SettingControl.Toggle,
                Window = ApplyWindow.Live,
                Owner = this,
                Read = () => Values[name],
                Write = text =>
                {
                    if (WriteFails != null)
                    {
                        Exception failure = WriteFails;
                        WriteFails = null;
                        throw failure;
                    }
                    Values[name] = text;
                    Writes[name] = WritesOf(name) + 1;
                },
            };
            settings.Add(setting);
            return setting;
        }
    }

    // The game as the store needs it: the mods given, a save by name, what the
    // requirements answer, and a log to look into. bundled.cfg is not written.
    internal sealed class FakeHost : IStoreHost
    {
        // The mods given, the save loaded, what the requirements answer, the keys told on screen, the log's
        // lines and warnings, and how often bundled.cfg was written or whether that fails.
        public readonly List<FakeMod> Mods = new List<FakeMod>();
        public string Save = "";
        public Func<BundledSetting, string, string> AdjustWith = (setting, value) => value;
        public Func<BundledSetting, string, bool> AllowsWith = (setting, value) => true;
        public readonly List<string> Told = new List<string>();
        public readonly List<string> Lines = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public int FileWrites;
        public bool FileWriteFails;

        // Reads nothing: the store starts from an empty bundled.cfg.
        public void Read(BundledLedger ledger, BundledState state)
        {
        }

        // Counts the write instead of writing bundled.cfg, or fails where FileWriteFails says so.
        public bool Write(BundledLedger ledger, BundledState state)
        {
            if (FileWriteFails) return false;
            FileWrites++;
            return true;
        }

        // The fake mods' setting of that key, or null.
        public BundledSetting Find(string key)
        {
            foreach (FakeMod mod in Mods)
            {
                foreach (BundledSetting setting in mod.Settings)
                    if (setting.Key == key) return setting;
            }
            return null;
        }

        // The fake mods given, as the store's installed mods.
        public IList<IBundledMod> Installed()
        {
            List<IBundledMod> list = new List<IBundledMod>();
            foreach (FakeMod mod in Mods) list.Add(mod);
            return list;
        }

        // The save the test set in Save.
        public string LoadedSave()
        {
            return Save;
        }

        // Answers as AdjustWith does.
        public string Adjust(BundledSetting setting, string value)
        {
            return AdjustWith(setting, value);
        }

        // Answers as AllowsWith does.
        public bool Allows(BundledSetting setting, string value)
        {
            return AllowsWith(setting, value);
        }

        // Notes the key of a setting whose reasons would be told on screen.
        public void Tell(BundledSetting setting, string value)
        {
            Told.Add(setting.Key);
        }

        // Collects the log line for the test to look into.
        public void Info(string message)
        {
            Lines.Add(message);
        }

        // Collects the warning for the test to look into.
        public void Warning(string message)
        {
            Warnings.Add(message);
        }

        // A warning of a kind goes into the same list as any other.
        public void Warn(string kind, string message)
        {
            Warnings.Add(message);
        }
    }
}
