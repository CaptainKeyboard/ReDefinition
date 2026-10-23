using System.Collections;
using System.Reflection;
using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Settings.Behaviours
{
    // Parallax's texture blending and ambient occlusion are shader keywords of
    // the six terrain materials each body makes once as the game loads
    // (ParallaxTerrainBody.LoadInitial) and every quad shares. Its window's
    // change sets them through the quads loaded now (ToolbarMenu
    // .UpdateTerrainKeywords, over PQSMod_Parallax.terrainQuadData), and a quad
    // set up later only switches them on (TerrainShaderQuadData.Initialize): a
    // material no loaded quad used kept a keyword switched off. Here every body's
    // six materials take it as well, besides the registration's `after`. The
    // planets seen from afar take it as Parallax sets them up, at the next start
    // (ConfigLoader.DetermineScaledKeywordsFromTerrain). Without the members the
    // setting goes as the registration has it, through the loaded quads.
    internal sealed class ParallaxKeywordsBehaviour : ModBehaviour
    {
        private static readonly string[] MaterialFields =
        {
            "parallaxLow", "parallaxMid", "parallaxHigh", "parallaxLowMid", "parallaxMidHigh", "parallaxFull",
        };

        public override void Finish(RegisteredMod mod, BundledSetting setting, SettingRegistration registration)
        {
            string keyword = registration.Name == "advancedTextureBlending" ? "ADVANCED_BLENDING"
                : registration.Name == "ambientOcclusion" ? "AMBIENT_OCCLUSION"
                : null;
            if (keyword == null) return;
            Action<string> write = setting.Write;
            setting.Write = text =>
            {
                write(text);
                bool on;
                if (bool.TryParse(text, out on)) SetEverywhere(keyword, on);
            };
        }

        private static void SetEverywhere(string keyword, bool on)
        {
            try
            {
                Type loader = TypeLookup.Find("Parallax.ConfigLoader");
                FieldInfo bodiesField = loader != null ? loader.GetField("parallaxTerrainBodies", TypeLookup.Any) : null;
                IDictionary bodies = bodiesField != null && bodiesField.IsStatic ? bodiesField.GetValue(null) as IDictionary : null;
                if (bodies == null) return;
                foreach (object body in bodies.Values)
                {
                    FieldInfo materialsField = body != null ? body.GetType().GetField("parallaxMaterials", TypeLookup.Any) : null;
                    object materials = materialsField != null ? materialsField.GetValue(body) : null;
                    if (materials == null) continue;
                    foreach (string name in MaterialFields)
                    {
                        FieldInfo field = materials.GetType().GetField(name, TypeLookup.Any);
                        Material material = field != null ? field.GetValue(materials) as Material : null;
                        if (material == null) continue;
                        if (on) material.EnableKeyword(keyword);
                        else material.DisableKeyword(keyword);
                    }
                }
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("parallax-keywords-" + keyword, "Parallax's terrain materials could not take "
                                                                      + keyword + " (" + CompatibilityLog.Reason(e)
                                                                      + "); the terrain loaded now has it.");
            }
        }
    }
}
