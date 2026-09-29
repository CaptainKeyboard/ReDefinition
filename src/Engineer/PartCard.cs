using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Engineer
{
    // A small card beside the pointer with the part it points at on the vessel:
    // mass, cost, engine,
    // reaction wheel, decoupler, parachute, antenna and resources (PartDetails).
    // It shows after the pointer rests on a part, not over a window, and not
    // while a part hangs on the mouse.
    internal sealed class PartCard
    {
        // Wide enough that "Reaction wheel, pitch / yaw / roll" and its value
        // stand side by side.
        private const float Width = 380f;
        private const float Delay = 0.35f;
        private const float Interval = 0.25f;
        private const string Dim = "#9a9a9a";

        private GameObject host;
        private DialogGUIBase root;
        private Part part;
        private float since;
        private float nextRead;
        private string text = "";

        internal static PartCard Create(RectTransform parent, UISkinDef skin)
        {
            PartCard card = new PartCard();
            card.host = new GameObject("ReDefinitionPartCard", typeof(RectTransform), typeof(Image));
            RectTransform rect = (RectTransform)card.host.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 0f);
            rect.pivot = new Vector2(0f, 1f);
            Image background = card.host.GetComponent<Image>();
            background.raycastTarget = false;
            UIStyleState style = skin.window != null ? skin.window.normal : null;
            if (style != null && style.background != null)
            {
                background.sprite = style.background;
                background.type = Image.Type.Sliced;
            }
            else
            {
                background.color = new Color(0.1f, 0.11f, 0.12f, 0.95f);
            }
            ContentSizeFitter fitter = card.host.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            VerticalLayoutGroup layout = card.host.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 6, 6);
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;

            DialogGUILabel label = new DialogGUILabel(() => card.text, Width - 16f);
            card.root = new DialogGUIVerticalLayout(Width - 16f, -1f, 0f, new RectOffset(), TextAnchor.UpperLeft, label);
            Stack<Transform> layouts = new Stack<Transform>();
            layouts.Push(card.host.transform);
            card.root.Create(ref layouts, skin);
            foreach (Graphic graphic in card.host.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
            card.host.SetActive(false);
            return card;
        }

        internal void Destroy()
        {
            if (host != null) Object.Destroy(host);
            host = null;
        }

        internal void Hide()
        {
            part = null;
            if (host != null && host.activeSelf) host.SetActive(false);
        }

        // KSP's hovered part is a raycast from the editor's camera that knows
        // nothing of the UI (Mouse.CheckHoveredPart, decompiled): over a window it
        // would find a part behind it.
        internal void Update(bool wanted)
        {
            if (host == null) return;
            UnityEngine.EventSystems.EventSystem events = UnityEngine.EventSystems.EventSystem.current;
            bool overUi = events != null && events.IsPointerOverGameObject();
            bool holding = EditorLogic.SelectedPart != null;
            Part hovered = wanted && !overUi && !holding ? Mouse.HoveredPart : null;
            if (hovered == null)
            {
                Hide();
                return;
            }
            if (hovered != part)
            {
                part = hovered;
                since = Time.unscaledTime;
                nextRead = 0f;
            }
            if (Time.unscaledTime - since < Delay)
            {
                if (host.activeSelf) host.SetActive(false);
                return;
            }
            if (Time.unscaledTime >= nextRead)
            {
                nextRead = Time.unscaledTime + Interval;
                text = Text(part);
            }
            if (!host.activeSelf) host.SetActive(true);
            Place();
            root.Update();
        }

        private void Place()
        {
            RectTransform rect = (RectTransform)host.transform;
            RectTransform parent = rect.parent as RectTransform;
            Canvas canvas = host.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            Vector2 local;
            if (parent == null
                || !RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, Input.mousePosition, camera, out local)) return;
            Vector2 position = local - parent.rect.min + new Vector2(24f, -16f);
            float width = rect.rect.width;
            float height = rect.rect.height;
            if (position.x + width > parent.rect.width) position.x -= width + 48f;
            if (position.y - height < 0f) position.y = height;
            rect.anchoredPosition = position;
        }

        private static string Text(Part part)
        {
            StringBuilder text = new StringBuilder("<b>" + (part.partInfo != null ? part.partInfo.title : part.name) + "</b>");
            foreach (PartDetails.Line line in PartDetails.Of(part))
            {
                text.Append('\n');
                string name = line.Detail ? "    " + line.Name : line.Name;
                if (line.Value.Length == 0) text.Append("<color=" + Dim + ">").Append(name).Append("</color>");
                else text.Append(name).Append("<pos=52%>").Append(line.Value);
            }
            return text.ToString();
        }
    }
}
