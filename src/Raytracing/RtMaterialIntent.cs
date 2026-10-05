using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // How a material's shine is meant, by its shader's family and its own values, for the
    // surfaces the raytracing writes back into Unity's G-buffer (RtCommon.hlsli,
    // SurfaceIntent). Deferred gives every KSP shader a shine by one formula, diffuse-only
    // ones and the space centre's ground included; what each shader draws without Deferred
    // was read from KSP's compiled shaders and Deferred's replacements (2026-10-05):
    //
    // * Diffuse-only shaders (KSP/Diffuse, Bumped, the scenery's Diffuse and Bumped, the
    //   ground without specular, cut-outs, lightmapped and flag shaders, Unity's Lambert):
    //   no shine on the scenery; a vessel's parts keep Deferred's.
    // * KSP's legacy specular shaders: Blinn-Phong, _SpecColor times the gloss in the main
    //   texture's alpha times (N.H)^(128 _Shininess). Its roughness from the exponent n as
    //   Unity relates them (UnityStandardBRDF.cginc: n = 2 / m^2 - 2), its specular colour as
    //   bright as the highlight: 8 c / (n + 2) times the gloss, which Deferred's smoothness
    //   gives back divided by _Shininess^0.215 sqrt(|c|) (LegacyToStandard.cginc). Unity's
    //   own legacy specular shaders write c gloss / pi as their specular colour instead.
    //   The space centre's buildings with Unity's untouched defaults (_Shininess 0.078,
    //   _SpecColor 0.5) as matt (the player, 2026-10-05: buildings matt rather than shiny),
    //   and its concrete by its materials' names (Concrete).
    // * The space centre's ground (Diffuse Ground KSC Specular): Unity's specular setup with
    //   _SpecularColor as its specular colour, black on every stock material.
    // * Parallax's terrain: its own Blinn-Phong, _SpecularIntensity at _SpecularPower, the
    //   gloss in its colour's alpha, which its G-buffer's smoothness gives back over 0.644.
    // * Unity's physically based shaders and every other one: the G-buffer's own.
    internal struct RtMaterialIntent
    {
        public uint Mode;
        public Color Specular;
        public float Smoothness;
        public float GlossDivisor;
        public float GlossMean;

        private const float LegacyShininessPower = 0.215f;
        // Unity's defaults, left on most of the space centre's specular materials.
        private const float DefaultShininess = 0.078125f;
        private const float DefaultSpecular = 0.5f;
        // Parallax's G-buffer smoothness per unit of gloss (measured from its deferred pass).
        private const float ParallaxGlossDivisor = 0.644f;
        // The gloss a hit without a G-buffer takes for the legacy families.
        private const float MeanGloss = 0.5f;

        private static readonly HashSet<string> Unknown = new HashSet<string>();

        internal static RtMaterialIntent Of(Material material, bool scenery)
        {
            var keep = new RtMaterialIntent { Mode = RtNative.SurfaceKeep, Specular = Color.black, Smoothness = 0f };
            if (material == null || material.shader == null) return keep;
            string shader = material.shader.name;

            if (shader.StartsWith("Custom/Parallax", StringComparison.Ordinal) && material.HasProperty("_SpecularPower"))
            {
                float power = Mathf.Max(material.GetFloat("_SpecularPower"), 0f);
                float intensity = material.HasProperty("_SpecularIntensity") ? material.GetFloat("_SpecularIntensity") : 0f;
                return Legacy(intensity * Color.white, power, ParallaxGlossDivisor, RtNative.SurfaceLegacy);
            }
            if (shader.IndexOf("Diffuse Ground KSC Specular", StringComparison.Ordinal) >= 0)
            {
                Color f0 = material.HasProperty("_SpecularColor") ? material.GetColor("_SpecularColor") : Color.black;
                if (Max(f0) < 0.004f) return Dry();
                return new RtMaterialIntent { Mode = RtNative.SurfaceFixed, Specular = f0, Smoothness = 0.25f };
            }
            bool kspLegacy = shader.StartsWith("KSP/", StringComparison.Ordinal);
            bool unityLegacy = shader.StartsWith("Legacy Shaders/", StringComparison.Ordinal);
            if ((kspLegacy || unityLegacy) && shader.IndexOf("Specular", StringComparison.Ordinal) >= 0
                && shader.IndexOf("(Mapped)", StringComparison.Ordinal) < 0)
            {
                if (!material.HasProperty("_Shininess") || !material.HasProperty("_SpecColor")) return Dry();
                Color c = material.GetColor("_SpecColor");
                c.r = Mathf.Clamp01(c.r);
                c.g = Mathf.Clamp01(c.g);
                c.b = Mathf.Clamp01(c.b);
                float shininess = Mathf.Clamp01(material.GetFloat("_Shininess"));
                if (Max(c) < 0.004f) return Dry();
                if (scenery && Mathf.Abs(shininess - DefaultShininess) < 0.003f && Mathf.Abs(Max(c) - DefaultSpecular) < 0.01f)
                    return Dry();
                if (scenery && Concrete(material.name)) return Dry();
                float exponent = 128f * shininess;
                if (unityLegacy)
                    return Legacy(c, exponent, Max(c) / Mathf.PI, RtNative.SurfaceUnityLegacy);
                float divisor = Mathf.Pow(Mathf.Max(shininess, 1e-4f), LegacyShininessPower)
                                * Mathf.Sqrt(Mathf.Sqrt(c.r * c.r + c.g * c.g + c.b * c.b));
                return Legacy(c, exponent, divisor, RtNative.SurfaceLegacy);
            }
            if (kspLegacy || unityLegacy || shader == "Mobile/Diffuse")
            {
                // KSP's and Unity's diffuse, bumped, cut-out, lightmapped, decal and flag shaders.
                if (shader.IndexOf("Diffuse", StringComparison.Ordinal) >= 0 || shader.IndexOf("Bumped", StringComparison.Ordinal) >= 0
                    || shader.IndexOf("Cutoff", StringComparison.Ordinal) >= 0 || shader.IndexOf("Lightmapper", StringComparison.Ordinal) >= 0
                    || shader.IndexOf("Decal", StringComparison.Ordinal) >= 0 || shader.IndexOf("Flag", StringComparison.Ordinal) >= 0
                    || shader.IndexOf("Emissive", StringComparison.Ordinal) >= 0)
                    // A vessel's part with no highlight of KSP's (the Mk1-3 pod, KSP/Emissive/
                    // Diffuse) keeps the shine Deferred gives it (the player, 2026-10-05: the
                    // pod looks like a shiny material); the scenery's stays matt.
                    return scenery ? Dry() : keep;
            }
            if (!material.HasProperty("_Glossiness") && !material.HasProperty("_Metallic") && Unknown.Add(shader))
                Debug.Log("[ReDefinition] Raytracing: the shine of shader '" + shader + "' is taken from Deferred's G-buffer, at most "
                          + "a smoothness of 0.95 (material '" + material.name + "').");
            return keep;
        }

        private static RtMaterialIntent Dry()
        {
            return new RtMaterialIntent { Mode = RtNative.SurfaceDry, Specular = Color.black, Smoothness = 0f };
        }

        private static RtMaterialIntent Legacy(Color specular, float exponent, float divisor, uint mode)
        {
            float n = Mathf.Max(exponent, 0f);
            float alpha = Mathf.Sqrt(2f / (n + 2f));
            float brightness = Mathf.Min(1f, 8f / (n + 2f));
            return new RtMaterialIntent
            {
                Mode = mode,
                Specular = new Color(specular.r * brightness, specular.g * brightness, specular.b * brightness),
                Smoothness = 1f - Mathf.Sqrt(alpha),
                GlossDivisor = Mathf.Max(divisor, 1e-4f),
                GlossMean = MeanGloss,
            };
        }

        // The space centre's concrete, which KSP gives a highlight (the launch pad's
        // kap_pad_launchPad_mat, _SpecColor 0.28, _Shininess 0.45; its ramps, floors and
        // foundations; the shader inventory, 2026-10-05): matt (the player: the pad must not
        // shine). Its metal -- tanks, the water tower, pipes, steel beams -- keeps its shine.
        private static readonly string[] ConcreteNames = { "launchPad", "ramp", "floor", "Foundation", "runway", "concrete" };

        private static bool Concrete(string name)
        {
            if (name.IndexOf("Steel", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            foreach (string concrete in ConcreteNames)
                if (name.IndexOf(concrete, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static float Max(Color c)
        {
            return Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        }
    }
}
