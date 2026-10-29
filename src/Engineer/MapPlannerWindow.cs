using System.Collections.Generic;
using KSP.UI;
using ReDefinition.Core;
using ReDefinition.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Engineer
{
    // The planner in the map view: a panel at the right edge of the screen that slides out over the map, with a tab on its left edge that opens
    // and closes it. It holds the planner's rows as the editor's panel does (PlannerView), over the vessel in flight (FlightPlannerHost). It is
    // there while the map view is, and the plan it shows is the vessel's, kept under the vessel's name.
    //
    // It is made once the flight scene has a vessel, out of KSP's own elements: the rows are dialog rows (DialogGUIBase) created into a scroll
    // list in a frame of its own, on the main canvas.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class MapPlannerWindow : MonoBehaviour
    {
        // The panel's width, the tab's, and the room left above, below and at its right for KSP's own bars and the toolbar column, in canvas units.
        private const float PanelWidth = 460f, TabWidth = 64f, TabHeight = 30f, TopMargin = 96f, BottomMargin = 70f, RightMargin = 56f;

        // Seconds the panel takes to slide.
        private const float SlideTime = 0.2f;

        private FlightPlannerHost host;
        private PlannerView view;
        private GameObject root;
        private RectTransform frame;
        private DialogGUIBase list;
        private DialogGUIBase tab;
        private bool open;
        private float slide;
        private bool failed;

        // Whether the panel is up: the map view is on and a vessel is flown.
        private static bool Wanted
        {
            get { return MapView.MapIsEnabled && FlightGlobals.ActiveVessel != null && HighLogic.LoadedSceneIsFlight; }
        }

        // Asked for by the probes: the panel open and out, without a click.
        internal static bool OpenAtOnce;

        private void Update()
        {
            if (failed) return;
            bool wanted = Wanted;
            if (root == null)
            {
                if (!wanted) return;
                try
                {
                    Build();
                }
                catch (System.Exception e)
                {
                    failed = true;
                    Debug.LogWarning(Log.Tag + " The planner in the map view could not be made (" + e.Message + ").");
                    return;
                }
                if (OpenAtOnce) open = true;
            }
            if (root.activeSelf != wanted) root.SetActive(wanted);
            if (!wanted) return;

            host.Tick();
            slide = Mathf.MoveTowards(slide, open ? 1f : 0f, Time.unscaledDeltaTime / SlideTime);
            float hidden = PanelWidth + RightMargin;
            frame.anchoredPosition = new Vector2(Mathf.Lerp(hidden, -RightMargin, Mathf.SmoothStep(0f, 1f, slide)), (BottomMargin - TopMargin) / 2f);
            if (tab != null) tab.Update();
            if (slide > 0f)
            {
                view.Keep(root);
                view.Tick();
                list.Update();
            }
        }

        // The panel made under the main canvas: a frame at the right edge, the tab on its left, and the planner's rows in a scroll list.
        private void Build()
        {
            UISkinDef skin = UISkinManager.GetSkin("MiniSettingsSkin") ?? HighLogic.UISkin;
            host = new FlightPlannerHost();
            root = new GameObject("ReDefinitionMapPlanner", typeof(RectTransform));
            root.layer = LayerMask.NameToLayer("UI");
            RectTransform rootRect = (RectTransform)root.transform;
            rootRect.SetParent(MainCanvasUtil.MainCanvas.transform, false);
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = rootRect.offsetMax = Vector2.zero;

            frame = NewRect("Frame", rootRect);
            frame.anchorMin = new Vector2(1f, 0f);
            frame.anchorMax = new Vector2(1f, 1f);
            frame.pivot = new Vector2(1f, 0.5f);
            frame.sizeDelta = new Vector2(PanelWidth, -(TopMargin + BottomMargin));

            Image back = frame.gameObject.AddComponent<Image>();
            back.color = new Color(0.05f, 0.07f, 0.1f, 0.88f);
            back.raycastTarget = true;

            float listWidth = PanelWidth - 6f;
            float rowWidth = PlannerWidgets.RowRoom(listWidth);
            view = new PlannerView(host, rowWidth);
            var rows = new List<DialogGUIBase>();
            rows.AddRange(view.Rows());
            TooltipText.WrapAll(rows);
            UiHygiene.DeferInteractable(rows);
            var layout = new DialogGUIVerticalLayout(rowWidth, -1f, 0f, new RectOffset(0, 0, 0, 12), TextAnchor.UpperLeft, rows.ToArray());
            float height = Screen.height / GameSettings.UI_SCALE - TopMargin - BottomMargin - 8f;
            var scroll = new DialogGUIScrollList(new Vector2(listWidth, Mathf.Max(200f, height)), false, true, layout);
            list = scroll;
            RectTransform content = NewRect("Content", frame);
            content.anchorMin = Vector2.zero;
            content.anchorMax = Vector2.one;
            content.offsetMin = new Vector2(3f, 3f);
            content.offsetMax = new Vector2(-3f, -3f);
            var layouts = new Stack<Transform>();
            layouts.Push(content);
            list.Create(ref layouts, skin);
            DialogTooltips.Attach(new[] { list });
            RectTransform listRect = list.uiItem != null ? list.uiItem.transform as RectTransform : null;
            if (listRect != null)
            {
                listRect.anchorMin = Vector2.zero;
                listRect.anchorMax = Vector2.one;
                listRect.offsetMin = listRect.offsetMax = Vector2.zero;
            }
            PlannerWidgets.Settle(scroll);
            view.Finish();
            ScrollThrough.Apply(list.uiItem);
            UiHygiene.MouseOnly(list.uiItem);

            // The tab: a button on the panel's left edge, near the top, which goes out with the panel.
            RectTransform tabHolder = NewRect("Tab", frame);
            tabHolder.anchorMin = tabHolder.anchorMax = new Vector2(0f, 1f);
            tabHolder.pivot = new Vector2(1f, 1f);
            tabHolder.sizeDelta = new Vector2(TabWidth, TabHeight);
            tabHolder.anchoredPosition = new Vector2(0f, -16f);
            var button = new DialogGUIButton("Plan", () => open = !open, TabWidth, TabHeight, false);
            tab = button;
            var tabLayouts = new Stack<Transform>();
            tabLayouts.Push(tabHolder);
            tab.Create(ref tabLayouts, skin);
            RectTransform buttonRect = tab.uiItem != null ? tab.uiItem.transform as RectTransform : null;
            if (buttonRect != null)
            {
                buttonRect.anchorMin = Vector2.zero;
                buttonRect.anchorMax = Vector2.one;
                buttonRect.offsetMin = buttonRect.offsetMax = Vector2.zero;
            }
            UiHygiene.MouseOnly(tab.uiItem);
            Debug.Log(Log.Tag + " The planner in the map view made: " + PanelWidth.ToString("0") + " wide, rows " + rowWidth.ToString("0") + " wide.");
        }

        private static RectTransform NewRect(string name, RectTransform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        private void OnDestroy()
        {
            if (view != null) view.Dispose();
            if (root != null) Destroy(root);
        }
    }
}
