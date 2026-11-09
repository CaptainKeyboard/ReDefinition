using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Planner
{
    // The planner's two switches where ReDefinition is not installed, in GameData/ReDefinitionPlanner/PluginData/settings.cfg. With ReDefinition
    // they are its own settings (PlannerSeams), and this file is not read.
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
            Read(ModFolder.PluginFile("ReDefinitionPlanner", FileName));
        }

        // The switches from that file; they stay as they are where it is missing or damaged.
        internal static void Read(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return;
                ConfigNode root = ConfigNode.Load(path);
                if (root == null) return;
                bool value;
                if (bool.TryParse(root.GetValue("backgroundRoute"), out value)) BackgroundRoute = value;
                if (bool.TryParse(root.GetValue("worldButtons"), out value)) WorldButtons = value;
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("planner-settings", "The planner's settings could not be read (" + CompatibilityLog.Reason(e) + ").");
            }
        }

        // Writes both switches.
        internal static void Save()
        {
            Write(ModFolder.PluginFile("ReDefinitionPlanner", FileName));
        }

        // Both switches into that file.
        internal static void Write(string path)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                var root = new ConfigNode();
                root.AddValue("backgroundRoute", BackgroundRoute.ToString());
                root.AddValue("worldButtons", WorldButtons.ToString());
                root.Save(path);
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("planner-settings", "The planner's settings could not be saved (" + CompatibilityLog.Reason(e) + ").");
            }
        }
    }
}
