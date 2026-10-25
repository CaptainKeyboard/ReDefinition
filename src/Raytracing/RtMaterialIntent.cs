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
    // * The space centre's ground (Diffuse Ground KSC and its Specular variant): Unity's
    //   specular setup with _SpecularColor as its specular colour, black on every stock
    //   material; there its lawn shines as Parallax's terrain beside it, its tarmac not at all (Ground).
    // * Parallax's terrain: its own Blinn-Phong, _SpecularIntensity at _SpecularPower, the
    //   gloss in its colour's alpha, which its G-buffer's smoothness gives back over 0.644.
    // * Unity's physically based shaders and every other one: the G-buffer's own.
    internal struct RtMaterialIntent
    {
        // How the shine is meant (RtNative's Surface constants); the specular colour and smoothness the hits take where it is not
        // Keep; for the legacy families what the G-buffer's smoothness is divided by for the gloss, and the gloss where
        // no G-buffer gives it (the ground: whether it is drawn as Parallax's terrain already).
        public uint Mode;
        public Color Specular;
        public float Smoothness;
        public float GlossDivisor;
        public float GlossMean;

        // The power of _Shininess in the divisor that gives the gloss back from Deferred's smoothness (see above).
        private const float LegacyShininessPower = 0.215f;
        // Unity's defaults, left on most of the space centre's specular materials.
        private const float DefaultShininess = 0.078125f;
        private const float DefaultSpecular = 0.5f;
        // Parallax's G-buffer smoothness per unit of gloss (measured from its deferred pass).
        private const float ParallaxGlossDivisor = 0.644f;
        // The gloss a hit without a G-buffer takes for the legacy families.
        private const float MeanGloss = 0.5f;
        // The gloss of Parallax's terrain beside the space centre's lawn, which the lawn takes (Ground):
        // the terrain's F0 there 0.020 to 0.022 at its intensity 0.12, from six views in the evening
        // (2026-10-25, tools/surface_sheet.py).
        private const float LawnGloss = 0.175f;

        // The shaders already logged as taken as drawn, so that each is logged once.
        private static readonly HashSet<string> Unknown = new HashSet<string>();

        // Materials whose values were changed for Deferred's G-buffer (EditorHallShine),
        // with the shine their own values mean: read again from the changed ones, a
        // highlight's colour and shininess set for Deferred's conversion give another. The
        // rays take a material's again whenever its texture arrives (RtScene.Deliver), after
        // the hall was changed.
        private static readonly Dictionary<Material, RtMaterialIntent> meant = new Dictionary<Material, RtMaterialIntent>();

        // Holds the shine a material's own values mean before they are changed for Deferred (EditorHallShine).
        internal static void Hold(Material material, RtMaterialIntent intent)
        {
            if (material != null) meant[material] = intent;
        }

        // Drops the shine held for a material, as its own values are put back.
        internal static void Release(Material material)
        {
            if (!ReferenceEquals(material, null)) meant.Remove(material);
        }

        // The material's shine as meant; `scenery` for the space centre and other scenery, whose diffuse and default
        // specular shaders stay matt where a vessel's part keeps the G-buffer's.
        internal static RtMaterialIntent Of(Material material, bool scenery)
        {
            var keep = new RtMaterialIntent { Mode = RtNative.SurfaceKeep, Specular = Color.black, Smoothness = 0f };
            if (material == null || material.shader == null) return keep;
            RtMaterialIntent held;
            if (meant.TryGetValue(material, out held)) return held;
            string shader = material.shader.name;

            if (shader.StartsWith("Custom/Parallax", StringComparison.Ordinal) && material.HasProperty("_SpecularPower"))
            {
                float power = Mathf.Max(material.GetFloat("_SpecularPower"), 0f);
                float intensity = material.HasProperty("_SpecularIntensity") ? material.GetFloat("_SpecularIntensity") : 0f;
                return Legacy(intensity * Color.white, power, ParallaxGlossDivisor, RtNative.SurfaceLegacy);
            }
            // KSP's own terrain: grass, sand and rock, matt (its Lambert shaders have no shine;
            // the atlas shader's own reflection of the sky is in the frame already). Kept
            // otherwise, the forward light gave it the default shine, and the grass at a
            // glancing view mirrored the sky: 30 % brighter than the matt space centre's
            // lawn and runway beside it (measured on the pad, 2026-10-13).
            if (shader.StartsWith("Terrain/PQS/", StringComparison.Ordinal)) return Dry();
            if (shader.IndexOf("Diffuse Ground KSC", StringComparison.Ordinal) >= 0)
            {
                Color f0 = material.HasProperty("_SpecularColor") ? material.GetColor("_SpecularColor") : Color.black;
                if (Max(f0) >= 0.004f)
                    return new RtMaterialIntent { Mode = RtNative.SurfaceFixed, Specular = f0, Smoothness = 0.25f };
                return Ground(Scenery.KscGround.Draws(material));
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
                Debug.Log("[ReDefinition] Raytracing: the shine of shader '" + shader + "' is taken as drawn (Deferred's G-buffer, or"
                          + " the material's own values), at most a smoothness of 0.95 (material '" + material.name + "').");
            return keep;
        }

        // The space centre's ground (RT_SURFACE_GROUND): its lawn, told apart per pixel by
        // its drawn colour, shines as Parallax's terrain beside it does, at the terrain's
        // gloss there (LawnGloss); its tarmac and concrete not at all. Matt as a whole
        // (Dry), the lawn was darker than the terrain at a glancing view: at the launch
        // pad a dark strip where they meet (measured 2026-10-19: 66 against 100 with the
        // rays). With the G-buffer's shine (0.22 at a smoothness of 0.013, rough and
        // strong) it was lighter than the terrain towards a low sun instead (the player,
        // 2026-10-25). Without Parallax the G-buffer's, as before. `asTerrain`: drawn as
        // Parallax's terrain already (Scenery/KscGround), which the raytracing then does not
        // lift or shade to the terrain's look (RtCommon.hlsli, MeadowShade, LawnOcclusion):
        // in GlossMean, which the ground's shine does not use.
        private static RtMaterialIntent Ground(bool asTerrain)
        {
            var ground = new RtMaterialIntent
            {
                Mode = RtNative.SurfaceGround, Specular = Color.black, Smoothness = 0f, GlossMean = asTerrain ? 1f : 0f,
            };
            RtMaterialIntent terrain;
            if (!HomeTerrain(out terrain)) return ground;
            ground.Specular = terrain.Specular * LawnGloss;
            ground.Smoothness = terrain.Smoothness;
            return ground;
        }

        // Parallax's terrain of the home body as its config gives it (ParallaxTerrain, Body, ShaderProperties:
        // _SpecularIntensity at _SpecularPower), the shine the terrain beside the space centre's lawn gets;
        // false without Parallax.
        private static bool homeTerrainRead;
        private static bool homeTerrainFound;
        private static RtMaterialIntent homeTerrain;

        // The space centre's lawn shines as the terrain, and the raytracing lifts it to the terrain's albedo
        // (RtCommon.hlsli, MeadowShade): Parallax's terrain found.
        internal static bool LawnAsTerrain
        {
            get
            {
                RtMaterialIntent terrain;
                return HomeTerrain(out terrain);
            }
        }

        // The home body's Parallax terrain shine, read from the config once GameDatabase is there; false without it.
        private static bool HomeTerrain(out RtMaterialIntent terrain)
        {
            if (!homeTerrainRead && GameDatabase.Instance != null)
            {
                homeTerrainRead = true;
                CelestialBody home = FlightGlobals.GetHomeBody();
                string name = home != null ? home.bodyName : "Kerbin";
                foreach (ConfigNode root in GameDatabase.Instance.GetConfigNodes("ParallaxTerrain"))
                    foreach (ConfigNode body in root.GetNodes("Body"))
                    {
                        ConfigNode values = body.GetValue("name") == name ? body.GetNode("ShaderProperties") : null;
                        float power, intensity;
                        if (values == null || !float.TryParse(values.GetValue("_SpecularPower"), System.Globalization.NumberStyles.Float,
                                                              System.Globalization.CultureInfo.InvariantCulture, out power)
                            || !float.TryParse(values.GetValue("_SpecularIntensity"), System.Globalization.NumberStyles.Float,
                                               System.Globalization.CultureInfo.InvariantCulture, out intensity))
                            continue;
                        homeTerrain = Legacy(intensity * Color.white, power, ParallaxGlossDivisor, RtNative.SurfaceLegacy);
                        homeTerrainFound = true;
                    }
            }
            terrain = homeTerrain;
            return homeTerrainFound;
        }

        // No shine at all (RT_SURFACE_DRY).
        private static RtMaterialIntent Dry()
        {
            return new RtMaterialIntent { Mode = RtNative.SurfaceDry, Specular = Color.black, Smoothness = 0f };
        }

        // A Blinn-Phong highlight of colour `specular` at exponent `exponent` as a shine: the roughness Unity relates to
        // the exponent, the colour as bright as the highlight, the gloss read from the G-buffer by `divisor`.
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

        // Whether a material's name is one of the space centre's concrete ones (ConcreteNames), steel excepted.
        private static bool Concrete(string name)
        {
            if (name.IndexOf("Steel", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            foreach (string concrete in ConcreteNames)
                if (name.IndexOf(concrete, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // The colour's brightest channel.
        private static float Max(Color c)
        {
            return Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        }
    }
}
