using System;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Window
{
    // How see-through the settings window is. The skin's window sprite is half
    // transparent.
    //
    // While the window holds the flight: the dialog's own background drawn once
    // more behind its contents -- the same sprite, type and colour, stretched
    // over the whole window -- which makes it more opaque the way the skin looks,
    // and a UI theme that replaces the sprite, ZTheme, covers the copy as well.
    //
    // While the game runs behind it: the skin's own transparency, so the flight
    // can be watched, with a dark panel behind the pages only, so that the rows
    // read against a bright sky or snow.
    internal static class WindowBackdrop
    {
        private const string Name = "ReDefinitionBackdrop";
        private static readonly Color PageBacking = new Color(0f, 0f, 0f, 0.45f);

        internal static void Add(PopupDialog dialog, bool holding, DialogGUIBase pages)
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
                if (pages == null || pages.uiItem == null) return;
                Behind(pages.uiItem.transform).color = PageBacking;
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
    }
}
