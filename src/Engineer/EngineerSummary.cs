using System.Collections.Generic;
using KSP.UI.Screens;
using ReDefinition.Window;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Engineer
{
    // One line beside the parts list while the engineer view is closed: the
    // vessel's delta-v, its TWR at launch, its mass and its parts, for the
    // situation the view is set to. It stands right of the parts panel, under
    // the editor's mode buttons, and is set there every frame so that it slides
    // with the panel. It is beside the panel rather than inside it, where the
    // panel's mask could cut it off. A click on it opens the view.
    internal sealed class EngineerSummary
    {
        private const float Width = 300f;
        private const float Height = 20f;
        private const string Warning = "#ff9a3d";

        private readonly EditorEngineer owner;
        private GameObject host;
        private DialogGUIBase root;
        private RectTransform panel;
        private readonly Vector3[] corners = new Vector3[4];

        private EngineerSummary(EditorEngineer owner)
        {
            this.owner = owner;
        }

        internal static EngineerSummary Create(EditorEngineer owner)
        {
            RectTransform parent = EditorPanels.Instance != null ? EditorPanels.Instance.partsEditor.transform as RectTransform : null;
            if (parent == null) return null;
            EngineerSummary summary = new EngineerSummary(owner) { panel = parent };
            summary.Build(parent.parent as RectTransform ?? parent);
            return summary;
        }

        private void Build(RectTransform parent)
        {
            host = new GameObject("ReDefinitionEngineerSummary", typeof(RectTransform), typeof(Image));
            RectTransform rect = (RectTransform)host.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(Width, Height);
            Image background = host.GetComponent<Image>();
            background.color = new Color(0.08f, 0.09f, 0.10f, 0.78f);

            // A label rather than a button: a button's text is bold in the skin.
            // The click is taken by a button on the line's own background.
            Button click = host.AddComponent<Button>();
            click.transition = Selectable.Transition.None;
            click.onClick.AddListener(() => owner.Open());

            UISkinDef skin = UISkinManager.GetSkin("MiniSettingsSkin") ?? HighLogic.UISkin;
            DialogGUILabel line = new DialogGUILabel(Text, Width - 12f, Height);
            root = new DialogGUIHorizontalLayout(Width, Height, 0f, new RectOffset(6, 6, 0, 0), TextAnchor.MiddleLeft, line);

            Stack<Transform> layouts = new Stack<Transform>();
            layouts.Push(host.transform);
            root.Create(ref layouts, skin);
            RectTransform created = root.uiItem != null ? root.uiItem.transform as RectTransform : null;
            if (created != null)
            {
                created.anchorMin = Vector2.zero;
                created.anchorMax = Vector2.one;
                created.offsetMin = Vector2.zero;
                created.offsetMax = Vector2.zero;
            }
            host.SetActive(false);
        }

        internal bool Wanted(OwnSettings settings)
        {
            return settings == null || settings.EngineerSummary;
        }

        internal void Update(OwnSettings settings, bool partsShown)
        {
            if (host == null) return;
            bool visible = partsShown && Wanted(settings) && owner.Readout != null && owner.Readout.HasShip;
            if (host.activeSelf != visible) host.SetActive(visible);
            if (!visible) return;
            if (panel != null)
            {
                panel.GetWorldCorners(corners);
                RectTransform rect = (RectTransform)host.transform;
                Vector3 at = corners[2];
                // Under the mode buttons where they reach below the panel's top.
                Button cargo = EditorLogic.fetch != null ? EditorLogic.fetch.cargoPanelBtn : null;
                if (cargo != null)
                {
                    ((RectTransform)cargo.transform).GetWorldCorners(corners);
                    at.y = Mathf.Min(at.y, corners[0].y);
                }
                rect.position = at;
                rect.localPosition += new Vector3(8f, -4f, 0f);
            }
            root.Update();
        }

        internal void Destroy()
        {
            if (host != null) Object.Destroy(host);
            host = null;
        }

        private string Text()
        {
            EditorReadout readout = owner.Readout;
            if (readout == null || !readout.HasShip) return "";
            StageRow first = StageTable.FirstBurning(readout.Stages);
            string twr = first == null ? "--" : EngineerFormat.Ratio(first.Twr);
            if (first != null && first.Twr < 1.0) twr = "<color=" + Warning + ">" + twr + "</color>";
            return "Δv " + (readout.Simulated ? EngineerFormat.Speed(readout.TotalDeltaV) : "--") + "    TWR " + twr + "    "
                   + EngineerFormat.Mass(readout.WetMass) + "    " + EngineerFormat.Count(readout.Parts) + " parts";
        }
    }
}
