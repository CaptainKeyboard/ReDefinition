using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ReDefinitionExample
{
    // What this mod keeps in a file of its own, and how ReDefinition reaches it:
    // the registration names this type with `behaviour =` and gives its settings no
    // `member`, so ReDefinition asks the methods below for them by name
    // (docs/modders/registering-a-mod.md, "Behaviours"). Found by name, so this mod
    // still needs no reference to ReDefinition.
    //
    // A member path would do for a field; this shows the way for values a path
    // cannot say: they live in a config node, a write goes to the running pass as
    // well, and the file is written only when ReDefinition says to save.
    public static class SettingsBridge
    {
        private static readonly Dictionary<string, string> values = new Dictionary<string, string>
        {
            { "edgeDarkening", "0.35" },
            { "overlay", "True" },
        };

        private static bool loaded;

        private static string FilePath
        {
            get
            {
                return Path.Combine(KSPUtil.ApplicationRootPath,
                    Path.Combine("GameData", Path.Combine("ReDefinitionExample",
                        Path.Combine("PluginData", "settings.cfg"))));
            }
        }

        // Whether the values can be read and set now. This mod reads its file once,
        // so everything is there from the first call; a mod that builds its settings
        // in flight only would say so here, and ReDefinition would hold what the
        // player sets until then.
        public static bool Ready
        {
            get
            {
                Load();
                return true;
            }
        }

        public static string Version
        {
            get { return "1.0.0"; }
        }

        // The value as a config file writes it. null says the value cannot be read
        // now; ReDefinition leaves the row alone until it can.
        public static string Read(string name)
        {
            Load();
            string value;
            return values.TryGetValue(name, out value) ? value : null;
        }

        public static void Write(string name, string value)
        {
            Load();
            values[name] = value;
            // What the running mod has to be told at once, as its own window would.
            ExampleAddon.SettingsChanged();
        }

        // As this mod's own window would save: ReDefinition calls it once after a
        // batch of changes.
        public static void Save()
        {
            Load();
            ConfigNode node = new ConfigNode("REDEFINITIONEXAMPLE");
            foreach (KeyValuePair<string, string> pair in values) node.AddValue(pair.Key, pair.Value);
            ConfigNode file = new ConfigNode();
            file.AddNode(node);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            file.Save(FilePath);
        }

        // The value as a number, for the mod itself.
        internal static float Number(string name, float fallback)
        {
            float number;
            string text = Read(name);
            return text != null && float.TryParse(text, System.Globalization.NumberStyles.Float,
                                                  System.Globalization.CultureInfo.InvariantCulture, out number)
                ? number
                : fallback;
        }

        internal static bool Flag(string name, bool fallback)
        {
            bool flag;
            string text = Read(name);
            return text != null && bool.TryParse(text, out flag) ? flag : fallback;
        }

        private static void Load()
        {
            if (loaded) return;
            loaded = true;
            if (!File.Exists(FilePath)) return;
            ConfigNode file = ConfigNode.Load(FilePath);
            ConfigNode node = file != null ? file.GetNode("REDEFINITIONEXAMPLE") : null;
            if (node == null) return;
            foreach (ConfigNode.Value value in node.values) values[value.name] = value.value;
        }
    }
}
