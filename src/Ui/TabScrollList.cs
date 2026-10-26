using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Ui
{
    // The settings window's scroll list, which every tab's page shares.
    //
    // KSP's DialogGUIScrollList (decompiled) puts its content into a ScrollRect
    // and sets the scroll position to the top only as it is made and resized.
    // KSP's DialogGUIContentSizer, which would size that content, turns its
    // ContentSizeFitter on only while the page is taller than the view and off
    // again once one fits, leaving the content at the height it last had.
    // So a shorter page after a taller one slides down, and at first rows of more than
    // one line are squeezed onto each other.
    // Here a fitter of the list's own keeps the content at its page's height, always,
    // and the content hangs from the top.
    // Unity's ScrollRect places content smaller than its view by the content's pivot (ScrollRect.AdjustBounds).
    // A new tab starts at the top once its page has been laid out: a few frames,
    // since the page switches in DialogGUIBase.Update and its size follows in the layout
    // pass after.
    internal sealed class TabScrollList : DialogGUIScrollList
    {
        // For how many frames a new tab is put back to the top, and how many of them are left.
        private const int TopFrames = 3;
        private int toTop;

        // The probe's shot of a page's lower part (-redefinitionProbeSettingsWindowScroll): the
        // normalized position to hold for the first frames, 1 the top and 0 the bottom; null for none.
        internal static float? ProbePosition;
        private int probeFrames = 12;

        // A vertical scroll list of that size over the pages' layout.
        public TabScrollList(Vector2 size, DialogGUILayoutBase layout)
            : base(size, false, true, layout)
        {
        }

        // Makes the list as KSP does, then hangs its content from the top with a fitter that keeps it at the page's height.
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

        // Holds the probe's position for its first frames, else puts a new tab back to the top while its frames last.
        public override void Update()
        {
            base.Update();
            if (ProbePosition.HasValue && scrollRect != null && probeFrames > 0)
            {
                probeFrames--;
                scrollRect.StopMovement();
                scrollRect.verticalNormalizedPosition = ProbePosition.Value;
                return;
            }
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
