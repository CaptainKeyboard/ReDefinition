using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The shaders and the texture the legacy storm pieces draw with. The Atmospheres' own bundle is read by AtmosphereShaders.Get (A02);
    // the members here that load from it are shims for the files of other packages that still call them (StormVolume, EveLayerStorm),
    // which switch to AtmosphereShaders.Get and then delete these. The rest are Unity's and KSP's own shaders found by name; none is
    // shipped.
    internal static class StormShaders
    {
        // Alpha-blended shaders, the first that exists is used: KSP's own particle shader, then Unity's legacy ones, then sprites.
        internal static readonly string[] BlendedNames =
        {
            "KSP/Particles/Alpha Blended",
            "Legacy Shaders/Particles/Alpha Blended",
            "Particles/Alpha Blended",
            "Sprites/Default"
        };

        // The raymarched storm volume's shader and the swirl shader, from the Atmospheres' own bundle.
        internal const string VolumeShaderName = "ReDefinition/AtmospheresStormVolume";
        internal const string SwirlShaderName = "ReDefinition/AtmospheresSwirlPaint";

        [System.Obsolete("Use AtmosphereShaders.BundleName.")]
        internal const string BundleName = AtmosphereShaders.BundleName;

        [System.Obsolete("Use AtmosphereShaders.BundlePath.")]
        internal static string BundlePath
        {
            get { return AtmosphereShaders.BundlePath; }
        }

        // The volume shader from the bundle, or null and the reason.
        [System.Obsolete("Use AtmosphereShaders.Get(StormShaders.VolumeShaderName, out error).")]
        internal static Shader LoadVolumeShader(out string error)
        {
            return AtmosphereShaders.Get(VolumeShaderName, out error);
        }

        // The swirl shader from the bundle, or null and the reason (an older bundle has none).
        [System.Obsolete("Use AtmosphereShaders.Get(StormShaders.SwirlShaderName, out error).")]
        internal static Shader LoadSwirlShader(out string error)
        {
            return AtmosphereShaders.Get(SwirlShaderName, out error);
        }

        // The first of the names that Shader.Find returns, or null.
        internal static Shader Find(string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                Shader shader = Shader.Find(names[i]);
                if (shader != null && shader.isSupported) return shader;
            }
            return null;
        }

        // A material with the first alpha-blended shader that exists, or null.
        internal static Material Blended(Texture texture)
        {
            Shader shader = Find(BlendedNames);
            if (shader == null) return null;
            var material = new Material(shader) { name = "ReDefinition Atmospheres storm" };
            material.mainTexture = texture;
            material.renderQueue = 3000;
            return material;
        }

        // Sets the colour a blended material draws with. The legacy particle shaders read _TintColor and double it; the others read _Color.
        internal static void Tint(Material material, Color colour, float alpha)
        {
            if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", new Color(colour.r * 0.5f, colour.g * 0.5f, colour.b * 0.5f, alpha * 0.5f));
            if (material.HasProperty("_Color")) material.SetColor("_Color", new Color(colour.r, colour.g, colour.b, alpha));
        }

        // A soft round white texture: alpha falls off from the middle to the edge.
        internal static Texture2D SoftDisc(int size)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "ReDefinition Atmospheres soft disc", wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            float half = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - half) * (x - half) + (y - half) * (y - half)) / half;
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a * (3f - 2f * a);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        // A solid white texture, for the veil.
        internal static Texture2D White()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "ReDefinition Atmospheres white" };
            texture.SetPixels32(new[] { new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255) });
            texture.Apply(false, true);
            return texture;
        }

        // The lowest layer in a camera's culling mask, layer 0 first; 0 where the mask is empty.
        internal static int LayerOf(Camera camera)
        {
            int mask = camera.cullingMask;
            if ((mask & 1) != 0) return 0;
            for (int i = 1; i < 32; i++) if ((mask & (1 << i)) != 0) return i;
            return 0;
        }
    }
}
