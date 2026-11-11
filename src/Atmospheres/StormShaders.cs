using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Shim for the files of other packages that still call it: StormVolume (LoadVolumeShader, LayerOf, bundle names), EveLayerStorm
    // (LoadSwirlShader, Find), OrbitTextures (Find, Blended, Tint) and VolumePass (LayerOf). The Atmospheres' own bundle is read by
    // AtmosphereShaders.Get (A02). Each of those packages (C21, C25, D34) switches to AtmosphereShaders.Get or its own helper and the
    // last of them deletes this class. The flakes' and the veil's textures are gone with StormFlakes and StormVeil (C22).
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
