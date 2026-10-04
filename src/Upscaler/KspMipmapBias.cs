using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // Applies the mipmap bias only to textures that end up in the upscaled image.
    //
    // At a lower render resolution Unity picks coarser mipmap levels, and the
    // upscaler cannot bring back detail those levels lack. A negative bias makes
    // Unity take finer levels than the render resolution suggests.
    //
    // A texture gets the bias where it hangs off a renderer on a layer one of the
    // redirected cameras renders (their culling masks). The bias belongs to the
    // texture, so where such a texture is used elsewhere as well -- in the UI, say
    // -- it applies there too; a texture only the UI uses keeps its own.
    // Texture2DArrays are included: Parallax keeps its terrain textures in them.
    // Renderers that come later -- a vessel loaded, a part added in the editors --
    // bring their textures in through Extend.
    internal class KspMipmapBias
    {
        private readonly List<Texture> touched = new List<Texture>();
        private readonly HashSet<int> seenTextures = new HashSet<int>();
        private readonly HashSet<int> seenMaterials = new HashSet<int>();
        private int cullingMask;
        private float applied;

        public int TextureCount { get { return touched.Count; } }

        public void SetVisibleLayers(int mask)
        {
            cullingMask = mask;
        }

        public void ApplyMipmapBias(float biasOffset)
        {
            if (float.IsNaN(biasOffset) || float.IsInfinity(biasOffset)) return;

            Collect();
            foreach (Texture texture in touched)
                texture.mipMapBias += biasOffset;

            applied += biasOffset;
        }

        // The bias applied to the textures of renderers that were not there at
        // ApplyMipmapBias; how many textures got it.
        public int Extend(IList<Renderer> renderers)
        {
            if (Mathf.Approximately(applied, 0f)) return 0;
            int before = touched.Count;
            for (int i = 0; i < renderers.Count; i++) Add(renderers[i]);
            for (int i = before; i < touched.Count; i++) touched[i].mipMapBias += applied;
            return touched.Count - before;
        }

        public void UndoMipmapBias()
        {
            if (Mathf.Approximately(applied, 0f))
            {
                applied = 0f;
                Forget();
                return;
            }

            // Undo the same list, do not collect again: the scene may have
            // changed in the meantime, and then textures would be reset that
            // never received a bias.
            foreach (Texture texture in touched)
            {
                if (texture != null) texture.mipMapBias -= applied;
            }

            applied = 0f;
            Forget();
        }

        private void Forget()
        {
            touched.Clear();
            seenMaterials.Clear();
            seenTextures.Clear();
        }

        private void Collect()
        {
            Forget();
            foreach (Renderer renderer in Object.FindObjectsOfType<Renderer>())
                Add(renderer);
        }

        private void Add(Renderer renderer)
        {
            if (renderer == null || !renderer.enabled) return;
            if (!renderer.gameObject.activeInHierarchy) return;

            // If none of the redirected cameras sees this renderer, it does
            // not show up in the upscaled image either.
            if ((cullingMask & (1 << renderer.gameObject.layer)) == 0) return;

            foreach (Material material in renderer.sharedMaterials)
            {
                if (material == null) continue;
                if (!seenMaterials.Add(material.GetInstanceID())) continue;

                foreach (string property in material.GetTexturePropertyNames())
                {
                    Texture texture = material.GetTexture(property);
                    if (texture == null) continue;

                    // Without mipmaps there is nothing to shift. That also
                    // rules out render and lookup textures right away.
                    if (texture.mipmapCount <= 1) continue;
                    if (!seenTextures.Add(texture.GetInstanceID())) continue;

                    touched.Add(texture);
                }
            }
        }
    }
}
