using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Shared
{
    // Sprites of whole textures from GameData, by their GameDatabase path, made
    // once and kept: the textures stay loaded for the whole game.
    internal static class IconSprites
    {
        // The sprites made so far by path; a null entry, where no texture was found, is looked up again.
        private static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();

        // Null where GameDatabase has no texture under `path`.
        internal static Sprite Load(string path)
        {
            Sprite sprite;
            if (sprites.TryGetValue(path, out sprite) && sprite != null) return sprite;
            Texture2D texture = GameDatabase.Instance != null ? GameDatabase.Instance.GetTexture(path, false) : null;
            sprite = texture != null
                ? Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f)
                : null;
            sprites[path] = sprite;
            return sprite;
        }
    }
}
