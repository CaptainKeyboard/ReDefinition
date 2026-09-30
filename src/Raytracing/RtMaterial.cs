using UnityEngine;

namespace ReDefinition.Raytracing
{
    // What a ray that hits a surface takes of its material: albedo, specular colour,
    // smoothness and emission, from the material's properties.
    //
    // KSP's part shaders are legacy Blinn-Phong ones (_Shininess, _SpecColor). Deferred
    // turns them into Unity's standard specular set for its G-buffer
    // (LegacyToStandard.cginc, GetStandardSpecularPropertiesFromLegacy, Deferred 1.3.5,
    // GPL 3.0), and the rays take them the same way so that a hit looks as the part
    // does on screen: smoothness = shininess ^ 0.215 * sqrt(|specular colour|),
    // specular = specular colour / pi. The texture's alpha, Deferred's specular map,
    // is taken as 1: the rays see one value per material.
    //
    // Unity's standard shaders give the diffuse light less than their albedo, what the
    // specular colour takes of it (UnityStandardUtils.cginc, Unity's built-in shaders
    // 2019.4): the specular setup's albedo times 1 - the specular colour's largest
    // channel, the metallic setup's times 1 - reflectivity, whose dielectric specular
    // colour is the colour space's (unity_ColorSpaceDielectricSpec). The rays take the
    // same, so that the light they bring matches what those shaders add.
    internal struct RtMaterial
    {
        public Color Albedo;
        public Color Specular;
        public float Smoothness;
        public Color Emission;

        private const float ShininessPower = 0.215f;

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
                float length = Mathf.Sqrt(legacy.r * legacy.r + legacy.g * legacy.g + legacy.b * legacy.b);
                result.Smoothness = Mathf.Pow(Mathf.Clamp01(material.GetFloat("_Shininess")), ShininessPower) * Mathf.Sqrt(length);
                result.Specular = new Color(legacy.r / Mathf.PI, legacy.g / Mathf.PI, legacy.b / Mathf.PI);
            }
            else if (material.HasProperty("_Metallic"))
                Metallic(ref result, material.GetFloat("_Metallic"));

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
