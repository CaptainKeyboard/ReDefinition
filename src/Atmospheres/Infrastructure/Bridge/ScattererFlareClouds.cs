using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Scatterer's sun flare dims by the clouds in front of the sun through one global texture: its shader (Scatterer/sunFlare, the vertex stage)
    // multiplies the flare's colour by the alpha of scattererReconstructedCloud read at the sun's screen position; SunFlare.start sets it to white
    // once (Scatterer 0.908; the shader read from its OpenGL bundle, PRUEFPROTOKOLL 275).
    [AtmospheresAssumptions]
    internal static class AssumptionsFlare
    {
        public static readonly ForeignAssumption FlareClouds =
            new ForeignAssumption("atmo-scatterer-flare-clouds", "Scatterer", "the sun flare dims behind the own clouds")
                .Type("Scatterer.SunFlare").Type("Scatterer.ShaderProperties")
                .Literal("Scatterer.ShaderProperties", "scattererReconstructedCloud")
                .TestedVersions("0.908.0.0")
                .OnChange(WhenChanged.Fallback, "the sun flare shows through the own clouds");
    }

    // Scatterer's sun flare behind the own clouds (master plan 46.1; owner 2026-11-25: under a closed deck the sun must not shine through,
    // only the diffused light comes): Scatterer's flare shader multiplies the flare by the alpha of the global texture scattererReconstructedCloud
    // at the sun's screen position (AssumptionsFlare.FlareClouds), which Scatterer sets to white once. Each frame the own clouds put
    // their DIRECT transmission towards the sun at the camera there (exp(-tau), the CPU's cloud field along the sun): a 1 x 1 texture, so every
    // screen position reads it. A closed deck of tau 26 takes the flare away; a clear sky leaves it as it was. Release puts white back.
    internal static class ScattererFlareClouds
    {
        private static readonly int Id = Shader.PropertyToID("scattererReconstructedCloud");
        private static Texture2D texture;
        private static float written = -1f;
        private static bool bound;

        // The value the texture holds: the direct transmission, clamped to 0..1 (the flare's colour is multiplied by it).
        internal static float Encode(float directTransmission)
        {
            return directTransmission > 1f ? 1f : (directTransmission > 0f ? directTransmission : 0f);
        }

        // Sets the flare's cloud transmission for the coming frame. Nothing where Scatterer's flare is not the one checked.
        internal static void Apply(float directTransmission)
        {
            if (!AssumptionsFlare.FlareClouds.Holds) return;
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
            Shader.SetGlobalTexture(Id, texture);
            bound = true;
        }

        // The own clouds are off: Scatterer's white again (its flare as without the own clouds).
        internal static void Release()
        {
            if (!bound) return;
            Shader.SetGlobalTexture(Id, Texture2D.whiteTexture);
            bound = false;
        }
    }
}
