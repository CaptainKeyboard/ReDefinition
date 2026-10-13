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
    //   KSP's own shader leaves out: per vertex by its blend mask, in the colours' alpha
    //   (RtStockTerrain, KscShare);
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

        // The space centre's ground's share where its blend mask is `tarmac`.
        internal static float KscShare(Material material, float tarmac)
        {
            float tarmacColour = 1f;
            if (material.HasProperty("_TarmacColor"))
            {
                Color c = material.GetColor("_TarmacColor");
                tarmacColour = (Mathf.Min(c.r, 0.89f) + Mathf.Min(c.g, 0.89f) + Mathf.Min(c.b, 0.89f)) / 3f;
            }
            return (1f - Dielectric) * Mathf.Lerp(1f, tarmacColour, tarmac);
        }

        private static float Dielectric
        {
            get { return QualitySettings.activeColorSpace == ColorSpace.Gamma ? 0.220916301f : 0.04f; }
        }

        // The share, 1 where Deferred would not change the shader; -1 where the vertex
        // colours' alpha holds it (KscGround).
        internal static float DiffuseShare(Material material)
        {
            if (material == null || material.shader == null) return 1f;
            if (KscGround(material)) return -1f;
            string name = material.shader.name;
            if (Metallic.Contains(name))
            {
                float dielectric = Dielectric;
                float own = 0f;
                if (material.HasProperty("_specularColor"))
                {
                    Color s = material.GetColor("_specularColor");
                    own = Mathf.Max(s.r, Mathf.Max(s.g, s.b));
                }
                return (1f - dielectric) / Mathf.Max(1f - own, 0.05f);
            }
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
