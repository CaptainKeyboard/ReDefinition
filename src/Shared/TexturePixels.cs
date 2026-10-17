using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ReDefinition.Shared
{
    // Pixels of textures that need not be readable, read back through a render
    // texture.
    internal static class TexturePixels
    {
        // A sprite's pixels, row by row from the bottom, or null where it cannot be
        // read. The texture is copied through a render texture, since a theme's
        // textures need not be readable. Where the graphics API counts a render
        // texture's rows from the top (Direct3D, SystemInfo.graphicsUVStartsAtTop),
        // the sprite's area lies mirrored in the copy, its rows still read from the
        // bottom: read from ZTheme's bottomBar_plain.png (the sprite is the whole
        // texture; its top row, which ends 5 pixels short of the right edge, came
        // last only unturned) and FlightButtonNextMorning.png (the sun's rows from
        // 0.04 to 1.00 of the sprite, as the file has them).
        internal static Color32[] OfSprite(Sprite sprite, out int width, out int height)
        {
            width = height = 0;
            if (sprite == null || sprite.texture == null) return null;
            Texture2D texture = sprite.texture;
            Rect area = sprite.textureRect;
            width = Mathf.RoundToInt(area.width);
            height = Mathf.RoundToInt(area.height);
            if (width <= 0 || height <= 0) return null;
            RenderTexture copy = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32);
            RenderTexture before = RenderTexture.active;
            Texture2D read = new Texture2D(width, height, TextureFormat.ARGB32, false);
            try
            {
                Graphics.Blit(texture, copy);
                RenderTexture.active = copy;
                bool flipped = SystemInfo.graphicsUVStartsAtTop;
                float from = flipped ? texture.height - area.y - height : area.y;
                read.ReadPixels(new Rect(area.x, from, width, height), 0, 0);
                read.Apply();
                return read.GetPixels32();
            }
            finally
            {
                RenderTexture.active = before;
                RenderTexture.ReleaseTemporary(copy);
                Object.Destroy(read);
            }
        }

        // The same as OfSprite, without waiting for the GPU: only the sprite's area is
        // copied, and read back a few frames later (AsyncGPUReadback), for a coroutine to
        // step through; done gets the pixels or null. OfSprite copies the whole texture
        // and waits for the GPU to finish all it was given before: for the vertical speed
        // gauge's picture at a flight's start, 621 ms of the main thread in one frame
        // (the perf monitor).
        internal static IEnumerator OfSpriteAsync(Sprite sprite, Action<Color32[], int, int> done)
        {
            if (sprite == null || sprite.texture == null || !SystemInfo.supportsAsyncGPUReadback)
            {
                int w, h;
                done(OfSprite(sprite, out w, out h), w, h);
                yield break;
            }
            Texture2D texture = sprite.texture;
            Rect area = sprite.textureRect;
            int width = Mathf.RoundToInt(area.width);
            int height = Mathf.RoundToInt(area.height);
            if (width <= 0 || height <= 0)
            {
                done(null, 0, 0);
                yield break;
            }
            RenderTexture copy = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            // The texture's coordinates from its bottom left, where the sprite's area lies.
            Graphics.Blit(texture, copy, new Vector2(area.width / texture.width, area.height / texture.height),
                          new Vector2(area.x / texture.width, area.y / texture.height));
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(copy, 0, TextureFormat.RGBA32);
            while (!request.done) yield return null;
            RenderTexture.ReleaseTemporary(copy);
            if (request.hasError)
            {
                done(null, 0, 0);
                yield break;
            }
            done(request.GetData<Color32>().ToArray(), width, height);
        }

        // A whole texture's pixels, row by row from the bottom; null for none.
        internal static Color32[] Of(Texture texture)
        {
            Texture2D copy = ReadableCopy(texture);
            if (copy == null) return null;
            Color32[] pixels = copy.GetPixels32();
            Object.Destroy(copy);
            return pixels;
        }

        // A texture's pixels at most `largest` on a side, row by row from the bottom, as its
        // texture coordinates run; null for none.
        internal static Color32[] Read(Texture texture, int largest, out Vector2Int size)
        {
            size = Vector2Int.zero;
            if (texture == null) return null;
            int width = Mathf.Max(1, Mathf.Min(texture.width, largest));
            int height = Mathf.Max(1, Mathf.Min(texture.height, largest));
            RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            RenderTexture before = RenderTexture.active;
            Texture2D copy = new Texture2D(width, height, TextureFormat.ARGB32, false);
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                copy.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                copy.Apply();
                size = new Vector2Int(width, height);
                return copy.GetPixels32();
            }
            finally
            {
                RenderTexture.active = before;
                RenderTexture.ReleaseTemporary(target);
                Object.Destroy(copy);
            }
        }

        // A readable copy of the texture, through a render texture.
        internal static Texture2D ReadableCopy(Texture texture)
        {
            if (texture == null) return null;
            RenderTexture target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            RenderTexture before = RenderTexture.active;
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                Texture2D copy = new Texture2D(texture.width, texture.height, TextureFormat.ARGB32, false);
                copy.ReadPixels(new Rect(0f, 0f, texture.width, texture.height), 0, 0);
                copy.Apply();
                return copy;
            }
            finally
            {
                RenderTexture.active = before;
                RenderTexture.ReleaseTemporary(target);
            }
        }
    }
}
