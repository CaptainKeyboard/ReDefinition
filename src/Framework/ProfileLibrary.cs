using System.Collections.Generic;

namespace ReDefinition.Framework
{
    // Every profile in the GameDatabase, in the order it was loaded. To be
    // read from the main menu on, when loading -- ModuleManager's patches
    // included -- is finished, so that what a visual pack's patch made of a
    // profile is what arrives here. GameDatabase.IsReady alone does not include
    // ModuleManager's pass, so the guard is ModRegistry.Ready's.
    internal static class ProfileLibrary
    {
        public static List<GraphicsProfile> LoadAll(List<string> problems)
        {
            List<GraphicsProfile> profiles = new List<GraphicsProfile>();
            if (!ModRegistry.Ready())
            {
                problems.Add("The game is still loading -- no profiles yet. Read them from the main menu on.");
                return profiles;
            }

            Dictionary<string, int> byName = new Dictionary<string, int>();
            foreach (ConfigNode node in GameDatabase.Instance.GetConfigNodes(GraphicsProfile.NodeName))
            {
                GraphicsProfile profile = GraphicsProfile.FromConfigNode(node, problems);
                if (profile == null) continue;

                // A second node of the same name is almost always a patch that
                // should have used @ to edit the first. The later one wins, as
                // with the last node ModuleManager leaves, and it is reported.
                int index;
                if (byName.TryGetValue(profile.Name, out index))
                {
                    problems.Add("Profile '" + profile.Name + "' defined twice -- the later one counts."
                                 + " A patch meant to change it should edit it with @.");
                    profiles[index] = profile;
                    continue;
                }

                byName[profile.Name] = profiles.Count;
                profiles.Add(profile);
            }
            return profiles;
        }
    }
}
