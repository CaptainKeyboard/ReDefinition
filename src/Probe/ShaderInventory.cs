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
    // ReDefinitionCaptures/shader-inventory-<scene>.txt. What the shine of a surface is
    // meant to be is read from these, per shader family.
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    internal sealed class ShaderInventory : MonoBehaviour
    {
        internal const string Argument = "-redefinitionShaderInventory";
        private static readonly HashSet<GameScenes> Done = new HashSet<GameScenes>();

        private static readonly string[] Floats =
            { "_Shininess", "_Glossiness", "_Smoothness", "_GlossMapScale", "_Metallic", "_SpecularIntensity", "_SpecularPower",
              "_Fresnel", "_FresnelPower", "_RimFalloff", "_Opacity", "_Cutoff" };
        private static readonly string[] Colours = { "_SpecColor", "_ReflectColor", "_ReflectionColor", "_Color", "_EmissiveColor" };
        private static readonly string[] Textures =
            { "_MainTex", "_SpecMap", "_SpecGlossMap", "_MetallicGlossMap", "_BumpMap", "_Cube", "_Emissive", "_MaskTex" };

        private sealed class Group
        {
            public string Shader;
            public readonly HashSet<Material> Materials = new HashSet<Material>();
            public readonly Dictionary<string, int> Kinds = new Dictionary<string, int>();
            public readonly Dictionary<string, List<float>> Values = new Dictionary<string, List<float>>();
            public readonly Dictionary<string, int> TextureSet = new Dictionary<string, int>();
            public readonly List<string> Examples = new List<string>();
            public int Renderers;
        }

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

        private static string Kind(Renderer renderer)
        {
            if (renderer.GetComponentInParent<Part>() != null) return "vessel";
            if (renderer.GetComponentInParent<PQSCity>() != null || renderer.GetComponentInParent<PQSCity2>() != null) return "scenery";
            if (renderer.GetComponent<PQ>() != null) return "terrain";
            return "other(layer " + renderer.gameObject.layer + ")";
        }

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
            string folder = Path.Combine(KSPUtil.ApplicationRootPath, "ReDefinitionCaptures");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "shader-inventory-" + scene + ".txt");
            File.WriteAllText(file, text.ToString());
            Debug.Log(Log.Tag + " Shader inventory: " + groups.Count + " shaders in " + scene + " into " + file + ".");
        }

        private static void Add(Group group, string key, float value)
        {
            List<float> list;
            if (!group.Values.TryGetValue(key, out list)) group.Values[key] = list = new List<float>();
            list.Add(value);
        }

        private static string F(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
