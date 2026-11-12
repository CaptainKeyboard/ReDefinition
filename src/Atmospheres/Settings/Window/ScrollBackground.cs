using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Atmospheres
{
    // The lighter background of the scroll view prefab of KSP's DialogGUIScrollList, taken out of the settings and forecast windows.
    // ScrollListSettle clears the Image of the list's root only; K-W3a read alpha 1 on the Image of the object that carries the ScrollRect
    // in both windows, so the Image of that object is cleared as well. Where the object also carries a Mask, the Mask's graphic is hidden
    // instead (a Mask over a fully transparent graphic hides everything under it).
    internal static class ScrollBackground
    {
        // Clears the backgrounds of the list under the window's root; true when something was changed.
        internal static bool Clear(GameObject window)
        {
            if (window == null) return false;
            bool changed = false;
            foreach (ScrollRect scroll in window.GetComponentsInChildren<ScrollRect>(true)) changed |= ClearOn(scroll.gameObject);
            return changed;
        }

        // Whether the background of the object that carries the ScrollRect is gone.
        internal static bool IsClear(ScrollRect scroll)
        {
            if (scroll == null) return false;
            Image image = scroll.GetComponent<Image>();
            if (image == null) return true;
            Mask mask = scroll.GetComponent<Mask>();
            return !Visible(image.color.a, mask != null, mask != null && mask.showMaskGraphic);
        }

        // Whether a graphic with that alpha shows: through its colour, or, under a Mask, through the mask graphic.
        internal static bool Visible(float alpha, bool hasMask, bool showMaskGraphic)
        {
            if (hasMask) return showMaskGraphic && alpha >= 0.01f;
            return alpha >= 0.01f;
        }

        // The Image and Mask of the object that carries a ScrollRect.
        private static bool ClearOn(GameObject target)
        {
            if (target == null) return false;
            Image image = target.GetComponent<Image>();
            if (image == null) return false;
            Mask mask = target.GetComponent<Mask>();
            if (mask != null)
            {
                if (!mask.showMaskGraphic) return false;
                mask.showMaskGraphic = false;
                return true;
            }
            if (image.color.a < 0.01f) return false;
            image.color = Color.clear;
            return true;
        }
    }
}
