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
    internal class KspMipmapBias
    {
        private readonly List<Texture> touched = new List<Texture>();
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

        public void UndoMipmapBias()
        {
            if (Mathf.Approximately(applied, 0f))
            {
                applied = 0f;
                touched.Clear();
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
            touched.Clear();
        }

        private void Collect()
        {
            touched.Clear();

            HashSet<int> seenMaterials = new HashSet<int>();
            HashSet<int> seenTextures = new HashSet<int>();

            foreach (Renderer renderer in Object.FindObjectsOfType<Renderer>())
            {
                if (renderer == null || !renderer.enabled) continue;
                if (!renderer.gameObject.activeInHierarchy) continue;

                // If none of the redirected cameras sees this renderer, it does
                // not show up in the upscaled image either.
                if ((cullingMask & (1 << renderer.gameObject.layer)) == 0) continue;

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
}
