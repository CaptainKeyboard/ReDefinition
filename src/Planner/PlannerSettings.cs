using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Planner
{
    // The planner's two switches where ReDefinition is not installed, in GameData/ReDefinition/Planner/PluginData/settings.cfg. With ReDefinition
    // they are its own settings (HostSeams), and this file is not read.
    internal static class PlannerSettings
    {
        private const string FileName = "settings.cfg";

        // The route is flown by the vessel itself in the background.
        internal static bool BackgroundRoute = true;
        // The buttons that stand in the world show.
        internal static bool WorldButtons = true;

        private static bool loaded;

        // Reads the file once; a missing or damaged one leaves the defaults.
        internal static void Load()
        {
            if (loaded) return;
            loaded = true;
            Read(ModFolder.PluginFile("ReDefinition/Planner", FileName));
        }

        // The switches from that file; they stay as they are where it is missing or damaged.
        internal static void Read(string path)
        {
            ModSettingsFile.Read(path, Apply, false);
        }

        private static bool Apply(ConfigNode root)
        {
            bool value;
            if (ModSettingsFile.TryBool(root, "backgroundRoute", out value)) BackgroundRoute = value;
            if (ModSettingsFile.TryBool(root, "worldButtons", out value)) WorldButtons = value;
            return true;
        }

        // Writes both switches.
        internal static void Save()
        {
            Write(ModFolder.PluginFile("ReDefinition/Planner", FileName));
        }

        // Both switches into that file.
        internal static void Write(string path)
        {
            ModSettingsFile.Write(path, Fill);
        }

        private static void Fill(ConfigNode root)
        {
            root.AddValue("backgroundRoute", BackgroundRoute.ToString());
            root.AddValue("worldButtons", WorldButtons.ToString());
        }
    }
}
