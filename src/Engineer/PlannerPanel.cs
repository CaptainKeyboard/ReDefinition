using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Engineer
{
    // The planner (docs/player/planner.md) as a mode of the editor: KSP's dialog
    // elements on the skin's window background, over the whole parts panel --
    // its categories, its search bar and its list -- so that nothing of the
    // parts list can be reached behind it. It stands beside the panel rather
    // than inside it, where the panel's mask would cut it to the panel's width,
    // and is set over the panel every frame so that it slides with it. Built as
    // a PopupDialog builds its content (DialogGUIBase.Create, decompiled) and
    // updated here, as a PopupDialog updates its dialog every frame.
    internal sealed class PlannerPanel
    {
        // Wide enough for nine legs side by side in the fuel plan.
        private const float Width = 540f;
        private const float Padding = 8f;
        private const float Scrollbar = 18f;

        private readonly EditorEngineer owner;
        private GameObject host;
        private RectTransform panel;
        private DialogGUIBase root;
        private PlannerView view;
        private readonly Vector3[] corners = new Vector3[4];

        private PlannerPanel(EditorEngineer owner)
        {
            this.owner = owner;
        }

        internal static PlannerPanel Create(EditorEngineer owner, RectTransform panel)
        {
            RectTransform parent = panel != null ? panel.parent as RectTransform : null;
            if (parent == null) return null;
            PlannerPanel planner = new PlannerPanel(owner) { panel = panel };
            planner.Build(parent);
            return planner;
        }

        private void Build(RectTransform parent)
        {
            UISkinDef skin = UISkinManager.GetSkin("MiniSettingsSkin") ?? HighLogic.UISkin;
            host = new GameObject("ReDefinitionPlanner", typeof(RectTransform), typeof(Image));
            RectTransform rect = (RectTransform)host.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.SetAsLastSibling();
            Image background = host.GetComponent<Image>();
            background.raycastTarget = true;
            UIStyleState style = skin.window != null ? skin.window.normal : null;
            if (style != null && style.background != null)
            {
                background.sprite = style.background;
                background.type = Image.Type.Sliced;
            }
            else
            {
                background.color = new Color(0.12f, 0.13f, 0.14f, 1f);
            }
            Place();

            float height = Mathf.Max(200f, rect.rect.height);
            float width = Width - Padding * 2f - Scrollbar;
            view = new PlannerView(owner, width);
            List<DialogGUIBase> rows = new List<DialogGUIBase>
            {
                new DialogGUIContentSizer(ContentSizeFitter.FitMode.Unconstrained, ContentSizeFitter.FitMode.PreferredSize, true),
            };
            rows.AddRange(view.Rows());
            DialogGUIVerticalLayout content = new DialogGUIVerticalLayout(width, -1f, 0f,
                new RectOffset((int)Padding, (int)Padding, 8, 12), TextAnchor.UpperLeft, rows.ToArray());
            root = new DialogGUIScrollList(new Vector2(Width, height), false, true, content);
            Stack<Transform> layouts = new Stack<Transform>();
            layouts.Push(host.transform);
            root.Create(ref layouts, skin);
            RectTransform scroll = root.uiItem != null ? root.uiItem.transform as RectTransform : null;
            if (scroll != null)
            {
                scroll.anchorMin = Vector2.zero;
                scroll.anchorMax = Vector2.one;
                scroll.offsetMin = Vector2.zero;
                scroll.offsetMax = Vector2.zero;
            }
            view.Finish();
            Debug.Log(Core.Log.Tag + " Planner built " + rect.rect.width.ToString("0") + " by " + rect.rect.height.ToString("0")
                      + ", rows " + width.ToString("0") + " wide.");
            host.SetActive(false);
        }

        // Over the parts panel, from its left edge at the planner's width or the
        // panel's, whichever is wider.
        private void Place()
        {
            if (panel == null || host == null) return;
            RectTransform rect = (RectTransform)host.transform;
            RectTransform parent = rect.parent as RectTransform;
            if (parent == null) return;
            panel.GetWorldCorners(corners);
            Vector3 min = parent.InverseTransformPoint(corners[0]);
            Vector3 max = parent.InverseTransformPoint(corners[2]);
            rect.anchoredPosition = new Vector2(min.x, min.y) - parent.rect.min;
            rect.sizeDelta = new Vector2(Mathf.Max(Width, max.x - min.x), Mathf.Max(200f, max.y - min.y));
        }

        internal void Show(bool on)
        {
            if (host == null) return;
            if (on)
            {
                host.transform.SetAsLastSibling();
                Place();
            }
            host.SetActive(on);
            if (!on && view != null) view.Flush();
        }

        internal void Destroy()
        {
            if (view != null) view.Dispose();
            if (host != null) Object.Destroy(host);
            host = null;
        }

        internal void Update()
        {
            if (host == null || root == null || !host.activeSelf) return;
            Place();
            view.Update();
            root.Update();
        }
    }
}
