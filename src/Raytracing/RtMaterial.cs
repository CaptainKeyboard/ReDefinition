using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // What a ray that hits a surface takes of its material: albedo, specular colour,
    // smoothness and emission, from the material's properties.
    //
    // KSP's part shaders are legacy Blinn-Phong ones (_Shininess, _SpecColor). The
    // specular colour is taken over pi as Deferred takes it for its G-buffer (LegacyToStandard.cginc,
    // Deferred 1.3.5, GPL 3.0). The smoothness is taken as KSP's own shaders give the highlight:
    // Blinn-Phong's exponent 128 times the shininess, Beckmann's m = sqrt(2 / (n + 2)).
    // That is how the raytracing takes the G-buffer's smoothness (RtCommon.hlsli, LegacySmoothness), not
    // Deferred's look, which turns many parts into mirrors.
    //
    // Unity's standard shaders give the diffuse light less than their albedo, what the
    // specular colour takes of it (UnityStandardUtils.cginc, Unity's built-in shaders
    // 2019.4). The specular setup gives its albedo times 1 - the specular colour's largest
    // channel. The metallic setup gives its albedo times 1 - reflectivity, whose dielectric specular
    // colour is the colour space's (unity_ColorSpaceDielectricSpec). The rays take the
    // same, so that the light they bring matches what those shaders add.
    internal struct RtMaterial
    {
        // The diffuse colour after what the specular takes of it, the specular colour, the smoothness the rays
        // reflect with, and the light the surface gives off.
        public Color Albedo;
        public Color Specular;
        public float Smoothness;
        public Color Emission;
        // The alpha below which the material's texture cuts the surface out, 0 for none.
        public float Cutoff;

        // The material as a ray takes it; white, a dielectric's specular, smoothness 0.2 where it says nothing.
        internal static RtMaterial Of(Material material)
        {
            var result = new RtMaterial
            {
                Albedo = Color.white,
                Specular = new Color(0.04f, 0.04f, 0.04f),
                Smoothness = 0.2f,
                Emission = Color.black,
            };
            if (material == null) return result;

            // Without _Color, as Parallax's terrain: white, the texture alone (MainTexture).
            if (material.HasProperty("_Color")) result.Albedo = material.GetColor("_Color");
            result.Albedo.a = 1f;

            if (material.HasProperty("_Glossiness"))
            {
                // Unity's standard shaders: the specular setup, or the metallic setup, where
                // a metal takes its specular colour from its albedo.
                result.Smoothness = material.GetFloat("_Glossiness");
                if (material.HasProperty("_SpecColor"))
                {
                    result.Specular = material.GetColor("_SpecColor");
                    result.Albedo *= 1f - Mathf.Max(result.Specular.r, Mathf.Max(result.Specular.g, result.Specular.b));
                    result.Albedo.a = 1f;
                }
                else if (material.HasProperty("_Metallic"))
                    Metallic(ref result, material.GetFloat("_Metallic"));
            }
            else if (material.HasProperty("_Shininess"))
            {
                Color legacy = material.HasProperty("_SpecColor") ? material.GetColor("_SpecColor") : new Color(0.5f, 0.5f, 0.5f);
                legacy.r = Mathf.Clamp01(legacy.r);
                legacy.g = Mathf.Clamp01(legacy.g);
                legacy.b = Mathf.Clamp01(legacy.b);
                float exponent = 128f * Mathf.Clamp01(material.GetFloat("_Shininess"));
                result.Smoothness = 1f - Mathf.Sqrt(Mathf.Sqrt(2f / (exponent + 2f)));
                result.Specular = new Color(legacy.r / Mathf.PI, legacy.g / Mathf.PI, legacy.b / Mathf.PI);
            }
            else if (material.HasProperty("_Metallic"))
                Metallic(ref result, material.GetFloat("_Metallic"));

            // Cut out: Unity's and KSP's cut-out shaders, at the alpha-test queue or with the
            // standard shaders' keyword, and Parallax's scatter, whose grass sits at the
            // opaque queue with its own keyword (InstancedSolid.shader, ALPHA_CUTOFF).
            if (material.HasProperty("_Cutoff")
                && (material.IsKeywordEnabled("_ALPHATEST_ON") || material.IsKeywordEnabled("ALPHA_CUTOFF")
                    || (material.renderQueue >= 2450 && material.renderQueue < 2501)))
                result.Cutoff = Mathf.Clamp(material.GetFloat("_Cutoff"), 0.01f, 1f);

            // KSP's emissive parts (_EmissiveColor, its alpha a strength), Unity's _EmissionColor.
            if (material.HasProperty("_EmissiveColor"))
            {
                Color e = material.GetColor("_EmissiveColor");
                result.Emission = new Color(e.r * e.a, e.g * e.a, e.b * e.a);
            }
            else if (material.HasProperty("_EmissionColor") && material.IsKeywordEnabled("_EMISSION"))
                result.Emission = material.GetColor("_EmissionColor");
            return result;
        }

        // The main texture of Unity's and KSP's shaders.
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        // Parallax's terrain shaders (Parallax.shader, KSCTerrain.shader, Parallax
        // Continued) have no _Color or _MainTex but a texture per altitude band. The
        // material's keyword says which are used (PARALLAX_SINGLE_LOW to
        // PARALLAX_FULL, TerrainShaderQuadData.cs). The KSC's has the low one only. The
        // lowest band used stands for the ground.
        private static readonly int LowTexId = Shader.PropertyToID("_MainTexLow");
        private static readonly int MidTexId = Shader.PropertyToID("_MainTexMid");
        private static readonly int HighTexId = Shader.PropertyToID("_MainTexHigh");

        // Where a shader keeps its main texture, as MainTexture tells them apart: none, _MainTex, _MainTexLow
        // alone, or all three bands.
        private enum TextureKind { None, Main, LowOnly, Bands }

        // Per shader, where its main texture is: HasProperty is a native call each.
        private static readonly Dictionary<Shader, TextureKind> textureKinds = new Dictionary<Shader, TextureKind>();

        // The texture the rays take for a material's colour. A world-mapped one -- the
        // terrain shaders tile theirs over world positions (biplanar), not the mesh's UVs
        // -- is taken as its average colour (worldMapped).
        internal static Texture MainTexture(Material material, out bool worldMapped)
        {
            worldMapped = false;
            Shader shader = material.shader;
            if (shader == null) return null;
            TextureKind kind;
            if (!textureKinds.TryGetValue(shader, out kind))
            {
                kind = material.HasProperty(MainTexId) ? TextureKind.Main
                       : !material.HasProperty(LowTexId) ? TextureKind.None
                       : material.HasProperty(MidTexId) && material.HasProperty(HighTexId) ? TextureKind.Bands
                       : TextureKind.LowOnly;
                textureKinds[shader] = kind;
            }
            if (kind == TextureKind.None) return null;
            // Parallax's terrain with its influence map: its colour comes from
            // RtTerrainColour, without a texture.
            if (kind != TextureKind.Main && RtTerrainColour.Applies(material)) return null;
            if (kind == TextureKind.Main) return material.GetTexture(MainTexId);
            worldMapped = true;
            if (kind == TextureKind.Bands)
            {
                if (material.IsKeywordEnabled("PARALLAX_SINGLE_MID") || material.IsKeywordEnabled("PARALLAX_DOUBLE_MIDHIGH"))
                    return material.GetTexture(MidTexId);
                if (material.IsKeywordEnabled("PARALLAX_SINGLE_HIGH")) return material.GetTexture(HighTexId);
            }
            return material.GetTexture(LowTexId);
        }

        // Unity's metallic setup: the specular from the dielectric's towards the albedo, the albedo times what the
        // specular leaves (1 - reflectivity).
        private static void Metallic(ref RtMaterial result, float metallic)
        {
            float dielectric = QualitySettings.activeColorSpace == ColorSpace.Linear ? 0.04f : 0.220916301f;
            result.Specular = Color.Lerp(new Color(dielectric, dielectric, dielectric), result.Albedo, metallic);
            result.Specular.a = 1f;
            result.Albedo *= (1f - dielectric) * (1f - metallic);
            result.Albedo.a = 1f;
        }
    }
}
