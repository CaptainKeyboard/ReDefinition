using System.Collections.Generic;
using System.IO;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Settings
{
    // The bindings ReDefinition keeps for a mod that has no member of its own: a
    // KEY without `member` in a registration. The mod asks ReDefinition.Api whether
    // its binding is pressed, and never holds a key itself.
    //
    // ReDefinition's own, not the mod's: never bundled, set straight from the
    // settings window with the bundling on or off, and kept in bindings.cfg in
    // ReDefinition's PluginData, written at every change.
    internal static class KeptBindings
    {
        private const string FileName = "bindings.cfg";
        private const string NodeName = "KEPT_BINDINGS";

        private static readonly Dictionary<string, string> bindings = new Dictionary<string, string>();
        private static bool loaded;

        internal static string Get(string key, string fallback)
        {
            Load();
            string text;
            return bindings.TryGetValue(key, out text) ? text : fallback;
        }

        internal static void Set(string key, string text)
        {
            Load();
            bindings[key] = text ?? KeyCombination.NoneText;
            Save();
        }

        private static void Load()
        {
            if (loaded) return;
            loaded = true;
            string path = FilePath();
            if (path == null || !File.Exists(path)) return;
            try
            {
                ConfigNode root = ConfigNode.Load(path);
                ConfigNode node = root != null ? root.GetNode(NodeName) : null;
                if (node == null) return;
                foreach (ConfigNode.Value value in node.values)
                    if (KeyCombination.IsText(value.value)) bindings[value.name] = value.value;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(Log.Tag + " The key bindings kept for other mods could not be read: " + e.Message);
            }
        }

        // Null outside the game, where KSP has no folder to ask for: the bindings
        // are kept in memory only.
        private static string FilePath()
        {
            try
            {
                return PluginData.Path(FileName);
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        private static void Save()
        {
            string path = FilePath();
            if (path == null) return;
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            ConfigNode root = new ConfigNode();
            ConfigNode node = root.AddNode(NodeName);
            foreach (KeyValuePair<string, string> pair in bindings) node.AddValue(pair.Key, pair.Value);
            // A failure reaches the window's Apply, which says the row could not be set.
            if (!root.Save(path)) throw new IOException("KSP's ConfigNode.Save did not write " + path);
        }
    }
}
