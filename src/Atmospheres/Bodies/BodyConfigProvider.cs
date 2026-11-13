using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // The body provider of the atmospheres: reads the REDEFINITION_BODY nodes of the GameDatabase (after ModuleManager), probes each
    // body once on first use, and caches the climate. Nodes are matched by body name; a node named * is the default for any body
    // without a node of its own. One summary line per body goes to the log, with the warnings. Install() makes it the provider of Bodies.
    internal sealed class BodyConfigProvider : IBodyProvider
    {
        private readonly Func<IEnumerable<ConfigNode>> nodeSource;
        private readonly Func<IEnumerable<string>> fieldSource;
        private readonly Func<IDictionary<string, BodyPlayerOptions>> playerSource;
        private readonly Func<int> seedSource;
        private readonly Dictionary<string, BodyClimate> cache = new Dictionary<string, BodyClimate>(StringComparer.OrdinalIgnoreCase);
        // Where the lines go; tests replace them.
        internal Action<string> Say = AtmospheresLog.Say;
        internal Action<string, string> Warn = AtmospheresLog.Warn;
        private readonly HashSet<string> loggedKeyWarnings = new HashSet<string>();
        private Dictionary<string, ConfigNode> nodes;
        private ConfigNode wildcard;
        private IEnumerable<string> fields;
        private IDictionary<string, BodyPlayerOptions> player;

        // The provider with the sources of the game; the other constructor is for tests.
        internal BodyConfigProvider()
            : this(GameNodes, EveFieldNames, () => BodiesFile.Read(null), () => AtmospheresSettings.GenSeed)
        {
        }

        internal BodyConfigProvider(Func<IEnumerable<ConfigNode>> nodeSource, Func<IEnumerable<string>> fieldSource,
            Func<IDictionary<string, BodyPlayerOptions>> playerSource, Func<int> seedSource)
        {
            this.nodeSource = nodeSource;
            this.fieldSource = fieldSource;
            this.playerSource = playerSource;
            this.seedSource = seedSource;
        }

        // Makes this provider the one Bodies uses.
        internal static BodyConfigProvider Install()
        {
            var provider = new BodyConfigProvider();
            Bodies.Provider = provider;
            return provider;
        }

        public BodyClimate Get(CelestialBody body)
        {
            if (body == null) return null;
            BodyClimate climate;
            if (cache.TryGetValue(body.name, out climate)) return climate;
            var notes = new List<string>();
            LoadSources(notes);
            BodyFacts facts = BodyProbe.Read(body, fields, notes);
            climate = Build(facts, notes);
            cache[body.name] = climate;
            return climate;
        }

        // Forgets every climate, node and option: the next Get reads them again (a changed configuration, a new game).
        internal void Reset()
        {
            cache.Clear();
            nodes = null;
            wildcard = null;
            fields = null;
            player = null;
        }

        // The climate of a body from its facts: the matching node, the player's options, the parse, the log line. Public to the tests.
        internal BodyClimate Build(BodyFacts facts, List<string> notes)
        {
            LoadSources(notes);
            ConfigNode node;
            bool wild = false;
            if (!nodes.TryGetValue(facts.Name, out node))
            {
                node = wildcard;
                wild = node != null && facts.HasAtmosphere;
                if (!wild) node = null;
            }
            BodyPlayerOptions options;
            if (player == null || !player.TryGetValue(facts.Name, out options)) options = BodyPlayerOptions.Default;
            BodyParseResult result = BodyConfigParser.Parse(facts, node, options, seedSource());
            string source = node == null ? "no node" : (wild ? "node *" : "node");
            Say(result.Summary + " [" + source + "]");
            foreach (string note in notes) Warn("body-config", facts.Name + ": " + note);
            foreach (string warning in result.Warnings)
            {
                // An unknown key is logged once, whatever the kind's count of other warnings (a node that has three typos says all three).
                if (warning.Contains(BodyNodeReader.UnknownKeyText))
                {
                    if (loggedKeyWarnings.Add(warning)) Warn("body-config-key " + loggedKeyWarnings.Count, warning);
                }
                else Warn("body-config", warning);
            }
            return result.Climate;
        }

        private void LoadSources(List<string> notes)
        {
            if (nodes != null) return;
            nodes = new Dictionary<string, ConfigNode>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (ConfigNode node in nodeSource() ?? new ConfigNode[0])
                {
                    string name = node.GetValue("name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        notes.Add("a REDEFINITION_BODY node without a name is ignored");
                        continue;
                    }
                    name = name.Trim();
                    if (name == "*")
                    {
                        wildcard = node;
                        continue;
                    }
                    if (nodes.ContainsKey(name)) notes.Add("there is more than one REDEFINITION_BODY node for " + name + "; the last one counts");
                    nodes[name] = node;
                }
            }
            catch (Exception e)
            {
                notes.Add("the REDEFINITION_BODY nodes could not be read (" + e.GetType().Name + ": " + e.Message + ")");
            }
            try
            {
                fields = fieldSource();
            }
            catch (Exception e)
            {
                notes.Add("the names of EVE's particle fields could not be read (" + e.GetType().Name + ": " + e.Message + ")");
            }
            try
            {
                player = playerSource();
            }
            catch (Exception e)
            {
                notes.Add("bodies.cfg could not be read (" + e.GetType().Name + ": " + e.Message + ")");
            }
        }

        private static IEnumerable<ConfigNode> GameNodes()
        {
            return GameDatabase.Instance == null ? new ConfigNode[0] : GameDatabase.Instance.GetConfigNodes(BodyConfigParser.NodeName);
        }

        // The names of EVE's particle fields: the OBJECT nodes of every EVE_PARTICLE_FIELD_CONFIG node.
        private static IEnumerable<string> EveFieldNames()
        {
            var names = new List<string>();
            if (GameDatabase.Instance == null) return names;
            foreach (ConfigNode config in GameDatabase.Instance.GetConfigNodes("EVE_PARTICLE_FIELD_CONFIG"))
            {
                foreach (ConfigNode field in config.GetNodes("OBJECT"))
                {
                    string name = field.GetValue("name");
                    if (!string.IsNullOrEmpty(name)) names.Add(name.Trim());
                }
            }
            return names;
        }
    }
}
