using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Probe
{
    // With -redefinitionShaderInventory: six seconds into each scene, every renderer's
    // materials grouped by shader, with what each says about its shine -- specular colour,
    // shininess, smoothness, metallic, their maps, a reflection cube -- and where the
    // renderers stand (a vessel's part, the space centre's buildings, the terrain), into
    // the session's capture folder, shader-inventory-<scene>.txt. What the shine of a
    // surface is meant to be is read from these, per shader family.
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    internal sealed class ShaderInventory : MonoBehaviour
    {
        // The command-line switch, and the scenes already written this session (each scene once).
        internal const string Argument = "-redefinitionShaderInventory";
        private static readonly HashSet<GameScenes> Done = new HashSet<GameScenes>();

        // The material properties read: numbers as they are, colours by their largest channel, textures by whether set.
        private static readonly string[] Floats =
            { "_Shininess", "_Glossiness", "_Smoothness", "_GlossMapScale", "_Metallic", "_SpecularIntensity", "_SpecularPower",
              "_Fresnel", "_FresnelPower", "_RimFalloff", "_Opacity", "_Cutoff" };
        private static readonly string[] Colours = { "_SpecColor", "_ReflectColor", "_ReflectionColor", "_Color", "_EmissiveColor" };
        private static readonly string[] Textures =
            { "_MainTex", "_SpecMap", "_SpecGlossMap", "_MetallicGlossMap", "_BumpMap", "_Cube", "_Emissive", "_MaskTex" };

        // One shader's materials and what they hold.
        private sealed class Group
        {
            // The shader's name, its distinct materials, its renderers by kind (vessel, scenery, terrain ...), each
            // property's values over the materials, how many set each texture, up to six examples, and its renderer count.
            public string Shader;
            public readonly HashSet<Material> Materials = new HashSet<Material>();
            public readonly Dictionary<string, int> Kinds = new Dictionary<string, int>();
            public readonly Dictionary<string, List<float>> Values = new Dictionary<string, List<float>>();
            public readonly Dictionary<string, int> TextureSet = new Dictionary<string, int>();
            public readonly List<string> Examples = new List<string>();
            public int Renderers;
        }

        // Six seconds into a scene not yet written, with the switch, its inventory into the capture folder.
        private IEnumerator Start()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), Argument) < 0) yield break;
            GameScenes scene = HighLogic.LoadedScene;
            if (Done.Contains(scene)) yield break;
            yield return new WaitForSeconds(6f);
            if (HighLogic.LoadedScene != scene || !Done.Add(scene)) yield break;
            try
            {
                Write(scene);
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Shader inventory: " + e);
            }
        }

        // Where a renderer stands: a vessel's part, the space centre's scenery, the terrain, or else its layer.
        private static string Kind(Renderer renderer)
        {
            if (renderer.GetComponentInParent<Part>() != null) return "vessel";
            if (renderer.GetComponentInParent<PQSCity>() != null || renderer.GetComponentInParent<PQSCity2>() != null) return "scenery";
            if (renderer.GetComponent<PQ>() != null) return "terrain";
            return "other(layer " + renderer.gameObject.layer + ")";
        }

        // Gathers the scene's materials by shader and writes them, the space centre's and vessels' materials one by one,
        // the transparent renderers near the vessel and the launch pad's and runway's levels into shader-inventory-<scene>.txt.
        private static void Write(GameScenes scene)
        {
            var groups = new Dictionary<string, Group>();
            var materials = new List<Material>();
            foreach (Renderer renderer in FindObjectsOfType<Renderer>())
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (renderer is ParticleSystemRenderer || renderer is LineRenderer || renderer is TrailRenderer) continue;
                renderer.GetSharedMaterials(materials);
                string kind = Kind(renderer);
                foreach (Material material in materials)
                {
                    if (material == null || material.shader == null) continue;
                    string name = material.shader.name;
                    Group group;
                    if (!groups.TryGetValue(name, out group)) groups[name] = group = new Group { Shader = name };
                    group.Renderers++;
                    int count;
                    group.Kinds.TryGetValue(kind, out count);
                    group.Kinds[kind] = count + 1;
                    if (group.Examples.Count < 6 && !group.Materials.Contains(material))
                        group.Examples.Add(material.name + " on " + renderer.name + " (" + kind + ")");
                    if (!group.Materials.Add(material)) continue;
                    foreach (string property in Floats)
                        if (material.HasProperty(property)) Add(group, property, material.GetFloat(property));
                    foreach (string property in Colours)
                        if (material.HasProperty(property))
                        {
                            Color c = material.GetColor(property);
                            Add(group, property + ".max", Mathf.Max(c.r, Mathf.Max(c.g, c.b)));
                        }
                    foreach (string property in Textures)
                        if (material.HasProperty(property) && material.GetTexture(property) != null)
                        {
                            group.TextureSet.TryGetValue(property, out count);
                            group.TextureSet[property] = count + 1;
                        }
                }
            }

            var text = new StringBuilder();
            text.Append("Shader inventory, scene ").Append(scene).Append(", ").Append(groups.Count).AppendLine(" shaders")
                .AppendLine("value: min / median / max over the shader's materials");
            foreach (Group group in groups.Values.OrderByDescending(g => g.Renderers))
            {
                text.AppendLine().Append("== ").Append(group.Shader).Append(": ").Append(group.Renderers).Append(" renderers, ")
                    .Append(group.Materials.Count).Append(" materials; ")
                    .AppendLine(string.Join(", ", group.Kinds.Select(k => k.Key + " " + k.Value).ToArray()));
                foreach (KeyValuePair<string, List<float>> value in group.Values)
                {
                    value.Value.Sort();
                    text.Append("   ").Append(value.Key).Append(": ").Append(F(value.Value[0])).Append(" / ")
                        .Append(F(value.Value[value.Value.Count / 2])).Append(" / ").AppendLine(F(value.Value[value.Value.Count - 1]));
                }
                if (group.TextureSet.Count > 0)
                    text.Append("   textures set: ")
                        .AppendLine(string.Join(", ", group.TextureSet.Select(t => t.Key + " " + t.Value).ToArray()));
                foreach (string example in group.Examples) text.Append("   e.g. ").AppendLine(example);
            }
            // The space centre's materials one by one, with the shine the raytracing gives them.
            var scenery = new Dictionary<Material, List<string>>();
            foreach (Renderer renderer in FindObjectsOfType<Renderer>())
            {
                string kind = renderer != null ? Kind(renderer) : null;
                if (renderer == null || !renderer.enabled || (kind != "scenery" && kind != "vessel")) continue;
                renderer.GetSharedMaterials(materials);
                foreach (Material material in materials)
                {
                    if (material == null || material.shader == null) continue;
                    List<string> on;
                    if (!scenery.TryGetValue(material, out on)) scenery[material] = on = new List<string>();
                    if (on.Count < 4) on.Add(renderer.name + (kind == "vessel" ? " (vessel)" : ""));
                }
            }
            text.AppendLine().AppendLine("== The space centre's and the vessels' materials: shader, _SpecColor, _Shininess, main texture, raytracing mode, on");
            foreach (KeyValuePair<Material, List<string>> entry in scenery.OrderBy(e => e.Key.shader.name).ThenBy(e => e.Key.name))
            {
                Material material = entry.Key;
                Color spec = material.HasProperty("_SpecColor") ? material.GetColor("_SpecColor") : Color.clear;
                Texture main = material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
                text.Append("   ").Append(material.name.Replace(" (Instance)", "")).Append(" | ").Append(material.shader.name)
                    .Append(" | ").Append(material.HasProperty("_SpecColor") ? F(spec.r) + "," + F(spec.g) + "," + F(spec.b) : "-")
                    .Append(" | ").Append(material.HasProperty("_Shininess") ? F(material.GetFloat("_Shininess")) : "-")
                    .Append(" | ").Append(main != null ? main.name : "-")
                    .Append(" | ").Append(Raytracing.RtMaterialIntent.Of(material, !entry.Value.Exists(n => n.EndsWith(" (vessel)", StringComparison.Ordinal))).Mode)
                    .Append(" | ").AppendLine(string.Join(", ", entry.Value.ToArray()));
            }
            // Transparent renderers within 150 m of the active vessel: what draws there after
            // the opaque scene, by name, material and shader.
            Vessel active = FlightGlobals.ActiveVessel;
            if (active != null)
            {
                text.AppendLine().AppendLine("== Transparent renderers within 150 m of the vessel: renderer, parent, material, shader, queue, size, distance");
                foreach (Renderer renderer in FindObjectsOfType<Renderer>())
                {
                    if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                    float distance = (renderer.bounds.center - (Vector3)active.CoMD).magnitude;
                    if (distance > 150f || renderer.bounds.size.magnitude > 1000f) continue;
                    renderer.GetSharedMaterials(materials);
                    foreach (Material material in materials)
                        if (material != null && material.renderQueue >= 2450)
                            text.Append("   ").Append(renderer.GetType().Name).Append(" '").Append(renderer.name).Append("' under '")
                                .Append(renderer.transform.parent != null ? renderer.transform.parent.name : "-").Append("' | ")
                                .Append(material.name).Append(" | ").Append(material.shader != null ? material.shader.name : "-").Append(" | ")
                                .Append(material.renderQueue).Append(" | ").Append(renderer.bounds.size.ToString("F1")).Append(" | ")
                                .AppendLine(distance.ToString("0", CultureInfo.InvariantCulture));
                }
            }
            // Every level of the launch pad and the runway, from each level's prefab
            // (UpgradeableObject.upgradeLevels): its shiny materials and the shine the
            // raytracing gives them, whichever level stands.
            text.AppendLine().AppendLine("== Launch pad and runway levels: facility, level, material, shader, _SpecColor, _Shininess, raytracing mode");
            var levelMaterials = new List<Material>();
            foreach (Upgradeables.UpgradeableObject facility in FindObjectsOfType<Upgradeables.UpgradeableObject>())
            {
                if (facility == null || facility.id == null
                    || (facility.id.IndexOf("LaunchPad", StringComparison.Ordinal) < 0 && facility.id.IndexOf("Runway", StringComparison.Ordinal) < 0)
                    || facility.UpgradeLevels == null)
                    continue;
                for (int level = 0; level < facility.UpgradeLevels.Length; level++)
                {
                    GameObject prefab = facility.UpgradeLevels[level].facilityPrefab;
                    if (prefab == null) continue;
                    var seen = new HashSet<Material>();
                    foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
                    {
                        renderer.GetSharedMaterials(levelMaterials);
                        foreach (Material material in levelMaterials)
                        {
                            if (material == null || material.shader == null || !seen.Add(material)) continue;
                            if (material.shader.name.IndexOf("Specular", StringComparison.Ordinal) < 0) continue;
                            Color spec = material.HasProperty("_SpecColor") ? material.GetColor("_SpecColor") : Color.clear;
                            text.Append("   ").Append(facility.id).Append(" | ").Append(level).Append(" | ").Append(material.name)
                                .Append(" | ").Append(material.shader.name).Append(" | ")
                                .Append(material.HasProperty("_SpecColor") ? F(spec.r) + "," + F(spec.g) + "," + F(spec.b) : "-").Append(" | ")
                                .Append(material.HasProperty("_Shininess") ? F(material.GetFloat("_Shininess")) : "-").Append(" | ")
                                .AppendLine(Raytracing.RtMaterialIntent.Of(material, true).Mode.ToString());
                        }
                    }
                }
            }
            string folder = CaptureFolder.Run;
            string file = Path.Combine(folder, "shader-inventory-" + scene + ".txt");
            File.WriteAllText(file, text.ToString());
            Debug.Log(Log.Tag + " Shader inventory: " + groups.Count + " shaders in " + scene + " into " + file + ".");
        }

        // One value of a property added to the group's list for it.
        private static void Add(Group group, string key, float value)
        {
            List<float> list;
            if (!group.Values.TryGetValue(key, out list)) group.Values[key] = list = new List<float>();
            list.Add(value);
        }

        // A number with up to three decimals, for the file.
        private static string F(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
