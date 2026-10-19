using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // The VAB's and SPH's halls as their materials mean their shine (RtMaterialIntent) while
    // the raytracing runs. The editors draw the halls with a camera of their own, on the
    // scenery's layer, which the raytracing's surfaces never reach (RtCamera,
    // SurfaceIntent), so Deferred's G-buffer kept its guesses there: the hall's concrete a
    // mirror (_SpecColor 1, _Shininess 1: smoothness 1). Here the materials are given values
    // from which Deferred's own conversion (LegacyToStandard.cginc) writes what is meant:
    // matt ones the specular variant of their shader with a black specular colour (spec 0,
    // smoothness 0 in the G-buffer); KSP's legacy highlight a specular colour as bright as
    // the highlight and a shininess for its roughness, both at the gloss's mean, as
    // Deferred cannot scale the strength by the gloss. Everything is put back when the
    // raytracing stops or the scene goes.
    internal sealed class EditorHallShine
    {
        private sealed class Original
        {
            public Shader Shader;
            public Color SpecColor;
            public float Shininess;
            public bool HasSpecColor, HasShininess;
        }

        private readonly Dictionary<Material, Original> changed = new Dictionary<Material, Original>();
        private GameScenes appliedIn = GameScenes.LOADING;
        private readonly Dictionary<string, Shader> specularVariants = new Dictionary<string, Shader>();
        private const float MeanGloss = 0.5f;

        // The editors' halls, once a scene; the rest stays as it is.
        internal void Apply(IEnumerable<MeshFilter> hall)
        {
            if (!HighLogic.LoadedSceneIsEditor || appliedIn == HighLogic.LoadedScene) return;
            appliedIn = HighLogic.LoadedScene;
            int matt = 0, shiny = 0;
            var materials = new List<Material>();
            foreach (MeshFilter filter in hall)
            {
                MeshRenderer renderer = filter != null ? filter.GetComponent<MeshRenderer>() : null;
                if (renderer == null) continue;
                renderer.GetSharedMaterials(materials);
                foreach (Material material in materials)
                {
                    if (material == null || material.shader == null || changed.ContainsKey(material)) continue;
                    RtMaterialIntent intent = RtMaterialIntent.Of(material, true);
                    if (intent.Mode == RtNative.SurfaceDry)
                    {
                        if (Matt(material, intent)) matt++;
                    }
                    else if (intent.Mode == RtNative.SurfaceLegacy && material.HasProperty("_SpecColor") && material.HasProperty("_Shininess"))
                    {
                        Remember(material, intent);
                        Highlight(material, intent);
                        shiny++;
                    }
                }
            }
            Debug.Log("[ReDefinition] Raytracing: the editor's hall as its materials mean their shine: " + matt + " materials matt, "
                      + shiny + " with KSP's highlight.");
        }

        // Back as they were.
        internal void Restore()
        {
            foreach (KeyValuePair<Material, Original> entry in changed)
            {
                Material material = entry.Key;
                RtMaterialIntent.Release(material);
                if (material == null) continue;
                if (material.shader != entry.Value.Shader) material.shader = entry.Value.Shader;
                if (entry.Value.HasSpecColor) material.SetColor("_SpecColor", entry.Value.SpecColor);
                if (entry.Value.HasShininess) material.SetFloat("_Shininess", entry.Value.Shininess);
            }
            changed.Clear();
            appliedIn = GameScenes.LOADING;
        }

        // The values as they were, and the shine they mean, which the rays keep taking
        // (RtMaterialIntent.Hold).
        private void Remember(Material material, RtMaterialIntent intent)
        {
            RtMaterialIntent.Hold(material, intent);
            changed[material] = new Original
            {
                Shader = material.shader,
                HasSpecColor = material.HasProperty("_SpecColor"),
                SpecColor = material.HasProperty("_SpecColor") ? material.GetColor("_SpecColor") : Color.black,
                HasShininess = material.HasProperty("_Shininess"),
                Shininess = material.HasProperty("_Shininess") ? material.GetFloat("_Shininess") : 0f,
            };
        }

        private bool Matt(Material material, RtMaterialIntent intent)
        {
            string name = material.shader.name;
            Shader target = material.shader;
            if (name.IndexOf("Specular") < 0)
            {
                // KSP's diffuse shaders take Deferred's fixed shine: the specular variant of the
                // same shader instead, as Deferred replaced it (taken from a material using it).
                string variant = name.Replace("Bumped", "Bumped Specular").Replace("/Diffuse", "/Specular");
                if (variant == name || !specularVariants.TryGetValue(variant, out target))
                {
                    target = null;
                    foreach (Material other in Resources.FindObjectsOfTypeAll<Material>())
                        if (other != null && other.shader != null && other.shader.name == variant)
                        {
                            target = other.shader;
                            break;
                        }
                    specularVariants[variant] = target;
                }
                if (target == null) return false;
            }
            Remember(material, intent);
            material.shader = target;
            if (material.HasProperty("_SpecColor")) material.SetColor("_SpecColor", Color.black);
            if (material.HasProperty("_Shininess")) material.SetFloat("_Shininess", 0.03f);
            return true;
        }

        // Deferred: specular = c / pi, smoothness = s^0.215 gloss sqrt(|c|); meant: a specular
        // colour of the intent's at the mean gloss and its smoothness at the mean gloss.
        private static void Highlight(Material material, RtMaterialIntent intent)
        {
            Color c = intent.Specular * (MeanGloss * Mathf.PI);
            c.r = Mathf.Clamp01(c.r);
            c.g = Mathf.Clamp01(c.g);
            c.b = Mathf.Clamp01(c.b);
            float length = Mathf.Sqrt(c.r * c.r + c.g * c.g + c.b * c.b);
            float wanted = intent.Smoothness / (MeanGloss * Mathf.Sqrt(Mathf.Max(length, 1e-4f)));
            material.SetColor("_SpecColor", new Color(c.r, c.g, c.b, 1f));
            material.SetFloat("_Shininess", Mathf.Clamp01(Mathf.Pow(Mathf.Clamp01(wanted), 1f / 0.215f)));
        }
    }
}
