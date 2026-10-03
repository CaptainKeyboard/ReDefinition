using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // The ground's colour as Parallax's terrain shaders give it, for the rays. With its
    // influence map (INFLUENCE_MAPPING, always in KSCTerrain.shader) a band's colour is
    //   lerp(vertexColour * (luminance(texture) + 0.5), texture, influence)
    // (ParallaxUtils.cginc, BIPLANAR_TEXTURE_SET): the planet's colour map, carried in
    // the vertex colours, shaded by the texture's brightness, the texture's own colour
    // only where the influence map says. Kerbin's influence averages 0.09 to 0.13, and
    // its grass texture is a bright yellow green (gamma 0.53, 0.67, 0.04): the texture
    // alone made the rays' ground far brighter and greener than the drawn one.
    //
    // Averaged over the texture, with the texture and the influence taken as
    // independent: albedo = vertexColour * (L + 0.5) * (1 - I) + T * I, with T, L and I
    // the averages of the band's texture, its luminance and its influence channel, all
    // in Unity's colour space. KSCTerrain.shader takes _GrassColor for the vertex colour.
    internal static class RtTerrainColour
    {
        private static readonly int LowTexId = Shader.PropertyToID("_MainTexLow");
        private static readonly int MidTexId = Shader.PropertyToID("_MainTexMid");
        private static readonly int HighTexId = Shader.PropertyToID("_MainTexHigh");
        private static readonly int InfluenceId = Shader.PropertyToID("_InfluenceMap");
        private static readonly int GrassColorId = Shader.PropertyToID("_GrassColor");

        private static readonly Dictionary<Texture, Color> averages = new Dictionary<Texture, Color>();

        // Whether the material is Parallax's terrain with its influence map.
        internal static bool Applies(Material material)
        {
            if (material == null || !material.HasProperty(LowTexId) || !material.HasProperty(InfluenceId)) return false;
            return material.HasProperty(GrassColorId) || material.IsKeywordEnabled("INFLUENCE_MAPPING");
        }

        // The colour in Unity's colour space and the vertex colour's factor (0 where the
        // vertex colour is the material's _GrassColor, already in the colour).
        internal static bool Of(Material material, out Color colour, out float vertexFactor)
        {
            colour = Color.white;
            vertexFactor = 0f;
            if (!Applies(material)) return false;
            int band = Band(material);
            Texture texture = material.GetTexture(band == 0 ? LowTexId : band == 1 ? MidTexId : HighTexId);
            Texture influenceMap = material.GetTexture(InfluenceId);
            if (texture == null) return false;
            Color t = Average(texture);
            float influence = 0f;
            if (influenceMap != null)
            {
                Color i = Average(influenceMap);
                influence = band == 0 ? i.r : band == 1 ? i.g : i.b;
            }
            float luminance = 0.21f * t.r + 0.72f * t.g + 0.07f * t.b;
            float factor = (luminance + 0.5f) * (1f - influence);
            Color own = t * influence;
            if (material.HasProperty(GrassColorId))
            {
                Color grass = material.GetColor(GrassColorId);
                colour = new Color(grass.r * factor + own.r, grass.g * factor + own.g, grass.b * factor + own.b, 1f);
                return true;
            }
            colour = new Color(own.r, own.g, own.b, 1f);
            vertexFactor = factor;
            return true;
        }

        // The band the material draws: its keyword (PARALLAX_SINGLE_LOW to PARALLAX_FULL,
        // TerrainShaderQuadData.cs), the lowest one used; KSCTerrain's has the low one only.
        // 0 low, 1 mid, 2 high; the influence map's red, green and blue.
        private static int Band(Material material)
        {
            if (!material.HasProperty(MidTexId)) return 0;
            if (material.IsKeywordEnabled("PARALLAX_SINGLE_MID") || material.IsKeywordEnabled("PARALLAX_DOUBLE_MIDHIGH")) return 1;
            if (material.IsKeywordEnabled("PARALLAX_SINGLE_HIGH")) return 2;
            return 0;
        }

        // A texture's average in Unity's colour space, once: rendered down to 16 by 16
        // into a linear target, which keeps the values the shaders work with, and read.
        private static Color Average(Texture texture)
        {
            Color average;
            if (averages.TryGetValue(texture, out average)) return average;
            RenderTexture small = RenderTexture.GetTemporary(16, 16, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            RenderTexture before = RenderTexture.active;
            Texture2D read = new Texture2D(16, 16, TextureFormat.RGBAHalf, false, true);
            try
            {
                Graphics.Blit(texture, small);
                RenderTexture.active = small;
                read.ReadPixels(new Rect(0, 0, 16, 16), 0, 0, false);
                Color[] pixels = read.GetPixels();
                double r = 0, g = 0, b = 0;
                foreach (Color p in pixels)
                {
                    r += p.r;
                    g += p.g;
                    b += p.b;
                }
                int n = pixels.Length;
                average = new Color((float)(r / n), (float)(g / n), (float)(b / n), 1f);
            }
            finally
            {
                RenderTexture.active = before;
                RenderTexture.ReleaseTemporary(small);
                Object.Destroy(read);
            }
            averages[texture] = average;
            Debug.Log("[ReDefinition] Raytracing: " + texture.name + " averages " + average.r.ToString("0.000") + ", "
                      + average.g.ToString("0.000") + ", " + average.b.ToString("0.000") + " for the terrain's colour.");
            return average;
        }
    }
}
