using System;
using System.IO;

namespace ReDefinition.Core
{
    // A mod's settings file, a ConfigNode in GameData/ReDefinition/<Mod>/PluginData. A missing or damaged file gives the fallback;
    // a file that cannot be written is logged and left. The overloads that take a full path are for tests.
    internal static class ModSettingsFile
    {
        private const string Kind = "mod-settings";

        // The file's content as parse reads it; the fallback where the file is missing, empty or damaged.
        internal static T Read<T>(string mod, string file, Func<ConfigNode, T> parse, T fallback)
        {
            return Read(ModFolder.PluginFile(mod, file), parse, fallback);
        }

        internal static T Read<T>(string path, Func<ConfigNode, T> parse, T fallback)
        {
            try
            {
                if (!File.Exists(path)) return fallback;
                ConfigNode root = ConfigNode.Load(path);
                return root == null ? fallback : parse(root);
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn(Kind, "The settings file " + path + " could not be read (" + CompatibilityLog.Reason(e) + ").");
                return fallback;
            }
        }

        // Writes what fill puts into a new node; the folder is created. False where it could not be written.
        internal static bool Write(string mod, string file, Action<ConfigNode> fill)
        {
            return Write(ModFolder.PluginFile(mod, file), fill);
        }

        internal static bool Write(string path, Action<ConfigNode> fill)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                ConfigNode root = new ConfigNode();
                fill(root);
                root.Save(path);
                return true;
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn(Kind, "The settings file " + path + " could not be saved (" + CompatibilityLog.Reason(e) + ").");
                return false;
            }
        }

        // The value of a key, if it is there and reads as the type.
        internal static bool TryBool(ConfigNode node, string key, out bool value)
        {
            return bool.TryParse(node.GetValue(key), out value);
        }

        internal static bool TryString(ConfigNode node, string key, out string value)
        {
            value = node.GetValue(key);
            return value != null;
        }

        internal static bool TryInt(ConfigNode node, string key, out int value)
        {
            return int.TryParse(node.GetValue(key), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        internal static bool TryFloat(ConfigNode node, string key, out float value)
        {
            return float.TryParse(node.GetValue(key), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }
    }
}
