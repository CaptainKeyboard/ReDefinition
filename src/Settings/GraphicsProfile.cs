using System;
using System.Collections.Generic;

namespace ReDefinition.Settings
{
    // A graphics profile as the chooser shows it, with the values of ReDefinition's
    // own modules. What it sets for the registered mods stands in their registrations
    // (ModProfiles). Config nodes loaded through the GameDatabase, so a visual pack can
    // add or patch profiles with ModuleManager.
    //
    //   GRAPHICS_PROFILE
    //   {
    //       name = balanced
    //       title = Balanced
    //       order = 3
    //       hardware = RTX 3080 class
    //       description = ...
    //       MODULE { name = upscaler   enabled = True   quality = NativeAA }
    //   }
    internal sealed class GraphicsProfile
    {
        public const string NodeName = "GRAPHICS_PROFILE";
        public const string ModuleNodeName = "MODULE";

        private static readonly string[] ProfileKeys = { "name", "title", "description", "order", "hardware" };

        public string Name = "";
        public string Title = "";
        public string Description = "";

        // Where it stands among the profiles, lowest first -- Low before Max;
        // a profile without one comes after those that have one.
        public int Order = int.MaxValue;

        // The hardware it is made for, in a few words, for the chooser.
        public string Hardware = "";

        // By module name: a copy of the MODULE node, so that nothing done to a
        // profile reaches the GameDatabase it was read from.
        public readonly Dictionary<string, ConfigNode> Modules = new Dictionary<string, ConfigNode>();

        // Everything wrong with a node goes into problems, with the profile's
        // name, and the profile is still built from what is usable. Returns null
        // only when the node has no name at all.
        public static GraphicsProfile FromConfigNode(ConfigNode node, List<string> problems)
        {
            if (node == null) return null;

            string name = node.GetValue("name");
            if (string.IsNullOrEmpty(name))
            {
                problems.Add(NodeName + " without a name -- skipped.");
                return null;
            }

            string title = node.GetValue("title");
            GraphicsProfile profile = new GraphicsProfile
            {
                Name = name,
                Title = string.IsNullOrEmpty(title) ? name : title,
                Description = node.GetValue("description") ?? "",
                Hardware = node.GetValue("hardware") ?? "",
            };
            string where = "Profile '" + name + "'";

            string orderText = node.GetValue("order");
            if (!string.IsNullOrEmpty(orderText))
            {
                int order;
                if (int.TryParse(orderText, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out order))
                    profile.Order = order;
                else
                    problems.Add(where + ": order '" + orderText + "' is not a whole number -- ignored.");
            }

            foreach (ConfigNode.Value value in node.values)
            {
                if (Array.IndexOf(ProfileKeys, value.name) < 0)
                    problems.Add(where + ": unknown key '" + value.name + "' -- ignored.");
            }

            foreach (ConfigNode child in node.GetNodes())
            {
                if (child.name != ModuleNodeName)
                {
                    problems.Add(where + ": unknown node '" + child.name + "' -- ignored.");
                    continue;
                }

                string key = child.GetValue("name");
                if (string.IsNullOrEmpty(key))
                {
                    problems.Add(where + ": " + child.name + " without a name -- skipped.");
                    continue;
                }
                if (profile.Modules.ContainsKey(key))
                    problems.Add(where + ": module '" + key + "' twice -- the last one counts.");
                profile.Modules[key] = child.CreateCopy();
            }

            return profile;
        }
    }
}
