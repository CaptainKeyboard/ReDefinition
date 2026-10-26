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
    //
    // Parallax draws its scatter without renderers (Graphics.DrawMeshInstancedIndirect),
    // so its materials are taken from Parallax itself (Extend). Without the bias the sun's shadow and the
    // diffuse light darkened the grass at the launch pad from 61.6 to 53.6 of 255
    // between AA only and Ultra Quality, 85.8 to 82.4 without the raytracing.
    internal class KspMipmapBias
    {
        // The textures that carry the bias, the instance IDs of the textures and materials already looked at,
        // the layers the redirected cameras render, and the bias added to every texture in `touched`.
        private readonly List<Texture> touched = new List<Texture>();
        private readonly HashSet<int> seenTextures = new HashSet<int>();
        private readonly HashSet<int> seenMaterials = new HashSet<int>();
        private int cullingMask;
        private float applied;

        // How many textures carry the bias now.
        public int TextureCount { get { return touched.Count; } }

        // The redirected cameras' culling masks combined: a renderer on another layer keeps its textures as
        // they are.
        public void SetVisibleLayers(int mask)
        {
            cullingMask = mask;
        }

        // Collects the textures of every visible renderer in the scene anew and adds `biasOffset` to each.
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
            return BiasAddedSince(before);
        }

        // The same for materials drawn without a renderer, such as Parallax's. Its grass and ferns cut their
        // blades out by the alpha of their texture at the mip level the pixel picks
        // (InstancedSolid.shader, ALPHA_CUTOFF), the raytracing by that alpha at full
        // resolution (RtCommon.hlsli, CandidateCounts). Without the bias, at a lower render
        // resolution the blades drawn and the blades the rays meet part. The rays from a
        // drawn blade start inside the clump the rays see.
        public int Extend(List<Material> materials)
        {
            if (Mathf.Approximately(applied, 0f)) return 0;
            int before = touched.Count;
            for (int i = 0; i < materials.Count; i++) Add(materials[i]);
            return BiasAddedSince(before);
        }

        // Gives the textures added to `touched` from index `before` on the bias applied; how many those are.
        private int BiasAddedSince(int before)
        {
            for (int i = before; i < touched.Count; i++) touched[i].mipMapBias += applied;
            return touched.Count - before;
        }

        // Takes the bias off every texture that got it and forgets them.
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

        // Empties the texture list and the sets of what was looked at, without touching any texture's bias.
        private void Forget()
        {
            touched.Clear();
            seenMaterials.Clear();
            seenTextures.Clear();
        }

        // Starts the list over from every renderer in the scene.
        private void Collect()
        {
            Forget();
            foreach (Renderer renderer in Object.FindObjectsOfType<Renderer>())
                Add(renderer);
        }

        // Adds the textures of an enabled, active renderer's materials where a redirected camera sees its layer.
        private void Add(Renderer renderer)
        {
            if (renderer == null || !renderer.enabled) return;
            if (!renderer.gameObject.activeInHierarchy) return;

            // If none of the redirected cameras sees this renderer, it does
            // not show up in the upscaled image either.
            if ((cullingMask & (1 << renderer.gameObject.layer)) == 0) return;

            foreach (Material material in renderer.sharedMaterials)
                Add(material);
        }

        // Adds the material's mipmapped textures not yet in the list; a material is looked at once.
        private void Add(Material material)
        {
            if (material == null) return;
            if (!seenMaterials.Add(material.GetInstanceID())) return;

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
