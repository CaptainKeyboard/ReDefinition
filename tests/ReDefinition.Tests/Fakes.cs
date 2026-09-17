using System;
using System.Collections.Generic;
using ReDefinition.Framework;

namespace ReDefinition.Tests
{
    // A mod as the store sees one: its settings held in a dictionary, read and
    // written as the mod's own fields would be, and a save routine that counts.
    internal sealed class FakeMod : IBundledMod
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        public readonly Dictionary<string, int> Writes = new Dictionary<string, int>();
        private readonly List<BundledSetting> settings = new List<BundledSetting>();
        private readonly List<string> dropped = new List<string>();

        public int Saves;
        public bool SaveFails;
        // Thrown by the next write, once.
        public Exception WriteFails;

        public FakeMod(string id, SettingsSaving saving)
        {
            Id = id;
            Saving = saving;
        }

        public string Id { get; private set; }
        public string ModName { get { return Id; } }
        public bool IsInstalled { get { return true; } }
        public IList<string> DroppedMembers { get { return dropped; } }
        public string ButtonAssembly { get { return null; } }
        public string ToolbarControlNamespace { get { return null; } }
        public string OwnWindow { get { return null; } }
        public Type OwnWindowType { get { return null; } }
        public IList<BundledSetting> Settings { get { return settings; } }
        public string Build { get { return null; } }
        public string Version { get { return null; } }
        public SettingsSaving Saving { get; private set; }

        public void Save()
        {
            if (SaveFails) throw new InvalidOperationException("the disk is full");
            Saves++;
        }

        public void InstallHooks()
        {
        }

        public int WritesOf(string name)
        {
            int count;
            return Writes.TryGetValue(name, out count) ? count : 0;
        }

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
        public readonly List<FakeMod> Mods = new List<FakeMod>();
        public string Save = "";
        public Func<BundledSetting, string, string> AdjustWith = (setting, value) => value;
        public Func<BundledSetting, string, bool> AllowsWith = (setting, value) => true;
        public readonly List<string> Told = new List<string>();
        public readonly List<string> Lines = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public int FileWrites;
        public bool FileWriteFails;

        public void Read(BundledLedger ledger, BundledState state)
        {
        }

        public bool Write(BundledLedger ledger, BundledState state)
        {
            if (FileWriteFails) return false;
            FileWrites++;
            return true;
        }

        public BundledSetting Find(string key)
        {
            foreach (FakeMod mod in Mods)
            {
                foreach (BundledSetting setting in mod.Settings)
                    if (setting.Key == key) return setting;
            }
            return null;
        }

        public IList<IBundledMod> Installed()
        {
            List<IBundledMod> list = new List<IBundledMod>();
            foreach (FakeMod mod in Mods) list.Add(mod);
            return list;
        }

        public string LoadedSave()
        {
            return Save;
        }

        public string Adjust(BundledSetting setting, string value)
        {
            return AdjustWith(setting, value);
        }

        public bool Allows(BundledSetting setting, string value)
        {
            return AllowsWith(setting, value);
        }

        public void Tell(BundledSetting setting, string value)
        {
            Told.Add(setting.Key);
        }

        public void Info(string message)
        {
            Lines.Add(message);
        }

        public void Warning(string message)
        {
            Warnings.Add(message);
        }

        public void Warn(string kind, string message)
        {
            Warnings.Add(message);
        }
    }
}
