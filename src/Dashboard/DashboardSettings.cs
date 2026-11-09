using System;
using System.Text;
using ReDefinition.Core;

namespace ReDefinition.Dashboard
{
    // The dashboard's two settings: whether it opens on the network, and the key a phone needs. With ReDefinition they are its settings
    // (HostSeams), held in its settings window. Without it they are in settings.cfg in the dashboard's own PluginData folder, which is
    // not read while ReDefinition is present. DashboardServer and the dialogs read them through the accessors at the end.
    internal static class DashboardSettings
    {
        private const string FileName = "settings.cfg";

        // The letters a new key is drawn from: lower case and digits without l, o, 0 and 1, which read alike.
        private const string KeyLetters = "abcdefghijkmnpqrstuvwxyz23456789";

        // The values of the file; they are used only where no host is present.
        internal static bool Network = true;
        internal static string Key = "";

        private static bool loaded;

        // Reads the file once; a missing or damaged one leaves the defaults.
        internal static void Load()
        {
            if (loaded) return;
            loaded = true;
            Read(ModFolder.PluginFile("ReDefinition/Dashboard", FileName));
        }

        // The settings from that file; they stay as they are where it is missing or damaged.
        internal static void Read(string path)
        {
            ModSettingsFile.Read(path, Apply, false);
        }

        private static bool Apply(ConfigNode root)
        {
            bool network;
            if (ModSettingsFile.TryBool(root, "network", out network)) Network = network;
            string key;
            if (ModSettingsFile.TryString(root, "key", out key)) Key = key;
            return true;
        }

        internal static void Save()
        {
            Write(ModFolder.PluginFile("ReDefinition/Dashboard", FileName));
        }

        // Both settings into that file.
        internal static void Write(string path)
        {
            ModSettingsFile.Write(path, Fill);
        }

        private static void Fill(ConfigNode root)
        {
            root.AddValue("network", Network.ToString());
            root.AddValue("key", Key ?? "");
        }

        // A random key of eight letters.
        internal static string NewKey()
        {
            var random = new Random();
            StringBuilder key = new StringBuilder();
            for (int i = 0; i < 8; i++) key.Append(KeyLetters[random.Next(KeyLetters.Length)]);
            return key.ToString();
        }

        // Whether the settings can be read: always alone, and with ReDefinition once it has loaded its own.
        internal static bool Ready
        {
            get
            {
                if (!HostSeams.StandsAlone()) return HostSeams.DashboardKey() != null;
                Load();
                return true;
            }
        }

        // Whether the dashboard opens on the network.
        internal static bool NetworkOn
        {
            get
            {
                if (!HostSeams.StandsAlone()) return HostSeams.DashboardNetwork();
                Load();
                return Network;
            }
        }

        // The key a phone needs; "" where there is none yet.
        internal static string CurrentKey
        {
            get
            {
                if (!HostSeams.StandsAlone()) return HostSeams.DashboardKey() ?? "";
                Load();
                return Key ?? "";
            }
        }

        // The key, made and kept where there is none yet. Call it when Ready.
        internal static string EnsureKey()
        {
            string key = CurrentKey;
            if (key != "") return key;
            key = NewKey();
            if (!HostSeams.StandsAlone())
            {
                HostSeams.SetDashboardKey(key);
                return key;
            }
            Key = key;
            Save();
            return key;
        }

        // Sets the switch where the settings are the dashboard's own; with ReDefinition the player does it in its window.
        internal static void SetNetwork(bool on)
        {
            if (!HostSeams.StandsAlone()) return;
            Network = on;
            Save();
        }
    }
}
