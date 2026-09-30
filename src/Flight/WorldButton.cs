using System.Collections;
using System.Collections.Generic;
using System.IO;
using KSP.UI;
using KSP.UI.TooltipTypes;
using ReDefinition.Core;
using ReDefinition.Window;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ReDefinition.Flight
{
    // A button over a place in the world, placed as KSP places its vessel labels:
    // on KSP's main canvas, at the world point's screen place from the canvas's
    // lower left over the UI scale (UIMasterController.WorldToMainCanvas), behind
    // the rest of the HUD. It looks like one of the space centre's facility buttons
    // (KscButtons) where those have been seen, else a symbol of ReDefinition's own
    // on a round plate. The main canvas stays across scenes: the button goes with
    // the add-on that made it (Destroy).
    internal sealed class WorldButton
    {
        internal const float Size = 40f;
        private static readonly Color Plate = new Color(0.13f, 0.13f, 0.16f, 0.9f);

        private readonly RectTransform rect;
        private readonly Image face;
        private readonly Image glyph;
        private readonly Button button;
        private readonly TooltipController_Text tooltip;
        private string look;

        internal WorldButton(string name, UnityAction click)
        {
            Transform canvas = UIMasterController.Instance.mainCanvas.transform;
            GameObject item = UiHygiene.Child(name, canvas, typeof(RectTransform), typeof(Image), typeof(Button));
            rect = (RectTransform)item.transform;
            rect.SetAsFirstSibling();
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(Size, Size);
            face = item.GetComponent<Image>();
            face.preserveAspect = true;
            button = item.GetComponent<Button>();
            button.targetGraphic = face;
            UiHygiene.MouseOnly(button);
            button.onClick.AddListener(click);
            tooltip = UiHygiene.Tooltip(item, "");
            if (tooltip != null) tooltip.continuousUpdate = true;

            GameObject symbol = UiHygiene.Child("Symbol", rect, typeof(RectTransform), typeof(Image));
            RectTransform symbolRect = (RectTransform)symbol.transform;
            symbolRect.anchorMin = symbolRect.anchorMax = symbolRect.pivot = new Vector2(0.5f, 0.5f);
            symbolRect.sizeDelta = new Vector2(Size - 14f, Size - 14f);
            glyph = symbol.GetComponent<Image>();
            glyph.preserveAspect = true;
            glyph.raycastTarget = false;
            item.SetActive(false);
        }

        internal string Tooltip
        {
            set { if (tooltip != null && tooltip.textString != value) tooltip.textString = value; }
        }

        // The space centre's button of that name (Buttons_VAB, ...) with its states,
        // else `fallback` on the round plate.
        internal void Look(string ksc, Sprite fallback)
        {
            if (look == ksc && face.sprite != null) return;
            KscButtons.Look states = KscButtons.Get(ksc);
            if (states != null)
            {
                face.sprite = states.Normal;
                face.color = Color.white;
                button.transition = Selectable.Transition.SpriteSwap;
                SpriteState swap = new SpriteState
                {
                    highlightedSprite = states.Highlighted,
                    pressedSprite = states.Pressed,
                    selectedSprite = states.Highlighted,
                    disabledSprite = states.Disabled,
                };
                button.spriteState = swap;
                glyph.enabled = false;
            }
            else
            {
                face.sprite = Disc();
                face.color = Plate;
                button.transition = Selectable.Transition.ColorTint;
                ColorBlock tint = button.colors;
                tint.highlightedColor = new Color(2f, 2f, 2.2f, 1f);
                tint.pressedColor = new Color(2.6f, 2.6f, 2.8f, 1f);
                tint.colorMultiplier = 1f;
                button.colors = tint;
                glyph.sprite = fallback;
                glyph.enabled = fallback != null;
            }
            look = ksc;
        }

        // At the world point where it is before the camera; hidden otherwise.
        internal bool Place(Vector3 world, Camera camera)
        {
            bool shown = camera != null && camera.WorldToViewportPoint(world).z > 0f;
            if (rect.gameObject.activeSelf != shown) rect.gameObject.SetActive(shown);
            if (shown) rect.anchoredPosition = UIMasterController.WorldToMainCanvas(world, camera);
            return shown;
        }

        internal void Hide()
        {
            if (rect != null && rect.gameObject.activeSelf) rect.gameObject.SetActive(false);
        }

        internal void Destroy()
        {
            if (rect != null) Object.Destroy(rect.gameObject);
        }

        private static Sprite disc;

        private static Sprite Disc()
        {
            if (disc != null) return disc;
            const int size = 64;
            Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            Color32[] pixels = new Color32[size * size];
            float c = (size - 1) / 2f, r = size / 2f - 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(r - d + 0.5f) * 255f));
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            disc = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
            return disc;
        }

        // The middle of what a transform draws, in the world; its own place where it
        // draws nothing.
        internal static Vector3 Middle(Transform place)
        {
            Renderer[] parts = place.GetComponentsInChildren<Renderer>(false);
            if (parts.Length == 0) return place.position;
            Bounds bounds = parts[0].bounds;
            foreach (Renderer part in parts) bounds.Encapsulate(part.bounds);
            return bounds.center;
        }
    }

    // The space centre's facility buttons (the column of buttons for the VAB, the
    // SPH, the launch pad, the runway and the rest; their sprites Buttons_VAB_0 to
    // _3 and so on belong to the space centre's scene, sharedassets5), copied as
    // the scene shows them -- a UI theme's where one replaces them -- with the
    // states their Buttons swap to, so that the buttons over the buildings and in
    // the editors look like them. Copied every time the space centre is shown, and
    // kept on disk for a game started straight into another scene
    // (PluginData/ksc/).
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class KscButtons : MonoBehaviour
    {
        internal sealed class Look
        {
            internal Sprite Normal, Highlighted, Pressed, Disabled;
        }

        internal static readonly string[] Wanted = { "Buttons_VAB", "Buttons_SPH", "Buttons_Launchpad", "Buttons_Runway" };
        private static readonly Dictionary<string, Look> looks = new Dictionary<string, Look>();

        internal static Look Get(string name)
        {
            Look look;
            if (looks.TryGetValue(name, out look) && look.Normal != null) return look;
            look = FromDisk(name);
            if (look != null) looks[name] = look;
            return look;
        }

        private IEnumerator Start()
        {
            // A theme swaps the pictures as the scene's UI stands.
            for (int i = 0; i < 10; i++) yield return null;
            int found = 0;
            foreach (Button button in Resources.FindObjectsOfTypeAll<Button>())
            {
                Image image = button != null ? button.targetGraphic as Image : null;
                if (image == null || image.sprite == null || !button.gameObject.scene.IsValid()) continue;
                string name = Base(image.sprite.name);
                if (name == null || Seen.Contains(name)) continue;
                Look look = new Look
                {
                    Normal = Copy(image.sprite, name, "normal"),
                    Highlighted = Copy(button.spriteState.highlightedSprite, name, "highlighted"),
                    Pressed = Copy(button.spriteState.pressedSprite, name, "pressed"),
                    Disabled = Copy(button.spriteState.disabledSprite, name, "disabled"),
                };
                if (look.Normal == null) continue;
                looks[name] = look;
                Seen.Add(name);
                found++;
            }
            Seen.Clear();
            Debug.Log(Log.Tag + " The space centre's facility buttons copied for the buttons in the world: " + found + " of "
                      + Wanted.Length + ".");
        }

        private static readonly HashSet<string> Seen = new HashSet<string>();

        // "Buttons_VAB" for "Buttons_VAB_0"; null for another sprite.
        private static string Base(string sprite)
        {
            foreach (string wanted in Wanted)
                if (sprite.StartsWith(wanted + "_", System.StringComparison.Ordinal)) return wanted;
            return null;
        }

        private static string File(string name, string state)
        {
            return PluginData.Path(Path.Combine("ksc", name + "_" + state + ".png"));
        }

        // A copy of a sprite in a texture of its own, and on disk.
        private static Sprite Copy(Sprite sprite, string name, string state)
        {
            if (sprite == null) return null;
            int width, height;
            Color32[] pixels = TimeWarpBar.Pixels(sprite, out width, out height);
            if (pixels == null) return null;
            Texture2D texture = new Texture2D(width, height, TextureFormat.ARGB32, false);
            texture.SetPixels32(pixels);
            texture.Apply();
            try
            {
                string path = File(name, state);
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
                System.IO.File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            catch (System.Exception e)
            {
                CompatibilityLog.Warn("ksc-buttons-save", "The space centre's buttons could not be kept on disk (" + CompatibilityLog.Reason(e) + ").");
            }
            return Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), sprite.pixelsPerUnit);
        }

        private static Look FromDisk(string name)
        {
            Sprite normal = Load(name, "normal");
            if (normal == null) return null;
            return new Look
            {
                Normal = normal,
                Highlighted = Load(name, "highlighted") ?? normal,
                Pressed = Load(name, "pressed") ?? normal,
                Disabled = Load(name, "disabled") ?? normal,
            };
        }

        private static Sprite Load(string name, string state)
        {
            string path = File(name, state);
            if (!System.IO.File.Exists(path)) return null;
            Texture2D texture = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            if (!texture.LoadImage(System.IO.File.ReadAllBytes(path))) return null;
            return Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
        }
    }
}
