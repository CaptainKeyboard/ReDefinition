using UnityEngine;

namespace ReDefinition.Seasons
{
    // The shaders and the texture the storm pieces draw with. They are Unity's and KSP's own shaders found by name; none is shipped.
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

        // The raymarched storm volume's shader, from the Seasons' own bundle (unity/Assets/ReDefinitionSeasons, built by
        // tools/build_seasons_bundle.ps1), and the bundle's file in GameData/ReDefinition/Seasons/Shaders.
        internal const string VolumeShaderName = "ReDefinition/SeasonsStormVolume";
        internal const string BundleName = "seasons.shaders";
        private static AssetBundle bundle;
        private static Shader volumeShader;
        private static string volumeError;
        private static bool volumeTried;

        internal static string BundlePath
        {
            get
            {
                return System.IO.Path.Combine(KSPUtil.ApplicationRootPath, System.IO.Path.Combine("GameData", System.IO.Path.Combine("ReDefinition",
                    System.IO.Path.Combine("Seasons", System.IO.Path.Combine("Shaders", BundleName)))));
            }
        }

        // The volume shader from the bundle, or null and the reason. Looked up once; a bundle that is missing or rejected stays so
        // until the game restarts. The player rejects a bundle from another Unity version without a word.
        internal static Shader LoadVolumeShader(out string error)
        {
            if (volumeTried)
            {
                error = volumeError;
                return volumeShader;
            }
            volumeTried = true;
            string path = BundlePath;
            if (!System.IO.File.Exists(path))
                volumeError = "the shader bundle is missing: " + path + " (tools/build_seasons_bundle.ps1 builds it)";
            else
            {
                bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null)
                    volumeError = "AssetBundle could not be loaded: " + path + " (it has to come from Unity 2019.4.18f1)";
                else
                {
                    foreach (Shader loaded in bundle.LoadAllAssets<Shader>())
                    {
                        if (loaded == null || loaded.name != VolumeShaderName) continue;
                        if (loaded.isSupported) volumeShader = loaded;
                        else volumeError = VolumeShaderName + " is in the bundle but does not compile or run on this graphics device";
                        break;
                    }
                    if (volumeShader == null && volumeError == null) volumeError = "the bundle has no " + VolumeShaderName;
                }
            }
            error = volumeError;
            return volumeShader;
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
            var material = new Material(shader) { name = "ReDefinition Seasons storm" };
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
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "ReDefinition Seasons soft disc", wrapMode = TextureWrapMode.Clamp };
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
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "ReDefinition Seasons white" };
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
