using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // What of a surface's colour stays diffuse where Deferred shades it, for the forward
    // light without Deferred (Raytracing.hlsl, ForwardLight): the same light and colour
    // over the frame as with Deferred. Deferred draws KSP's shaders with Unity's standard
    // shading, which takes the specular colour off the diffuse one (the energy
    // conservation of UnityStandardUtils.cginc), its replacements read (Deferred 1.3.5,
    // shaders/.../Replaced shaders):
    // - the terrain's (Terrain/PQS/*) and the space centre's ground (Diffuse Ground KSC,
    //   Diffuse Multiply): the metallic setup at metallic 0, a dielectric's specular,
    //   unity_ColorSpaceDielectricSpec (0.2209 in gamma space, 0.04 linear); the ground's
    //   tarmac besides times _TarmacColor, at most 0.89 (DiffuseKSCGround.shader), which
    //   KSP's own shader leaves out: per pixel by its blend mask (TarmacFactor);
    // - KSP's part and scenery shaders: the specular setup, LegacyToStandard.cginc's
    //   specular: _SpecColor / pi where the shader has KSP's highlight (SPECULAR_ON), 0.5 / pi
    //   for the diffuse ones, the specular map for the mapped ones.
    // KSP's own terrain shaders with _specularColor shade by the specular setup themselves,
    // so the frame has its share already. Other shaders Deferred leaves as they are.
    // Measured on the launch pad against Deferred (2026-10-13): the lawn 0.77 times as
    // bright, the rocket 0.83.
    internal static class RtDeferredLook
    {
        private static readonly HashSet<string> Metallic = new HashSet<string>
        {
            "KSP/Scenery/Diffuse Ground KSC", "KSP/Scenery/Diffuse Ground KSC Specular", "KSP/Scenery/Diffuse Multiply",
            "Terrain/PQS/PQS Main - Extras", "Terrain/PQS/PQS Main - Optimised With Fast Blend", "Terrain/PQS/PQS Main - Optimised",
            "Terrain/PQS/PQS Main Shader - Fast Blend", "Terrain/PQS/PQS Main Shader - Triplanar Zoom Rotation",
            "Terrain/PQS/PQS Main Shader", "Terrain/PQS/PQS Triplanar Zoom Rotation Texture Array - 1 Blend",
            "Terrain/PQS/PQS Triplanar Zoom Rotation Texture Array - 2 Blend",
            "Terrain/PQS/PQS Triplanar Zoom Rotation Texture Array - 3 Blend",
            "Terrain/PQS/PQS Triplanar Zoom Rotation Texture Array - 4 Blend", "Terrain/PQS/PQS Triplanar Zoom Rotation",
            "Terrain/PQS/Sphere Projection SURFACE QUAD (AP) ", "Terrain/PQS/Sphere Projection SURFACE QUAD",
        };

        private static readonly HashSet<string> WithHighlight = new HashSet<string>
        {
            "KSP/Alpha/Translucent Specular", "KSP/Bumped Specular (Cutoff)", "KSP/Bumped Specular Opaque (Cutoff)",
            "KSP/Bumped Specular", "KSP/Emissive/Bumped Specular", "KSP/Emissive/Specular", "KSP/InternalSpace",
            "KSP/Lightwrapped/Specular", "KSP/Scenery/Bumped Specular", "KSP/Scenery/Emissive/Bumped Specular",
            "KSP/Scenery/Emissive/Specular", "KSP/Scenery/Specular Normal", "KSP/Scenery/Specular", "KSP/Specular (Cutoff)",
            "KSP/Specular",
        };

        private static readonly HashSet<string> Diffuse = new HashSet<string>
        {
            "KSP/Alpha/Cutoff", "KSP/Alpha/Translucent", "KSP/Bumped", "KSP/Diffuse", "KSP/Emissive/Diffuse",
            "KSP/Scenery/Bumped", "KSP/Scenery/Diffuse",
        };

        private static readonly HashSet<string> Mapped = new HashSet<string>
        {
            "KSP/Bumped Specular (Mapped)", "KSP/Emissive/Bumped Specular (Mapped)",
        };

        // The space centre's ground, whose share is per vertex.
        internal static bool KscGround(Material material)
        {
            return material != null && material.shader != null
                   && material.shader.name.StartsWith("KSP/Scenery/Diffuse Ground KSC", System.StringComparison.Ordinal);
        }

        // What Deferred's ground shader keeps of the space centre's tarmac: _TarmacColor, at
        // most 0.89, as one factor (ReDefinitionRtTarmac.shader applies it per pixel).
        internal static float TarmacFactor(Material material)
        {
            if (!material.HasProperty("_TarmacColor")) return 1f;
            Color c = material.GetColor("_TarmacColor");
            return (Mathf.Min(c.r, 0.89f) + Mathf.Min(c.g, 0.89f) + Mathf.Min(c.b, 0.89f)) / 3f;
        }

        // Whether Deferred draws the material by its terrain or ground shader, whose look the
        // forward light takes per pixel.
        internal static bool Ground(Material material)
        {
            return material != null && material.shader != null && Metallic.Contains(material.shader.name);
        }

        // KSP's terrain as Deferred's G-buffer has its shine (TerrainReplacementShader.cginc):
        // the metallic setup's dielectric specular, and the smoothness the textures' alpha
        // -- on the atlas the layers', otherwise the low band's times
        // sqrt(max(|_specularColor|, 0.1)), 0.1 for the shaders without that colour --,
        // here their averages. False for anything else.
        internal static bool TerrainShine(Material material, out float specular, out float smoothness)
        {
            specular = Dielectric;
            smoothness = 0f;
            if (!Ground(material) || KscGround(material) || material.shader.name == "KSP/Scenery/Diffuse Multiply") return false;
            if (material.HasProperty("_AtlasTex"))
            {
                Color[] layers = RtStockTerrain.Layers(material.GetTexture("_AtlasTex"));
                foreach (Color layer in layers) smoothness += layer.a;
                smoothness = layers.Length > 0 ? smoothness / layers.Length : 0f;
            }
            else if (!material.HasProperty("_specularColor")) smoothness = 0.1f;
            else
            {
                Color s = material.GetColor("_specularColor");
                float alpha = material.HasProperty("_lowTex") ? RtStockTerrain.Average(material.GetTexture("_lowTex")).a : 1f;
                smoothness = Mathf.Sqrt(Mathf.Max(Mathf.Clamp01(new Vector3(s.r, s.g, s.b).magnitude), 0.1f)) * alpha;
            }
            return true;
        }

        // What of the colour Deferred's metallic setup keeps.
        internal static float MetallicShare
        {
            get { return 1f - Dielectric; }
        }

        private static float Dielectric
        {
            get { return QualitySettings.activeColorSpace == ColorSpace.Gamma ? 0.220916301f : 0.04f; }
        }

        // The share, 1 where Deferred would not change the shader; the ground's tarmac
        // besides by TarmacFactor, per pixel.
        internal static float DiffuseShare(Material material)
        {
            if (material == null || material.shader == null) return 1f;
            string name = material.shader.name;
            // The ground's look per pixel instead (TerrainLook, the KSC's in Look).
            if (Metallic.Contains(name)) return 1f;
            if (WithHighlight.Contains(name))
            {
                Color s = material.HasProperty("_SpecColor") ? material.GetColor("_SpecColor") : Color.grey;
                return 1f - Mathf.Max(Mathf.Clamp01(s.r), Mathf.Max(Mathf.Clamp01(s.g), Mathf.Clamp01(s.b))) / Mathf.PI;
            }
            if (Diffuse.Contains(name)) return 1f - 0.5f / Mathf.PI;
            if (Mapped.Contains(name) && material.HasProperty("_SpecMap"))
            {
                Color s = RtStockTerrain.Average(material.GetTexture("_SpecMap"));
                return 1f - Mathf.Max(s.r, Mathf.Max(s.g, s.b));
            }
            return 1f;
        }
    }
}
