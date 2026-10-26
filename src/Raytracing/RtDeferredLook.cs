using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // What of a surface's colour stays diffuse where Deferred shades it, for the forward
    // light without Deferred (Raytracing.hlsl, ForwardLight): the same light and colour
    // over the frame as with Deferred. Deferred draws KSP's shaders with Unity's standard
    // shading, which takes the specular colour off the diffuse one (the energy
    // conservation of UnityStandardUtils.cginc). Its replacements are read from Deferred 1.3.5,
    // shaders/.../Replaced shaders. Other shaders Deferred leaves as they are. The sets below
    // name the shaders each setup covers.
    //
    // Measured on the launch pad with raytracing on, a stock copy against the same with
    // Deferred (2026-10-13). Without this share the lawn came out 1.3 times and the rocket
    // 1.2 times as bright as with Deferred, Deferred's 0.77 and 0.83 of them. With it,
    // within 4 of 255 in each channel (lawn 46,58,42 against 48,61,42).
    internal static class RtDeferredLook
    {
        // The terrain's (Terrain/PQS/*) and the space centre's ground (Diffuse Ground KSC, Diffuse Multiply)
        // shaders: Deferred shades them in the metallic setup at metallic 0, a dielectric's specular.
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

        // KSP's part and scenery shaders with a highlight (SPECULAR_ON): Deferred's specular setup
        // (LegacyToStandard.cginc) takes their _SpecColor / pi.
        private static readonly HashSet<string> WithHighlight = new HashSet<string>
        {
            "KSP/Alpha/Translucent Specular", "KSP/Bumped Specular (Cutoff)", "KSP/Bumped Specular Opaque (Cutoff)",
            "KSP/Bumped Specular", "KSP/Emissive/Bumped Specular", "KSP/Emissive/Specular", "KSP/InternalSpace",
            "KSP/Lightwrapped/Specular", "KSP/Scenery/Bumped Specular", "KSP/Scenery/Emissive/Bumped Specular",
            "KSP/Scenery/Emissive/Specular", "KSP/Scenery/Specular Normal", "KSP/Scenery/Specular", "KSP/Specular (Cutoff)",
            "KSP/Specular",
        };

        // KSP's diffuse shaders: Deferred's specular setup (LegacyToStandard.cginc) gives them a specular of 0.5 / pi.
        private static readonly HashSet<string> Diffuse = new HashSet<string>
        {
            "KSP/Alpha/Cutoff", "KSP/Alpha/Translucent", "KSP/Bumped", "KSP/Diffuse", "KSP/Emissive/Diffuse",
            "KSP/Scenery/Bumped", "KSP/Scenery/Diffuse",
        };

        // KSP's shaders with a specular map: the specular from the map's mean colour.
        private static readonly HashSet<string> Mapped = new HashSet<string>
        {
            "KSP/Bumped Specular (Mapped)", "KSP/Emissive/Bumped Specular (Mapped)",
        };

        // unity_ColorSpaceDielectricSpec in the active colour space: 0.2209 in gamma space, 0.04 linear.
        private static float Dielectric
        {
            get { return QualitySettings.activeColorSpace == ColorSpace.Gamma ? 0.220916301f : 0.04f; }
        }

        // The share, 1 where Deferred would not change the shader. The terrain and the space centre's ground take the
        // same share, so that they keep the balance KSP's own shaders draw them in. Deferred's colour formulas for
        // them, and its tarmac colour, would leave lawn, terrain and runway in light and dark bands.
        internal static float DiffuseShare(Material material)
        {
            if (material == null || material.shader == null) return 1f;
            string name = material.shader.name;
            // The space centre's ground and the terrain beside it alike, so that the two keep
            // KSP's own balance.
            if (Metallic.Contains(name)) return 1f - Dielectric;
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
