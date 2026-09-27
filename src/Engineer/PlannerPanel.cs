using System.Collections.Generic;
using System;
using KSP.UI;
using KSP.UI.Screens;
using ReDefinition.Core;
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
        private readonly EditorEngineer owner;
        private UIPanelTransition transition;
        private UIPanelTransitionManager manager;
        private DialogGUIBase root;
        private PlannerView view;

        private PlannerPanel(EditorEngineer owner)
        {
            this.owner = owner;
        }

        internal static PlannerPanel Create(EditorEngineer owner)
        {
            EditorPanels panels = EditorPanels.Instance;
            if (panels == null || panels.crew == null || panels.panelManager == null) return null;
            PlannerPanel planner = new PlannerPanel(owner);
            if (!planner.Build(panels)) return null;
            return planner;
        }

        private bool Build(EditorPanels panels)
        {
            UIPanelTransition crew = panels.crew;
            manager = panels.panelManager;

            // Made under an inactive holder, so that nothing of the crew panel
            // wakes up in the copy before its content is taken off.
            GameObject holder = new GameObject("ReDefinitionPlannerHolder");
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
            float listWidth = Mathf.Max(200f, frame.sizeDelta.x + content.sizeDelta.x);
            float width = PlannerWidgets.RowRoom(listWidth);
            view = new PlannerView(owner, width);
            List<DialogGUIBase> rows = new List<DialogGUIBase>
            {
                new DialogGUIContentSizer(ContentSizeFitter.FitMode.Unconstrained, ContentSizeFitter.FitMode.PreferredSize, true),
            };
            rows.AddRange(view.Rows());
            DialogGUIVerticalLayout layout = new DialogGUIVerticalLayout(width, -1f, 0f, new RectOffset(0, 0, 0, 12),
                TextAnchor.UpperLeft, rows.ToArray());
            DialogGUIScrollList list = new DialogGUIScrollList(new Vector2(listWidth, Mathf.Max(200f, content.rect.height)), false,
                true, layout);
            root = list;
            Stack<Transform> layouts = new Stack<Transform>();
            layouts.Push(content);
            root.Create(ref layouts, skin);
            RectTransform scroll = root.uiItem != null ? root.uiItem.transform as RectTransform : null;
            if (scroll != null)
            {
                scroll.anchorMin = Vector2.zero;
                scroll.anchorMax = Vector2.one;
                scroll.offsetMin = Vector2.zero;
                scroll.offsetMax = Vector2.zero;
            }
            // The content is laid out with every row first and shrinks as the
            // rows the plan does not need hide. It is held at its top edge, so
            // that it shrinks from the bottom.
            PlannerWidgets.Settle(list);
            view.Finish();
            Debug.Log(Log.Tag + " Planner made from the crew panel: " + frame.sizeDelta.x.ToString("0") + " wide, rows "
                      + width.ToString("0") + " wide.");
            return true;
        }

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

        internal void Destroy()
        {
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

        internal void Update()
        {
            if (root == null || !Shown) return;
            view.Update();
            view.Keep();
            root.Update();
        }
    }
}
