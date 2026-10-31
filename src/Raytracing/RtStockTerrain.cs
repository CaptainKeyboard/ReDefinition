using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // The ground's colour as KSP's own terrain shaders give it (Terrain/PQS/*), for the
    // rays, worked out per vertex and handed over as the vertex colours, with the
    // material's albedo black and its vertex factor 1 (RtScene). Read from KSP 1.12.5's
    // compiled shaders (their glcore programs, which keep the uniforms' names).
    //
    // Every PQS shader first turns the planet's colour (the vertex colour) by
    //   s = lum + _saturation * (c - lum), lum = dot(c, (0.299, 0.587, 0.114))
    //   v = _contrast * (s - t) + t,       t = _tintColor.rgb * _tintColor.a.
    // Then the bands or the atlas give col (Colours, AtlasAt). The result goes towards _steepTex
    // by saturate(uv2.y * _steepPower), finally times _albedoBrightness where the shader has it.
    // The standard-specular ones (those with _specularColor) write their diffuse colour times
    // 1 - max(specular), as Unity's standard shading does. The textures are taken as their
    // averages. The steep texture's fade with distance (from 10 km on) is left out.
    // KscColours has the space centre's ground.
    internal static class RtStockTerrain
    {
        // The PQS shaders' atlas, band and steep textures.
        private static readonly int AtlasId = Shader.PropertyToID("_AtlasTex");
        private static readonly int LowTexId = Shader.PropertyToID("_lowTex");
        private static readonly int MidTexId = Shader.PropertyToID("_midTex");
        private static readonly int HighTexId = Shader.PropertyToID("_highTex");
        private static readonly int SteepTexId = Shader.PropertyToID("_steepTex");

        // The textures' averages per layer, and buffers for the meshes' uv2 (steepness) and uv3 (atlas layers).
        private static readonly Dictionary<Texture, Color[]> averages = new Dictionary<Texture, Color[]>();
        private static readonly List<Vector2> uv2 = new List<Vector2>();
        private static readonly List<Vector2> uv3 = new List<Vector2>();

        // The space centre ground's blend mask, the size it is read at, the masks read by texture, and the uv1 buffer.
        private static readonly int BlendMaskId = Shader.PropertyToID("_BlendMaskTexture");
        private const int MaskSize = 256;
        private static readonly Dictionary<Texture, Color[]> masks = new Dictionary<Texture, Color[]>();
        private static readonly List<Vector2> uv1 = new List<Vector2>();

        // Whether the material is one of KSP's terrain shaders this knows.
        internal static bool Applies(Material material)
        {
            if (material == null || material.shader == null) return false;
            return Pqs(material) || Ksc(material);
        }

        // KSP's PQS terrain shaders, with an atlas or the three bands.
        private static bool Pqs(Material material)
        {
            return NamedLike(material.shader, PqsShaders, "Terrain/PQS/")
                   && (material.HasProperty(AtlasId) || material.HasProperty(LowTexId));
        }

        // KSP's space centre ground shaders, with their blend mask.
        private static bool Ksc(Material material)
        {
            return NamedLike(material.shader, KscShaders, "KSP/Scenery/Diffuse Ground KSC")
                   && material.HasProperty(BlendMaskId);
        }

        // Whether each shader seen has the name's start, so a mesh upload reads no shader name twice.
        private static readonly Dictionary<Shader, bool> PqsShaders = new Dictionary<Shader, bool>();
        private static readonly Dictionary<Shader, bool> KscShaders = new Dictionary<Shader, bool>();

        // Whether the shader's name starts with `prefix`, remembered in `known`.
        private static bool NamedLike(Shader shader, Dictionary<Shader, bool> known, string prefix)
        {
            bool named;
            if (!known.TryGetValue(shader, out named))
                known[shader] = named = shader.name.StartsWith(prefix, System.StringComparison.Ordinal);
            return named;
        }

        // The mesh's colours as the material draws its ground, or null where it is none of
        // these shaders. Triplanar Zoom Rotation and Main Shader - Triplanar Zoom Rotation give
        // _lowTex, _midTex and _highTex by the vertex colour's alpha (smoothstep between _lowStart
        // and _lowEnd, _highStart and _highEnd), col = v * tex. Main - Optimised and Main Shader
        // give the same bands, col = lerp(v, v * tex, _powerNear).
        internal static Color32[] Colours(Mesh mesh, Material material)
        {
            if (mesh == null || !Applies(material)) return null;
            if (Ksc(material)) return KscColours(mesh, material);
            Color32[] colours = mesh.colors32;
            if (colours.Length != mesh.vertexCount) return null;

            float saturation = Get(material, "_saturation", 1f);
            float contrast = Get(material, "_contrast", 1f);
            Color tint = material.HasProperty("_tintColor") ? material.GetColor("_tintColor") : Color.black;
            Vector3 t = new Vector3(tint.r, tint.g, tint.b) * tint.a;
            float brightness = Get(material, "_albedoBrightness", 1f);
            if (material.HasProperty("_specularColor"))
            {
                Color specular = material.GetColor("_specularColor");
                brightness *= 1f - Mathf.Max(specular.r, Mathf.Max(specular.g, specular.b));
            }
            float steepPower = Get(material, "_steepPower", 0f);
            Color steep = material.HasProperty(SteepTexId) ? Layer(material.GetTexture(SteepTexId), 0) : Color.black;
            bool hasSteep = material.HasProperty(SteepTexId) && material.GetTexture(SteepTexId) != null;

            bool atlas = material.HasProperty(AtlasId);
            Texture atlasTexture = atlas ? material.GetTexture(AtlasId) : null;
            float lerp = atlas ? Get(material, "_ColorLerpModifier", 1f) : material.HasProperty("_powerNear") ? Get(material, "_powerNear", 1f) : 1f;
            Color low = Color.white, mid = Color.white, high = Color.white;
            float lowStart = 0f, lowEnd = 0f, highStart = 0f, highEnd = 0f;
            if (!atlas)
            {
                low = Layer(material.GetTexture(LowTexId), 0);
                mid = material.HasProperty(MidTexId) ? Layer(material.GetTexture(MidTexId), 0) : low;
                high = material.HasProperty(HighTexId) ? Layer(material.GetTexture(HighTexId), 0) : mid;
                lowStart = Get(material, "_lowStart", 0f);
                lowEnd = Get(material, "_lowEnd", 0f);
                highStart = Get(material, "_highStart", 1f);
                highEnd = Get(material, "_highEnd", 1f);
            }

            mesh.GetUVs(1, uv2);
            if (atlas) mesh.GetUVs(2, uv3);
            bool steepness = uv2.Count == colours.Length;
            bool packed = atlas && uv3.Count == colours.Length;
            var result = new Color32[colours.Length];
            for (int i = 0; i < colours.Length; i++)
            {
                Color32 c32 = colours[i];
                Vector3 c = new Vector3(c32.r, c32.g, c32.b) / 255f;
                float lum = 0.299f * c.x + 0.587f * c.y + 0.114f * c.z;
                Vector3 s = new Vector3(lum, lum, lum) + saturation * (c - new Vector3(lum, lum, lum));
                Vector3 v = contrast * (s - t) + t;

                Color texture;
                if (atlas)
                    texture = packed ? AtlasAt(atlasTexture, uv3[i]) : Color.white;
                else
                {
                    float a = c32.a / 255f;
                    float hi = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(highStart, highEnd, a));
                    float lo = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(lowStart, lowEnd, a));
                    texture = low * (1f - lo) + mid * (lo - hi) + high * hi;
                }
                Vector3 col = Vector3.Lerp(v, Vector3.Scale(v, new Vector3(texture.r, texture.g, texture.b)), lerp);
                if (hasSteep && steepness)
                    col = Vector3.Lerp(col, new Vector3(steep.r, steep.g, steep.b), Mathf.Clamp01(uv2[i].y * steepPower));
                col *= brightness;
                result[i] = new Color32(Byte(col.x), Byte(col.y), Byte(col.z), 255);
            }
            return result;
        }

        // The space centre's ground per vertex: grass and tarmac averages mixed by the blend mask at the vertex's uv1.
        // The ground (KSP/Scenery/Diffuse Ground KSC and its variants) has no vertex colour. It has grass,
        // _NearGrassTexture times _GrassColor, and _TarmacTexture at uv0, mixed by _BlendMaskTexture's red at uv1
        // (both with their _ST). Here the averages are mixed by the mask at the vertex, so paths narrower than the
        // mesh's spacing blur into the grass.
        private static Color32[] KscColours(Mesh mesh, Material material)
        {
            int count = mesh.vertexCount;
            Color grass = material.HasProperty("_NearGrassTexture") ? Layer(material.GetTexture("_NearGrassTexture"), 0) : Color.white;
            if (material.HasProperty("_GrassColor")) grass *= material.GetColor("_GrassColor");
            Color tarmac = material.HasProperty("_TarmacTexture") ? Layer(material.GetTexture("_TarmacTexture"), 0) : Color.grey;
            Texture maskTexture = material.GetTexture(BlendMaskId);
            Color[] mask = Mask(maskTexture);
            Vector2 scale = material.GetTextureScale(BlendMaskId), offset = material.GetTextureOffset(BlendMaskId);
            mesh.GetUVs(1, uv1);
            bool masked = mask != null && uv1.Count == count;
            var result = new Color32[count];
            for (int i = 0; i < count; i++)
            {
                float m = masked ? Sample(mask, Vector2.Scale(uv1[i], scale) + offset).r : 0f;
                Color c = Color.Lerp(grass, tarmac, m);
                result[i] = new Color32(Byte(c.r), Byte(c.g), Byte(c.b), 255);
            }
            return result;
        }

        // The mask's pixels at MaskSize, once per texture.
        private static Color[] Mask(Texture texture)
        {
            if (texture == null) return null;
            Color[] pixels;
            if (masks.TryGetValue(texture, out pixels)) return pixels;
            RenderTexture small = RenderTexture.GetTemporary(MaskSize, MaskSize, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            RenderTexture before = RenderTexture.active;
            Texture2D read = new Texture2D(MaskSize, MaskSize, TextureFormat.RGBAHalf, false, true);
            try
            {
                Graphics.Blit(texture, small);
                RenderTexture.active = small;
                read.ReadPixels(new Rect(0, 0, MaskSize, MaskSize), 0, 0, false);
                pixels = read.GetPixels();
            }
            finally
            {
                RenderTexture.active = before;
                RenderTexture.ReleaseTemporary(small);
                Object.Destroy(read);
            }
            masks[texture] = pixels;
            return pixels;
        }

        // Bilinear, wrapping, as the shader's sampler (rows from the bottom, as ReadPixels).
        private static Color Sample(Color[] pixels, Vector2 uv)
        {
            float x = (uv.x - Mathf.Floor(uv.x)) * MaskSize - 0.5f, y = (uv.y - Mathf.Floor(uv.y)) * MaskSize - 0.5f;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            Color a = Pixel(pixels, x0, y0), b = Pixel(pixels, x0 + 1, y0);
            Color c = Pixel(pixels, x0, y0 + 1), d = Pixel(pixels, x0 + 1, y0 + 1);
            return Color.Lerp(Color.Lerp(a, b, fx), Color.Lerp(c, d, fx), fy);
        }

        // The mask's pixel at x, y, wrapped into the mask.
        private static Color Pixel(Color[] pixels, int x, int y)
        {
            x = ((x % MaskSize) + MaskSize) % MaskSize;
            y = ((y % MaskSize) + MaskSize) % MaskSize;
            return pixels[y * MaskSize + x];
        }

        // The atlas's colour at a vertex: its layers' averages by the strengths packed in uv3.
        // That is Texture Array 1-4 Blend (Kerbin at the highest terrain quality): up to four
        // layers of _AtlasTex, their indices and strengths packed into uv3. In x the indices, five bits
        // each, the first from 32768 up. In y the strengths, the first in units of 40000 / 200, the next two in
        // units of 200 and 1 times 0.01, the last the rest. The colour is lerp(v, v * atlas, _ColorLerpModifier).
        private static Color AtlasAt(Texture atlas, Vector2 packed)
        {
            int indices = Mathf.RoundToInt(packed.x);
            float y = Mathf.Round(packed.y);
            float s0 = Mathf.Floor(y / 40000f) / 200f;
            float rest = y - 40000f * Mathf.Floor(y / 40000f);
            float s1 = Mathf.Floor(rest / 200f) * 0.01f;
            float s2 = (rest - 200f * Mathf.Floor(rest / 200f)) * 0.01f;
            float s3 = 1f - s0 - s1 - s2;
            Color sum = Color.black;
            if (s0 > 0f) sum += Layer(atlas, indices >> 15) * s0;
            if (s1 > 0f) sum += Layer(atlas, (indices >> 10) & 31) * s1;
            if (s2 > 0f) sum += Layer(atlas, (indices >> 5) & 31) * s2;
            if (s3 > 0f) sum += Layer(atlas, indices & 31) * s3;
            return sum;
        }

        // A colour channel from 0 to 1 as a byte, rounded and clamped.
        private static byte Byte(float value)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
        }

        // The material's float `name`, or `otherwise` where its shader has none.
        private static float Get(Material material, string name, float otherwise)
        {
            return material.HasProperty(name) ? material.GetFloat(name) : otherwise;
        }

        // A texture's average in Unity's colour space (Layer), white for none.
        internal static Color Average(Texture texture)
        {
            return Layer(texture, 0);
        }

        // A texture's (or an array's layer's) average in Unity's colour space, once per
        // texture: each layer rendered down to 16 by 16 into a linear target, which keeps
        // the values the shaders work with, and read.
        private static Color Layer(Texture texture, int layer)
        {
            if (texture == null) return Color.white;
            Color[] layers;
            if (!averages.TryGetValue(texture, out layers))
            {
                Texture2DArray array = texture as Texture2DArray;
                int count = array != null ? array.depth : 1;
                layers = new Color[count];
                RenderTexture small = RenderTexture.GetTemporary(16, 16, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                RenderTexture before = RenderTexture.active;
                Texture2D read = new Texture2D(16, 16, TextureFormat.RGBAHalf, false, true);
                try
                {
                    for (int l = 0; l < count; l++)
                    {
                        if (array != null) Graphics.Blit(texture, small, l, 0);
                        else Graphics.Blit(texture, small);
                        RenderTexture.active = small;
                        read.ReadPixels(new Rect(0, 0, 16, 16), 0, 0, false);
                        Color[] pixels = read.GetPixels();
                        double r = 0, g = 0, b = 0, a = 0;
                        foreach (Color p in pixels)
                        {
                            r += p.r;
                            g += p.g;
                            b += p.b;
                            a += p.a;
                        }
                        int n = pixels.Length;
                        layers[l] = new Color((float)(r / n), (float)(g / n), (float)(b / n), (float)(a / n));
                    }
                }
                finally
                {
                    RenderTexture.active = before;
                    RenderTexture.ReleaseTemporary(small);
                    Object.Destroy(read);
                }
                averages[texture] = layers;
                var text = new System.Text.StringBuilder("[ReDefinition] Raytracing: KSP's terrain texture " + texture.name + " averages");
                for (int l = 0; l < layers.Length; l++)
                    text.Append(l == 0 ? " " : "; ").Append(layers[l].r.ToString("0.000")).Append(", ")
                        .Append(layers[l].g.ToString("0.000")).Append(", ").Append(layers[l].b.ToString("0.000"));
                Debug.Log(text.ToString());
            }
            return layer >= 0 && layer < layers.Length ? layers[layer] : Color.white;
        }
    }
}
