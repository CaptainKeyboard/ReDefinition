using System;
using System.Collections.Generic;
using System.Globalization;

namespace ReDefinition.Atmospheres
{
    // Reads the volumetric layers out of EVE_CLOUDS nodes: what a pack's config (or one of our generated files) puts on a body. Pure: it
    // takes ConfigNodes and returns plain data, so the discovery runs in tests on stored pack files. The key names are those of EVE's
    // raymarched layers (release-5 tree, CloudsObject and CloudsRaymarchedVolume): OBJECT { name, body, altitude, layerRaymarchedVolumeV5
    // { coverageMap, cloudTypeMap, cloudTypes { Item { typeName } }, raymarchingSettings { fxOnlyLayer }, particleField
    // { particleFieldConfig } } }. An OBJECT without layerRaymarchedVolumeV5 is a classic 2D layer and is not a layer of ours.
    internal static class PackConfigReader
    {
        internal const string NodeName = "EVE_CLOUDS";

        // The layers of every EVE_CLOUDS node among the given nodes (a node's name may carry a ModuleManager suffix such as
        // ":NEEDS[StockVolumetricClouds]", which is not part of the name here). Other nodes are skipped.
        internal static List<PackLayerInfo> Read(IEnumerable<ConfigNode> roots, string origin)
        {
            var layers = new List<PackLayerInfo>();
            if (roots == null) return layers;
            foreach (ConfigNode root in roots)
            {
                if (root == null) continue;
                if (IsCloudsNode(root.name)) AddObjects(root, origin, layers);
                else
                {
                    // A file: the nodes below the root, or the root of a file read whole (name empty).
                    foreach (ConfigNode child in root.nodes)
                    {
                        if (IsCloudsNode(child.name)) AddObjects(child, origin, layers);
                    }
                }
            }
            return layers;
        }

        // The layers of a file's text.
        internal static List<PackLayerInfo> ReadText(string text, string origin)
        {
            ConfigNode root = ConfigNode.Parse(text ?? "");
            return Read(new[] { root }, origin);
        }

        private static bool IsCloudsNode(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            int colon = name.IndexOf(':');
            string bare = colon < 0 ? name : name.Substring(0, colon);
            return string.Equals(bare.TrimStart('@', '+', '!', '-', '%', '&', '*').Trim(), NodeName, StringComparison.Ordinal);
        }

        private static void AddObjects(ConfigNode clouds, string origin, List<PackLayerInfo> layers)
        {
            foreach (ConfigNode obj in clouds.GetNodes("OBJECT"))
            {
                ConfigNode volume = obj.GetNode("layerRaymarchedVolumeV5");
                if (volume == null) continue;
                var layer = new PackLayerInfo
                {
                    Name = (obj.GetValue("name") ?? "").Trim(),
                    Body = (obj.GetValue("body") ?? "").Trim(),
                    AltitudeM = Number(obj.GetValue("altitude")),
                    HasCoverageMap = HasMap(volume.GetNode("coverageMap")),
                    HasTypeMap = HasMap(volume.GetNode("cloudTypeMap")),
                    Origin = origin ?? ""
                };
                ConfigNode raymarching = volume.GetNode("raymarchingSettings");
                layer.FxOnly = raymarching != null && IsTrue(raymarching.GetValue("fxOnlyLayer"));
                ConfigNode field = volume.GetNode("particleField");
                if (field != null) layer.ParticleField = (field.GetValue("particleFieldConfig") ?? "").Trim();
                ConfigNode types = volume.GetNode("cloudTypes");
                if (types != null)
                {
                    foreach (ConfigNode item in types.GetNodes("Item")) layer.TypeNames.Add((item.GetValue("typeName") ?? "").Trim());
                }
                layers.Add(layer);
            }
        }

        // A map node counts when it names a texture (its node value, key "value").
        private static bool HasMap(ConfigNode map)
        {
            return map != null && !string.IsNullOrEmpty((map.GetValue("value") ?? "").Trim());
        }

        private static bool IsTrue(string text)
        {
            return text != null && string.Equals(text.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static double Number(string text)
        {
            double value;
            return text != null && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0.0;
        }
    }
}
