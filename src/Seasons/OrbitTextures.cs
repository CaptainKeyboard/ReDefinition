using UnityEngine;

namespace ReDefinition.Seasons
{
    // The textures and the material OrbitSnow draws with. The base copy holds the planet's colour map. The target is what the snow is
    // drawn into, with mips. The mask texture takes the worker's mask, and the overlay material draws it over the base copy.
    // OrbitSnow decides when; this class makes, draws and frees them.
    internal sealed class OrbitTextures
    {
        private static readonly string[] OverlayNames = { "UI/Default" };

        internal RenderTexture BaseCopy { get; private set; }
        internal RenderTexture Target { get; private set; }
        internal Texture2D Mask { get; private set; }
        internal Material Overlay { get; private set; }
        internal bool OverlayKeepsAlpha { get; private set; }

        // Whether the base copy is gone (a lost graphics device, or never made) and has to be filled from the original again.
        internal bool BaseLost
        {
            get { return BaseCopy == null || !BaseCopy.IsCreated(); }
        }

        // Makes the base copy and the target of that size, or keeps them where they have it already. True if new ones were made.
        internal bool Ensure(int width, int height, int anisoLevel)
        {
            if (BaseCopy != null && BaseCopy.width == width && BaseCopy.height == height) return false;
            Release();
            BaseCopy = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32) { name = "ReDefinition Seasons orbit base", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Repeat };
            BaseCopy.Create();
            Target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
            {
                name = "ReDefinition Seasons orbit map",
                useMipMap = true,
                autoGenerateMips = false,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Repeat,
                anisoLevel = Mathf.Max(1, anisoLevel)
            };
            Target.Create();
            return true;
        }

        // Copies the planet's own map into the base copy.
        internal void FillBase(Texture source)
        {
            if (BaseCopy != null && !BaseCopy.IsCreated()) BaseCopy.Create();
            Graphics.Blit(source, BaseCopy);
        }

        // Makes the overlay material and the mask texture where they are not there; false where no shader can draw the mask.
        internal bool EnsureOverlay()
        {
            if (Overlay == null) MakeOverlay();
            if (Overlay == null) return false;
            if (Mask == null)
            {
                Mask = new Texture2D(OrbitMask.MaskWidth, OrbitMask.MaskHeight, TextureFormat.RGBA32, false)
                {
                    name = "ReDefinition Seasons orbit mask",
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Bilinear
                };
                Overlay.mainTexture = Mask;
            }
            return true;
        }

        private void MakeOverlay()
        {
            Shader shader = StormShaders.Find(OverlayNames);
            if (shader != null)
            {
                Overlay = new Material(shader) { name = "ReDefinition Seasons orbit overlay" };
                if (Overlay.HasProperty("_ColorMask"))
                {
                    // Red, green and blue only (Alpha 1, Blue 2, Green 4, Red 8): the original's alpha channel stays as it is.
                    Overlay.SetFloat("_ColorMask", 14f);
                    OverlayKeepsAlpha = true;
                }
                return;
            }
            Overlay = StormShaders.Blended(null);
            if (Overlay != null) StormShaders.Tint(Overlay, Color.white, 1f);
        }

        // Draws the mask over the base copy into the target and builds the mips.
        internal void Compose()
        {
            if (!Target.IsCreated()) Target.Create();
            RenderTexture previous = RenderTexture.active;
            bool fog = RenderSettings.fog;
            // The overlay's shader may apply fog; the fog of a storm must not tint the map.
            RenderSettings.fog = false;
            try
            {
                Graphics.Blit(BaseCopy, Target);
                RenderTexture.active = Target;
                GL.PushMatrix();
                GL.LoadOrtho();
                Overlay.SetPass(0);
                GL.Begin(GL.QUADS);
                GL.Color(Color.white);
                GL.TexCoord2(0f, 0f);
                GL.Vertex3(0f, 0f, 0f);
                GL.TexCoord2(1f, 0f);
                GL.Vertex3(1f, 0f, 0f);
                GL.TexCoord2(1f, 1f);
                GL.Vertex3(1f, 1f, 0f);
                GL.TexCoord2(0f, 1f);
                GL.Vertex3(0f, 1f, 0f);
                GL.End();
                GL.PopMatrix();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderSettings.fog = fog;
            }
            Target.GenerateMips();
        }

        // Frees the two render textures.
        internal void Release()
        {
            if (Target != null)
            {
                Target.Release();
                Object.Destroy(Target);
            }
            if (BaseCopy != null)
            {
                BaseCopy.Release();
                Object.Destroy(BaseCopy);
            }
            Target = null;
            BaseCopy = null;
        }

        // Frees everything.
        internal void ReleaseAll()
        {
            Release();
            if (Overlay != null) Object.Destroy(Overlay);
            if (Mask != null) Object.Destroy(Mask);
            Overlay = null;
            Mask = null;
        }
    }
}
