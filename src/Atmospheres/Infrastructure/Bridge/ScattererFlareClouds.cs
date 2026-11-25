using System;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Scatterer's sun flare dims by the clouds in front of the sun through the texture scattererReconstructedCloud: its shader (Scatterer/sunFlare, the
    // vertex stage) multiplies the flare's colour by that texture's alpha at the sun's screen position (Scatterer 0.908; the shader read from its OpenGL
    // bundle, PRUEFPROTOKOLL 275). Scatterer sets the GLOBAL texture to white once; its TAA (with TAA_UseVolumetricCloudsMotionVectors on its near camera)
    // and EVE's composite (EveCloudRelight) read that global too, so the own clouds set their value on the flare's MATERIAL (SunFlare.sunglareMaterial),
    // which takes precedence over the global for that material alone (PRUEFPROTOKOLL 284/285).
    [AtmospheresAssumptions]
    internal static class AssumptionsFlare
    {
        public static readonly ForeignAssumption FlareClouds =
            new ForeignAssumption("atmo-scatterer-flare-clouds", "Scatterer", "the sun flare dims behind the own clouds")
                .Type("Scatterer.SunFlare").Type("Scatterer.ShaderProperties")
                .Field("Scatterer.SunFlare", "sunglareMaterial", "UnityEngine.Material")
                .Literal("Scatterer.ShaderProperties", "scattererReconstructedCloud")
                .TestedVersions("0.908.0.0")
                .OnChange(WhenChanged.Fallback, "the sun flare shows through the own clouds");
    }

    // Scatterer's sun flare behind the own clouds (master plan 46.1; owner 2026-11-25: under a closed deck the sun must not shine through, only the
    // diffused light comes). Each frame the own clouds put their DIRECT transmission towards the sun at the camera (exp(-tau), the CPU's cloud field
    // along the sun) into a 1 x 1 texture on every flare's material: the flare's shader reads it at the sun's position, and a 1 x 1 texture gives that
    // value at every position. A closed deck of tau 26 takes the flare away; a clear sky leaves it. Release hands the materials the global texture
    // again (Scatterer's white, or what another cloud mod bound there).
    internal static class ScattererFlareClouds
    {
        private const float RescanSeconds = 10f;
        private static readonly int Id = Shader.PropertyToID("scattererReconstructedCloud");
        private static Texture2D texture;
        private static float written = -1f;
        private static Type flareType;
        private static FieldInfo materialField;
        private static UnityEngine.Object[] flares = new UnityEngine.Object[0];
        private static float rescanAt = -1f;
        private static bool bound, failed;

        // The value the texture holds: the direct transmission, clamped to 0..1 (the flare's colour is multiplied by it).
        internal static float Encode(float directTransmission)
        {
            return directTransmission > 1f ? 1f : (directTransmission > 0f ? directTransmission : 0f);
        }

        // Sets the flare's cloud transmission for the coming frame. Nothing where Scatterer's flare is not the one checked.
        internal static void Apply(float directTransmission)
        {
            if (failed || !AssumptionsFlare.FlareClouds.Holds) return;
            try
            {
                if (flareType == null)
                {
                    flareType = TypeLookup.Find("Scatterer.SunFlare");
                    materialField = flareType?.GetField("sunglareMaterial", TypeLookup.Any);
                    if (materialField == null) { failed = true; return; }
                }
                if (Time.realtimeSinceStartup >= rescanAt)
                {
                    rescanAt = Time.realtimeSinceStartup + RescanSeconds;
                    flares = UnityEngine.Object.FindObjectsOfType(flareType);
                }
                float t = Encode(directTransmission);
                if (texture == null)
                {
                    texture = new Texture2D(1, 1, TextureFormat.RGBA32, false, true) { name = "ReDefinition flare clouds", hideFlags = HideFlags.HideAndDontSave };
                    written = -1f;
                }
                // One byte step is the texture's resolution; rewrite only when the value moves by one.
                if (Mathf.Abs(t - written) >= 0.5f / 255f)
                {
                    texture.SetPixel(0, 0, new Color(1f, 1f, 1f, t));
                    texture.Apply(false);
                    written = t;
                }
                SetOnMaterials(texture);
                bound = true;
            }
            catch (Exception e)
            {
                failed = true;
                AtmospheresLog.WarnOnce("own clouds: Scatterer's sun flare behind the clouds stops (" + e.Message + ")");
            }
        }

        // The own clouds are off: the flares read the global texture's value again.
        internal static void Release()
        {
            if (!bound || failed) return;
            try { SetOnMaterials(Shader.GetGlobalTexture(Id) ?? Texture2D.whiteTexture); }
            catch (Exception) { }
            bound = false;
        }

        private static void SetOnMaterials(Texture value)
        {
            foreach (UnityEngine.Object f in flares)
            {
                if (f == null) continue;
                var m = materialField.GetValue(f) as Material;
                if (m != null) m.SetTexture(Id, value);
            }
        }
    }
}
