using UnityEngine;

namespace ReDefinition.Raytracing
{
    // The sun's highlight a material's shader draws by itself. The forward light
    // takes it out of the frame's colour before it relights it, and puts it back as it was
    // (Raytracing.hlsl, ForwardLight). Without that the highlight would be scaled with the light
    // the raytracing replaces, brighter in a dim ambient light than in a bright one. It is held in
    // Unity's colour space as the specular colour, and in w the shape: above 0 the exponent of
    // Unity's legacy Blinn-Phong (Lighting.cginc, UnityBlinnPhongLight; its gloss in the
    // main texture's alpha). Otherwise w is minus the smoothness of Unity's standard shader
    // (UnityStandardBRDF.cginc, BRDF1_Unity_PBS). All 0 where the highlight is not known:
    // a texture's smoothness or gloss map, KSP's mapped and other shaders, which keep
    // theirs in the frame's colour as before.
    internal static class RtForwardHighlight
    {
        // unity_ColorSpaceDielectricSpec (UnityCG.cginc), gamma and linear.
        private const float DielectricGamma = 0.220916301f;
        private const float DielectricLinear = 0.04f;

        // The material's highlight as the class comment describes it, in gamma or linear colour; zero where not known.
        internal static Vector4 Of(Material material, bool gammaSpace)
        {
            if (material == null || material.shader == null) return Vector4.zero;
            string shader = material.shader.name;
            if (shader == "Standard" || shader == "Standard (Specular setup)")
            {
                if (!material.HasProperty("_Glossiness") || material.IsKeywordEnabled("_SPECULARHIGHLIGHTS_OFF")
                    || material.IsKeywordEnabled("_METALLICGLOSSMAP") || material.IsKeywordEnabled("_SPECGLOSSMAP")
                    || material.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A"))
                    return Vector4.zero;
                Color specular;
                if (shader == "Standard")
                {
                    // Metallic: the dielectric's reflectance towards the albedo's colour.
                    float metallic = material.HasProperty("_Metallic") ? Mathf.Clamp01(material.GetFloat("_Metallic")) : 0f;
                    Color albedo = material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
                    if (!gammaSpace) albedo = albedo.linear;
                    float dielectric = gammaSpace ? DielectricGamma : DielectricLinear;
                    specular = Color.Lerp(new Color(dielectric, dielectric, dielectric), albedo, metallic);
                }
                else
                {
                    if (!material.HasProperty("_SpecColor")) return Vector4.zero;
                    specular = material.GetColor("_SpecColor");
                    if (!gammaSpace) specular = specular.linear;
                }
                return new Vector4(specular.r, specular.g, specular.b, -Mathf.Clamp01(material.GetFloat("_Glossiness")));
            }
            bool legacy = shader.StartsWith("KSP/", System.StringComparison.Ordinal)
                          || shader.StartsWith("Legacy Shaders/", System.StringComparison.Ordinal);
            if (legacy && shader.IndexOf("Specular", System.StringComparison.Ordinal) >= 0
                && shader.IndexOf("(Mapped)", System.StringComparison.Ordinal) < 0
                && material.HasProperty("_SpecColor") && material.HasProperty("_Shininess"))
            {
                Color specular = material.GetColor("_SpecColor");
                if (!gammaSpace) specular = specular.linear;
                // The shader's range for _Shininess starts at 0.03.
                float exponent = 128f * Mathf.Clamp(material.GetFloat("_Shininess"), 0.03f, 1f);
                return new Vector4(Mathf.Max(specular.r, 0f), Mathf.Max(specular.g, 0f), Mathf.Max(specular.b, 0f), exponent);
            }
            return Vector4.zero;
        }
    }
}
