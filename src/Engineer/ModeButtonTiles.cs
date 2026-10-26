using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The planner button's tiles, made in the game from the cargo button's
    // tiles as they are: KSP's own, or a UI theme's such as ZTheme's, which
    // replaces them with flat ones. For each state the button takes, a tile is
    // the same sprite. The cargo glyph is painted over in the face's colours row by
    // row, and "Δv" is laid on in the glyph's own colour
    // (GameData/ReDefinition/Icons/EngineerGlyph_Fill.png, drawn by
    // tools/make_icon.py). So the button has the form, size and colours of its
    // neighbours. Textures are not readable from the CPU, so each is copied
    // through a render texture first.
    internal static class ModeButtonTiles
    {
        // Where the glyph's textures lie, without the part's name (Fill).
        private const string GlyphPath = "ReDefinition/Icons/EngineerGlyph_";

        // The glyph's height as a share of the face's, as KSP's and ZTheme's
        // cargo glyphs fill it.
        private const float GlyphShare = 0.66f;

        // Null where the source or the glyph cannot be read, or the source has
        // no face; the caller keeps its drawn tile then.
        internal static Sprite From(Sprite source)
        {
            if (source == null) return null;
            Texture2D mask = null;
            try
            {
                mask = TexturePixels.ReadableCopy(GameDatabase.Instance.GetTexture(GlyphPath + "Fill", false));
                int width = (int)source.rect.width;
                int height = (int)source.rect.height;
                if (mask == null) return null;

                Color32[] pixels = ReadSprite(source, width, height);
                RectInt face;
                if (!FaceOf(pixels, width, height, out face)) return null;
                Color32 fill = EraseGlyph(pixels, width, height);
                LayGlyph(pixels, width, face, mask, fill);
                Texture2D texture = new Texture2D(width, height, TextureFormat.ARGB32, false);
                texture.SetPixels32(pixels);
                texture.Apply();
                Vector2 pivot = new Vector2(source.pivot.x / source.rect.width, source.pivot.y / source.rect.height);
                return Sprite.Create(texture, new Rect(0f, 0f, width, height), pivot, source.pixelsPerUnit, 0,
                    SpriteMeshType.FullRect, source.border);
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The planner button's tile could not be made from " + source.name + ": " + e.Message);
                return null;
            }
            finally
            {
                if (mask != null) UnityEngine.Object.Destroy(mask);
            }
        }

        // The box of the tile's face pixels.
        private static bool FaceOf(Color32[] pixels, int width, int height, out RectInt face)
        {
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    if (!Face(pixels[y * width + x])) continue;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            face = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
            return maxX - minX > 8 && maxY - minY > 8;
        }

        // The glyph in the middle of the face, as high as KSP's own glyphs are
        // there, sampled from the mask wherever its strokes are.
        private static void LayGlyph(Color32[] pixels, int width, RectInt face, Texture2D mask, Color32 fill)
        {
            Color32[] maskPixels = mask.GetPixels32();
            int minX = mask.width, minY = mask.height, maxX = -1, maxY = -1;
            for (int y = 0; y < mask.height; y++)
                for (int x = 0; x < mask.width; x++)
                {
                    if (maskPixels[y * mask.width + x].a < 8) continue;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            if (maxX < minX) return;
            float glyphWidth = maxX - minX + 1f;
            float glyphHeight = maxY - minY + 1f;
            float scale = face.height * GlyphShare / glyphHeight;
            float left = face.x + (face.width - glyphWidth * scale) / 2f;
            float bottom = face.y + (face.height - glyphHeight * scale) / 2f;
            for (int y = face.yMin; y < face.yMax; y++)
                for (int x = face.xMin; x < face.xMax; x++)
                {
                    float u = (minX + (x + 0.5f - left) / scale) / mask.width;
                    float v = (minY + (y + 0.5f - bottom) / scale) / mask.height;
                    if (u < 0f || u > 1f || v < 0f || v > 1f) continue;
                    float b = mask.GetPixelBilinear(u, v).a;
                    if (b <= 0f) continue;
                    Color32 before = pixels[y * width + x];
                    Color32 over = Color32.Lerp(before, fill, b);
                    over.a = (byte)Math.Max(before.a, (int)(255f * b));
                    pixels[y * width + x] = over;
                }
        }

        // The sprite's full rectangle: its packed part copied to where it sits
        // (textureRect at textureRectOffset), the rest clear.
        private static Color32[] ReadSprite(Sprite source, int width, int height)
        {
            Color32[] all = TexturePixels.Of(source.texture);
            Color32[] pixels = new Color32[width * height];
            if (all == null) throw new InvalidOperationException("the texture cannot be read");
            Rect packed = source.textureRect;
            Vector2 offset = source.textureRectOffset;
            int textureWidth = source.texture.width;
            for (int y = 0; y < (int)packed.height; y++)
            {
                int toY = y + Mathf.RoundToInt(offset.y);
                int fromY = y + Mathf.RoundToInt(packed.y);
                if (toY < 0 || toY >= height || fromY < 0 || fromY >= source.texture.height) continue;
                for (int x = 0; x < (int)packed.width; x++)
                {
                    int toX = x + Mathf.RoundToInt(offset.x);
                    int fromX = x + Mathf.RoundToInt(packed.x);
                    if (toX < 0 || toX >= width || fromX < 0 || fromX >= textureWidth) continue;
                    pixels[toY * width + toX] = all[fromY * textureWidth + fromX];
                }
            }
            return pixels;
        }

        // Each row of the face, from six pixels inside its left edge to six
        // inside its right, blended between the colours there: the face's own
        // shading, without the glyph. A face pixel is opaque and teal, which
        // KSP's grey tabs are not. Returns the glyph's colour: the pixel that
        // stood out most from the face.
        private static Color32 EraseGlyph(Color32[] pixels, int width, int height)
        {
            Color32 glyph = new Color32(11, 36, 43, 255);
            int most = 60;
            for (int y = 0; y < height; y++)
            {
                int left = -1, right = -1;
                for (int x = 14; x < width - 14; x++)
                {
                    if (!Face(pixels[y * width + x])) continue;
                    if (left < 0) left = x;
                    right = x;
                }
                if (left < 0 || right - left < 20) continue;
                Color32 from = pixels[y * width + left + 6];
                Color32 to = pixels[y * width + right - 6];
                float span = Math.Max(1, right - left - 12);
                for (int x = left + 6; x < right - 5; x++)
                {
                    Color32 pixel = pixels[y * width + x];
                    if (pixel.a <= 230) continue;
                    Color32 blended = Color32.Lerp(from, to, (x - left - 6) / span);
                    blended.a = 255;
                    int apart = Math.Abs(pixel.r - blended.r) + Math.Abs(pixel.g - blended.g) + Math.Abs(pixel.b - blended.b);
                    if (apart > most)
                    {
                        most = apart;
                        glyph = pixel;
                    }
                    pixels[y * width + x] = blended;
                }
            }
            glyph.a = 255;
            return glyph;
        }

        // A pixel of the button's face: nearly opaque, and bluer or greener than red by more than 28, as the teal face is.
        private static bool Face(Color32 pixel)
        {
            return pixel.a > 230 && (pixel.b - pixel.r > 28 || pixel.g - pixel.r > 28);
        }
    }
}
