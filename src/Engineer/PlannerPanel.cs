using System.Collections.Generic;
using KSP.UI;
using KSP.UI.Screens;
using ReDefinition.Core;
using ReDefinition.Ui;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ReDefinition.Engineer
{
    // The planner (docs/player/planner.md) as a fifth panel of the editor, made
    // as KSP's crew panel is: a copy of its frame -- its transition, its
    // background, its content area -- with the planner's rows in the content
    // area (EditorPanels.crew, "Panel Crew" in the editor scene, read with
    // UnityPy). The copy is added to KSP's panel manager
    // (UIPanelTransitionManager.panels), so KSP brings it in and out as it does
    // its own panels: bringing it in takes the parts list out, and bringing in
    // any other panel takes it out. The rows are KSP's dialog elements, created
    // as a PopupDialog creates its content (DialogGUIBase.Create, decompiled).
    internal sealed class PlannerPanel
    {
        // The planner add-on it belongs to; the copy's transition and KSP's panel manager it was added to; the scroll
        // list that holds the rows; and the rows' view, which works out the plan.
        private readonly EditorEngineer owner;
        private UIPanelTransition transition;
        private UIPanelTransitionManager manager;
        private DialogGUIBase root;
        private PlannerView view;

        // Made only through Create, which builds it or leaves nothing of it.
        private PlannerPanel(EditorEngineer owner)
        {
            this.owner = owner;
        }

        // The planner panel built beside KSP's crew panel; null where the editor has no crew panel or the build fails.
        internal static PlannerPanel Create(EditorEngineer owner)
        {
            EditorPanels panels = EditorPanels.Instance;
            if (panels == null || panels.crew == null || panels.panelManager == null) return null;
            PlannerPanel planner = new PlannerPanel(owner);
            try
            {
                if (planner.Build(panels)) return planner;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(Log.Tag + " The planner could not be built: " + e);
            }
            // Nothing half made stays: the panel's copy in KSP's list of panels,
            // the view's subscriptions.
            planner.Destroy();
            return null;
        }

        // What is left between the list and the panel's right edge.
        private const float EdgeGap = 2f;

        // Copies the crew panel's frame, empties its content area, adds the copy to KSP's panel manager, and fills it with
        // the planner's rows in a scroll list as wide as the parts list; false where the crew panel has no content area.
        private bool Build(EditorPanels panels)
        {
            UIPanelTransition crew = panels.crew;
            manager = panels.panelManager;

            // Made under an inactive holder, so that nothing of the crew panel
            // wakes up in the copy before its content is taken off.
            GameObject holder = new GameObject("ReDefinitionPlannerHolder");
            building = holder;
            holder.SetActive(false);
            GameObject copy = UnityEngine.Object.Instantiate(crew.gameObject, holder.transform, false);
            copy.name = "Panel Planner";
            RectTransform content = null;
            foreach (Transform child in copy.transform)
                if (child.name == "anchorCrewManagement") content = (RectTransform)child;
            if (content == null)
            {
                UnityEngine.Object.Destroy(holder);
                Debug.LogWarning(Log.Tag + " The crew panel has no content area; the planner is left out.");
                return false;
            }
            for (int i = content.childCount - 1; i >= 0; i--) UnityEngine.Object.DestroyImmediate(content.GetChild(i).gameObject);
            foreach (MonoBehaviour behaviour in content.GetComponents<MonoBehaviour>()) UnityEngine.Object.DestroyImmediate(behaviour);

            transition = copy.GetComponent<UIPanelTransition>();
            // The crew panel's own reactions to its transitions stay the crew
            // panel's.
            transition.onTransitionStart = new UnityEvent();
            transition.onTransitionComplete = new UnityEvent();
            transition.childrenForDeactivate.Clear();

            copy.transform.SetParent(crew.transform.parent, false);
            copy.transform.SetSiblingIndex(crew.transform.GetSiblingIndex() + 1);
            UnityEngine.Object.Destroy(holder);
            copy.SetActive(true);
            transition.TransitionImmediate("Out");
            List<UIPanelTransition> all = new List<UIPanelTransition>(manager.panels);
            all.Add(transition);
            manager.panels = all.ToArray();

            UISkinDef skin = UISkinManager.GetSkin("MiniSettingsSkin") ?? HighLogic.UISkin;
            RectTransform frame = (RectTransform)copy.transform;
            // The content area stands in from the panel's right edge; the list reaches
            // out to it, so that its bar sits at the edge as the parts list's does.
            float reach = Mathf.Max(0f, -content.offsetMax.x - EdgeGap);
            float listWidth = Mathf.Max(200f, frame.sizeDelta.x + content.sizeDelta.x + reach);
            float width = PlannerWidgets.RowRoom(listWidth);
            view = new PlannerView(owner, width);
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            rows.AddRange(view.Rows());
            TooltipText.WrapAll(rows);
            // Rows hidden while their buttons' conditions turn keep them (UiHygiene).
            UiHygiene.DeferInteractable(rows);
            DialogGUIVerticalLayout layout = new DialogGUIVerticalLayout(width, -1f, 0f, new RectOffset(0, 0, 0, 12),
                TextAnchor.UpperLeft, rows.ToArray());
            DialogGUIScrollList list = new DialogGUIScrollList(new Vector2(listWidth, Mathf.Max(200f, content.rect.height)), false,
                true, layout);
            root = list;
            Stack<Transform> layouts = new Stack<Transform>();
            layouts.Push(content);
            root.Create(ref layouts, skin);
            DialogTooltips.Attach(new DialogGUIBase[] { root });
            RectTransform scroll = root.uiItem != null ? root.uiItem.transform as RectTransform : null;
            if (scroll != null)
            {
                scroll.anchorMin = Vector2.zero;
                scroll.anchorMax = Vector2.one;
                scroll.offsetMin = Vector2.zero;
                scroll.offsetMax = new Vector2(reach, 0f);
                // The wheel scrolls as far as in the parts list: KSP's dialog lists
                // scroll 10 units a notch (DialogGUIScrollList.Create; decompiled),
                // its parts list 50 (EditorPartList.partListScrollRect, sharedassets6).
                ScrollRect rect = scroll.GetComponent<ScrollRect>();
                ScrollRect parts = EditorPartList.Instance != null ? EditorPartList.Instance.partListScrollRect : null;
                if (rect != null && parts != null)
                {
                    rect.scrollSensitivity = parts.scrollSensitivity;
                    Debug.Log(Log.Tag + " Planner scrolls " + rect.scrollSensitivity.ToString("0") + " units a wheel notch, as the parts list.");
                }
            }
            // The content is laid out with every row first and shrinks as the
            // rows the plan does not need hide. It is held at its top edge, so
            // that it shrinks from the bottom.
            PlannerWidgets.Settle(list);
            view.Finish();
            // The wheel over a button or a text field scrolls the list, and no
            // control here takes the keyboard's selection (UiHygiene).
            ScrollThrough.Apply(root.uiItem);
            UiHygiene.MouseOnly(root.uiItem);
            Debug.Log(Log.Tag + " Planner made from the crew panel: " + frame.sizeDelta.x.ToString("0") + " wide, rows "
                      + width.ToString("0") + " wide.");
            return true;
        }

        // Whether the panel stands in or is sliding.
        internal bool Shown
        {
            get { return transition != null && (transition.State == "In" || transition.Transitioning); }
        }

        // In as KSP brings in its crew panel: the other panels go out, and so
        // does the search bar.
        internal void BringIn()
        {
            if (transition == null) return;
            manager.BringIn(transition);
            EditorPanels.Instance.searchField.Transition("Out");
        }

        // Out, and the parts list back as KSP brings it in (EditorPanels.
        // ShowPartsList, decompiled). KSP still counts the parts mode as the
        // one shown, so its own ShowPartsList would do nothing.
        internal void BackToParts()
        {
            EditorPanels panels = EditorPanels.Instance;
            if (panels == null) return;
            manager.BringIn(panels.partsEditor);
            panels.searchField.Transition("In");
            panels.updatePartsListMode();
        }

        // Out by itself: KSP's cargo mode brings the parts list and the cargo
        // panel in together and left the planner standing in the game.
        internal void Closed()
        {
            if (view != null) view.Flush();
            if (transition != null && transition.State != "Out") manager.Dismiss(transition);
        }

        // The holder the copy is made under, until it is destroyed in Build.
        private GameObject building;

        // Takes the panel out of KSP's list of panels and destroys it, with the view's subscriptions and a holder left
        // over from a failed build.
        internal void Destroy()
        {
            if (building != null) UnityEngine.Object.Destroy(building);
            if (view != null) view.Dispose();
            if (manager != null && transition != null)
            {
                List<UIPanelTransition> all = new List<UIPanelTransition>(manager.panels);
                all.Remove(transition);
                manager.panels = all.ToArray();
            }
            if (transition != null) UnityEngine.Object.Destroy(transition.gameObject);
            transition = null;
        }

        // Worked out without being shown, for the dashboard: the plan and its rows' texts,
        // with nothing of the panel drawn or held.
        internal void TickHidden()
        {
            if (root == null || Shown) return;
            view.Tick();
        }

        // The rows' view, which works out the plan; null before the panel is built.
        internal PlannerView View
        {
            get { return view; }
        }

        // While the panel is shown: keeps the typing lock, works out the plan and updates the rows.
        internal void Tick()
        {
            if (root == null || !Shown) return;
            // The typing lock first: it has to go even where the rest throws.
            view.Keep(root.uiItem);
            view.Tick();
            root.Update();
        }
    }
}
