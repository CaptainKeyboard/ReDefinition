using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Ui
{
    // How a dialog's scroll list is set after it is spawned.
    internal static class ScrollListSettle
    {
        // A scroll list as KSP's own panels show one. Its content is held at
        // the top edge and always as tall as its rows. KSP's DialogGUIContentSizer
        // (decompiled) sizes it only while it is taller than the view and then
        // leaves it at its last height. So a list grown short stays scrollable
        // into empty room below its rows (as TabScrollList). Its background, a
        // lighter box of KSP's scroll view prefab, is cleared, so that the panel
        // it stands in shows one background.
        internal static void Apply(DialogGUIScrollList list)
        {
            if (list == null || list.uiItem == null) return;
            Image own = list.uiItem.GetComponent<Image>();
            if (own != null) own.color = Color.clear;
            ScrollRect scroll = list.uiItem.GetComponentInChildren<ScrollRect>();
            if (scroll == null || scroll.content == null) return;
            scroll.content.anchorMin = new Vector2(scroll.content.anchorMin.x, 1f);
            scroll.content.anchorMax = new Vector2(scroll.content.anchorMax.x, 1f);
            scroll.content.pivot = new Vector2(scroll.content.pivot.x, 1f);
            scroll.content.anchoredPosition = new Vector2(scroll.content.anchoredPosition.x, 0f);
            ContentSizeFitter fitter = scroll.content.GetComponent<ContentSizeFitter>();
            if (fitter == null) fitter = scroll.content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.enabled = true;
            scroll.verticalNormalizedPosition = 1f;
        }
    }
}
