using System.Collections.Generic;
using System;
using ReDefinition.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Window
{
    // How the settings window stands against what is behind it. The skin's window
    // sprite is half transparent.
    //
    // Where nothing moves behind the window -- it holds the flight, or the scene
    // is the main menu, the space centre or an editor -- the window is drawn
    // nearly opaque: the dialog's own background once more behind
    // its contents -- the same sprite, type and colour, stretched over the whole
    // window -- which a UI theme that replaces the sprite, ZTheme, covers as well.
    //
    // While a flight runs behind it, the window is drawn thinner than the skin
    // draws it, and the rows are kept readable by their text instead: every text
    // gets a shadow under it (TextMeshPro's underlay). The shadow is one copy of
    // the font's material per font, shared by every text of that font, so the
    // window still draws in as few batches as before and KSP's own texts keep
    // their material.
    internal static class WindowBackdrop
    {
        private const string Name = "ReDefinitionBackdrop";

        // How much of the skin's own opacity the window keeps while the game runs
        // behind it.
        private const float SeeThrough = 0.55f;

        // Half a pixel of the font's size, dark and soft: enough against snow and
        // a bright sky, not enough to thicken the letters.
        private static readonly Color ShadowColor = new Color(0f, 0f, 0f, 0.9f);
        private const float ShadowOffset = 0.5f;
        private const float ShadowSoftness = 0.25f;
        private const float ShadowDilate = 0.1f;

        // The shadowed copy per material the texts came with.
        private static readonly Dictionary<int, Material> shadowed = new Dictionary<int, Material>();

        internal static void Add(PopupDialog dialog, bool holding)
        {
            try
            {
                if (dialog == null || dialog.popupWindow == null) return;
                if (holding)
                {
                    Image window = dialog.popupWindow.GetComponent<Image>();
                    if (window == null || window.sprite == null) return;
                    Image copy = Behind(dialog.popupWindow.transform);
                    copy.sprite = window.sprite;
                    copy.type = window.type;
                    copy.color = window.color;
                    return;
                }
                SeeThroughWindow(dialog.popupWindow);
                ShadowTexts(dialog.popupWindow);
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("window-backdrop", "The settings window's background stays as the skin draws it ("
                                                         + CompatibilityLog.Reason(e) + ").");
            }
        }

        // An image stretched over its parent, behind everything else in it, out
        // of the layout and of the clicks.
        private static Image Behind(Transform parent)
        {
            GameObject backdrop = new GameObject(Name, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            backdrop.GetComponent<LayoutElement>().ignoreLayout = true;
            RectTransform rect = (RectTransform)backdrop.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.SetAsFirstSibling();
            Image image = backdrop.GetComponent<Image>();
            image.raycastTarget = false;
            return image;
        }

        // The window's own background, thinner than the skin draws it.
        private static void SeeThroughWindow(GameObject window)
        {
            Image image = window.GetComponent<Image>();
            if (image == null) return;
            Color color = image.color;
            color.a *= SeeThrough;
            image.color = color;
        }

        private static void ShadowTexts(GameObject window)
        {
            foreach (TextMeshProUGUI text in window.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                Material material = text.fontSharedMaterial;
                if (material == null) continue;
                Material copy = Shadowed(material);
                if (copy != null) text.fontSharedMaterial = copy;
            }
        }

        private static Material Shadowed(Material material)
        {
            Material copy;
            if (shadowed.TryGetValue(material.GetInstanceID(), out copy)) return copy;

            copy = new Material(material);
            copy.name = material.name + " ReDefinition shadow";
            // TextMeshPro's underlay: the same glyphs drawn once more, offset and
            // softened, under the text (its SDF shaders' UNDERLAY_ON).
            copy.EnableKeyword("UNDERLAY_ON");
            if (copy.HasProperty("_UnderlayColor")) copy.SetColor("_UnderlayColor", ShadowColor);
            if (copy.HasProperty("_UnderlayOffsetX")) copy.SetFloat("_UnderlayOffsetX", ShadowOffset);
            if (copy.HasProperty("_UnderlayOffsetY")) copy.SetFloat("_UnderlayOffsetY", -ShadowOffset);
            if (copy.HasProperty("_UnderlaySoftness")) copy.SetFloat("_UnderlaySoftness", ShadowSoftness);
            if (copy.HasProperty("_UnderlayDilate")) copy.SetFloat("_UnderlayDilate", ShadowDilate);
            shadowed[material.GetInstanceID()] = copy;
            return copy;
        }
    }
}
