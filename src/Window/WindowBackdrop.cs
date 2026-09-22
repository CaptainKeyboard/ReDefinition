using System;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Window
{
    // A less see-through window: the dialog's own background drawn once more
    // behind its contents. The skin's window sprite is half transparent; a second
    // copy of it, the same sprite and colour, stretched over the whole window and
    // behind everything else, makes it more opaque the way the skin looks -- and a
    // UI theme that replaces the sprite, ZTheme, covers the copy as well.
    internal static class WindowBackdrop
    {
        private const string Name = "ReDefinitionBackdrop";

        internal static void Add(PopupDialog dialog)
        {
            try
            {
                if (dialog == null || dialog.popupWindow == null) return;
                Image window = dialog.popupWindow.GetComponent<Image>();
                if (window == null || window.sprite == null) return;

                GameObject backdrop = new GameObject(Name, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
                backdrop.GetComponent<LayoutElement>().ignoreLayout = true;
                RectTransform rect = (RectTransform)backdrop.transform;
                rect.SetParent(dialog.popupWindow.transform, false);
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                rect.SetAsFirstSibling();

                Image image = backdrop.GetComponent<Image>();
                image.sprite = window.sprite;
                image.type = window.type;
                image.color = window.color;
                image.raycastTarget = false;
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("window-backdrop", "The settings window's background stays as the skin draws it ("
                                                         + CompatibilityLog.Reason(e) + ").");
            }
        }
    }
}
