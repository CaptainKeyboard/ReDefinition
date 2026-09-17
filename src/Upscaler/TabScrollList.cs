using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition
{
    // The settings window's scroll list, which every tab's page shares.
    //
    // KSP's DialogGUIScrollList (decompiled) puts its content into a ScrollRect
    // and sets the scroll position to the top only as it is made and resized.
    // KSP's DialogGUIContentSizer, which would size that content, turns its
    // ContentSizeFitter on only while the page is taller than the view and off
    // again once one fits, leaving the content at the height it last had: a
    // shorter page after a taller one slides down, and at first rows of more than
    // one line are squeezed onto each other. Here a
    // fitter of the list's own keeps the content at its page's height, always,
    // and the content hangs from the top -- Unity's ScrollRect places content
    // smaller than its view by the content's pivot (ScrollRect.AdjustBounds). A
    // new tab starts at the top once its page has been laid out (a few frames:
    // the page switches in DialogGUIBase.Update, its size follows in the layout
    // pass after).
    internal sealed class TabScrollList : DialogGUIScrollList
    {
        private const int TopFrames = 3;
        private int toTop;

        public TabScrollList(Vector2 size, DialogGUILayoutBase layout)
            : base(size, false, true, layout)
        {
        }

        public override GameObject Create(ref Stack<Transform> layouts, UISkinDef skin)
        {
            GameObject created = base.Create(ref layouts, skin);
            RectTransform content = scrollRect != null ? scrollRect.content : null;
            if (content != null)
            {
                content.anchorMin = new Vector2(content.anchorMin.x, 1f);
                content.anchorMax = new Vector2(content.anchorMax.x, 1f);
                content.pivot = new Vector2(content.pivot.x, 1f);
                content.anchoredPosition = new Vector2(content.anchoredPosition.x, 0f);

                ContentSizeFitter fitter = content.GetComponent<ContentSizeFitter>();
                if (fitter == null) fitter = content.gameObject.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                fitter.enabled = true;
            }
            return created;
        }

        // From the tab's toggle, as another tab is chosen.
        public void ToTop()
        {
            toTop = TopFrames;
        }

        public override void Update()
        {
            base.Update();
            if (toTop <= 0 || scrollRect == null) return;
            toTop--;
            scrollRect.StopMovement();
            // As KSP's own list sets the top (DialogGUIScrollList.Resize).
            Scrollbar bar = scrollRect.verticalScrollbar;
            scrollRect.verticalNormalizedPosition =
                bar != null && bar.direction != Scrollbar.Direction.BottomToTop ? 0f : 1f;
        }
    }
}
