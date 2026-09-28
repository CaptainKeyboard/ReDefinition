using System.Collections.Generic;
using System;
using ReDefinition.Window;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Engineer
{
    // The planner's symbols (GameData/ReDefinition/Icons/Planner_*.png, drawn by
    // tools/make_icon.py): white on clear, laid over a button of KSP's skin, so
    // that the button's own states and a UI theme stay as they are.
    internal static class PlannerIcons
    {
        private const string Path = "ReDefinition/Icons/Planner_";
        private static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();

        internal static Sprite Get(string name)
        {
            Sprite sprite;
            if (sprites.TryGetValue(name, out sprite) && sprite != null) return sprite;
            Texture2D texture = GameDatabase.Instance != null ? GameDatabase.Instance.GetTexture(Path + name, false) : null;
            sprite = texture != null
                ? Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f)
                : null;
            sprites[name] = sprite;
            return sprite;
        }
    }

    // The colours the planner draws with: one per stage, in firing order, and the
    // red of a leg no stage pays for. Picked apart from green, which marks the
    // parts on the vessel, and from each other.
    internal static class PlannerColors
    {
        private static readonly Color[] Stages =
        {
            new Color(0.36f, 0.61f, 1f), new Color(0.94f, 0.69f, 0.29f), new Color(0.69f, 0.49f, 1f),
            new Color(0.31f, 0.82f, 0.85f), new Color(1f, 0.49f, 0.71f), new Color(0.78f, 0.70f, 0.48f),
            new Color(0.54f, 0.63f, 0.72f), new Color(0.95f, 0.85f, 0.40f),
        };

        internal static readonly Color Short = new Color(0.90f, 0.28f, 0.30f);
        internal static readonly Color Line = new Color(0.55f, 0.55f, 0.58f);
        internal static readonly Color Spare = new Color(0.40f, 0.40f, 0.43f);
        // Behind a step: a little lighter than the panel, whatever theme it has.
        internal static readonly Color StepBackground = new Color(1f, 1f, 1f, 0.07f);

        internal static Color Stage(int order)
        {
            return Stages[((order % Stages.Length) + Stages.Length) % Stages.Length];
        }

        // A stage's colour as the vessel shows it: KSP's part highlighter draws
        // a soft glow, so the colour is pushed to full saturation and
        // brightness to read on the parts.
        internal static Color Vivid(Color color)
        {
            float h, s, v;
            Color.RGBToHSV(color, out h, out s, out v);
            return Color.HSVToRGB(h, Mathf.Min(1f, s * 1.5f + 0.15f), 1f);
        }

        internal static string Hex(Color color)
        {
            return "#" + ColorUtility.ToHtmlStringRGB(color);
        }
    }

    // What has to happen once the dialog elements are created: symbols laid on
    // buttons, bars reached for their images.
    // A background behind an element's rows, from `left` on, once it is created: an
    // image under its children that takes no part in the layout.
    internal static class Tint
    {
        internal static void Behind(DialogGUIBase element, Color color, float left, AfterCreate after)
        {
            after.Add(() =>
            {
                if (element.uiItem == null) return;
                GameObject tint = new GameObject("ReDefinitionTint", typeof(RectTransform));
                tint.transform.SetParent(element.uiItem.transform, false);
                tint.transform.SetAsFirstSibling();
                RectTransform rect = (RectTransform)tint.transform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = new Vector2(left, 0f);
                rect.offsetMax = Vector2.zero;
                tint.AddComponent<LayoutElement>().ignoreLayout = true;
                Image image = tint.AddComponent<Image>();
                image.color = color;
                image.raycastTarget = false;
            });
        }
    }

    internal sealed class AfterCreate
    {
        private readonly List<Action> actions = new List<Action>();

        internal void Add(Action action)
        {
            actions.Add(action);
        }

        internal void Run()
        {
            foreach (Action action in actions)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Core.Log.Tag + " A planner element could not be finished: " + e.Message);
                }
            }
            actions.Clear();
        }
    }

    internal static class PlannerWidgets
    {
        // A toggle button of KSP's skin with a symbol on it.
        internal static DialogGUIToggleButton IconToggle(string icon, Func<bool> set, Callback<bool> selected, float size,
                                                         string tip, AfterCreate after)
        {
            DialogGUIToggleButton toggle = new DialogGUIToggleButton(set, "", selected, size, size);
            toggle.tooltipText = tip;
            after.Add(() => AddIcon(toggle.uiItem, icon, size, Color.white));
            return toggle;
        }

        // A button of KSP's skin with a symbol on it.
        internal static DialogGUIButton IconButton(string icon, Callback click, float size, string tip, AfterCreate after)
        {
            DialogGUIButton button = new DialogGUIButton("", click, size, size, false);
            button.tooltipText = tip;
            after.Add(() => AddIcon(button.uiItem, icon, size, Color.white));
            return button;
        }

        // Makes a row answer a click anywhere on its pictures: the click passes
        // up from the image hit to the first handler above it (Unity's
        // ExecuteEvents).
        internal static void Clickable(DialogGUIBase element, Callback click, AfterCreate after)
        {
            after.Add(() =>
            {
                if (element.uiItem == null) return;
                Button button = element.uiItem.GetComponent<Button>();
                if (button == null) button = element.uiItem.AddComponent<Button>();
                button.transition = Selectable.Transition.None;
                button.onClick.AddListener(() => click());
            });
        }

        // A scroll list as KSP's own panels show one. Its content is held at
        // the top edge: KSP's content sizer lets it keep its last height, and
        // rows hidden after it was laid out shrank it about its middle,
        // leaving a gap above the first row. Its background, a lighter box of
        // KSP's scroll view prefab, is cleared, so that the panel it stands in
        // shows one background.
        internal static void Settle(DialogGUIScrollList list)
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
            scroll.verticalNormalizedPosition = 1f;
        }

        // KSP's scroll view insets its content 4 units at each side, and its
        // vertical bar, 16 wide, stands 3 units in from the right edge (the
        // prefab, read with UnityPy): what a row may fill of a list this wide.
        internal static float RowRoom(float listWidth)
        {
            return listWidth - 4f - 19f;
        }

        // A symbol alone, tinted, as a mark in a row.
        internal static DialogGUIImage Icon(string icon, float size, Color tint)
        {
            Sprite sprite = PlannerIcons.Get(icon);
            return new DialogGUIImage(new Vector2(size, size), Vector2.zero, tint,
                sprite != null ? sprite.texture : Texture2D.whiteTexture);
        }

        // A plain rectangle of one colour.
        internal static DialogGUIImage Block(float w, float h, Color color)
        {
            return new DialogGUIImage(new Vector2(w, h), Vector2.zero, color, Texture2D.whiteTexture);
        }

        private static void AddIcon(GameObject item, string icon, float size, Color tint)
        {
            Sprite sprite = PlannerIcons.Get(icon);
            if (item == null || sprite == null) return;
            GameObject holder = new GameObject("ReDefinitionIcon", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            RectTransform rect = (RectTransform)holder.transform;
            rect.SetParent(item.transform, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(size - 6f, size - 6f);
            holder.GetComponent<LayoutElement>().ignoreLayout = true;
            Image image = holder.GetComponent<Image>();
            image.sprite = sprite;
            image.color = tint;
            image.preserveAspect = true;
            image.raycastTarget = false;
            rect.SetAsLastSibling();
        }
    }

    // Elements whose width is set after they are created: the fuel plan's
    // columns, as wide as the panel's room over the legs there are.
    internal sealed class Sizer
    {
        private readonly List<DialogGUIBase> elements = new List<DialogGUIBase>();
        private float width = -1f;

        internal Sizer(AfterCreate after)
        {
            after.Add(() =>
            {
                float wanted = width;
                width = -1f;
                if (wanted > 0f) Set(wanted);
            });
        }

        internal T Add<T>(T element) where T : DialogGUIBase
        {
            elements.Add(element);
            return element;
        }

        internal void Set(float wanted)
        {
            if (Mathf.Abs(wanted - width) < 0.5f) return;
            width = wanted;
            foreach (DialogGUIBase element in elements)
            {
                if (element.uiItem == null) continue;
                LayoutElement layout = element.uiItem.GetComponent<LayoutElement>();
                if (layout != null)
                {
                    layout.minWidth = wanted;
                    layout.preferredWidth = wanted;
                }
                RectTransform rect = element.uiItem.transform as RectTransform;
                if (rect != null) rect.sizeDelta = new Vector2(wanted, rect.sizeDelta.y);
            }
        }
    }

    // A small symbol in the top right corner of a button, shown or hidden: the
    // pin on a cell of the fuel plan, clear of its centred figure.
    internal sealed class IconMark
    {
        private GameObject mark;
        private bool on;

        internal IconMark(DialogGUIBase element, string icon, float size, AfterCreate after)
        {
            after.Add(() =>
            {
                Sprite sprite = PlannerIcons.Get(icon);
                if (element.uiItem == null || sprite == null) return;
                mark = new GameObject("ReDefinitionMark", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
                RectTransform rect = (RectTransform)mark.transform;
                rect.SetParent(element.uiItem.transform, false);
                rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(1f, 1f);
                rect.anchoredPosition = new Vector2(-1f, -1f);
                rect.sizeDelta = new Vector2(size, size);
                mark.GetComponent<LayoutElement>().ignoreLayout = true;
                Image image = mark.GetComponent<Image>();
                image.sprite = sprite;
                image.preserveAspect = true;
                image.raycastTarget = false;
                mark.SetActive(on);
            });
        }

        internal void Set(bool show)
        {
            on = show;
            if (mark != null && mark.activeSelf != show) mark.SetActive(show);
        }
    }

    // A bar of coloured pieces side by side, each as wide as it is given: a
    // leg's stages, or the whole route's. A fixed number of pieces, the unused
    // ones hidden.
    internal sealed class SegmentBar
    {
        private readonly DialogGUIImage[] pieces;
        private readonly float[] widths;
        private readonly Color[] colors;
        private readonly RawImage[] images;
        private readonly LayoutElement[] layouts;
        private readonly float height;

        internal readonly DialogGUIHorizontalLayout Layout;

        internal SegmentBar(int count, float height, AfterCreate after)
        {
            this.height = height;
            pieces = new DialogGUIImage[count];
            widths = new float[count];
            colors = new Color[count];
            images = new RawImage[count];
            layouts = new LayoutElement[count];
            for (int i = 0; i < count; i++)
            {
                int at = i;
                pieces[i] = new DialogGUIImage(new Vector2(1f, height), Vector2.zero, Color.white, Texture2D.whiteTexture);
                pieces[i].OptionEnabledCondition = () => widths[at] >= 1f;
            }
            Layout = new DialogGUIHorizontalLayout(false, false, 1f, new RectOffset(), TextAnchor.MiddleLeft, pieces);
            after.Add(Reach);
        }

        private void Reach()
        {
            for (int i = 0; i < pieces.Length; i++)
            {
                if (pieces[i].uiItem == null) continue;
                images[i] = pieces[i].uiItem.GetComponent<RawImage>();
                layouts[i] = pieces[i].uiItem.GetComponent<LayoutElement>();
                Apply(i);
            }
        }

        // Pieces as (width, colour); the rest are hidden.
        internal void Set(IList<KeyValuePair<float, Color>> set)
        {
            for (int i = 0; i < pieces.Length; i++)
            {
                float width = i < set.Count ? Mathf.Max(0f, set[i].Key) : 0f;
                Color color = i < set.Count ? set[i].Value : Color.clear;
                if (Mathf.Abs(width - widths[i]) < 0.25f && color == colors[i]) continue;
                widths[i] = width;
                colors[i] = color;
                Apply(i);
            }
        }

        private void Apply(int i)
        {
            if (images[i] != null) images[i].color = colors[i];
            float width = Mathf.Max(1f, widths[i]);
            if (layouts[i] != null)
            {
                layouts[i].minWidth = width;
                layouts[i].preferredWidth = width;
                layouts[i].flexibleWidth = 0f;
            }
            if (pieces[i].uiItem != null)
                ((RectTransform)pieces[i].uiItem.transform).sizeDelta = new Vector2(width, height);
        }
    }

    // The list of bodies to choose a stop's body from: the star, each planet and
    // under it its moons, in a small window in the middle of the screen. KSP
    // places a dialog off the middle through a formula that fails at a UI scale
    // of 200 % (PopupDialog.OnRectTransformDimensionsChange, decompiled).
    internal static class BodyPicker
    {
        private static PopupDialog open;
        private static object owner;

        internal static void Show(object by, PlannerBodies bodies, int current, Callback<int> chosen)
        {
            Close();
            owner = by;
            if (bodies == null || bodies.Order.Count == 0) return;
            UISkinDef skin = UISkinManager.GetSkin("MiniSettingsSkin") ?? HighLogic.UISkin;
            const float width = 200f;
            const float rowHeight = 22f;
            float listWidth = width - 20f;
            float rowWidth = PlannerWidgets.RowRoom(listWidth) - 8f;

            List<DialogGUIBase> rows = new List<DialogGUIBase>
            {
                new DialogGUIContentSizer(ContentSizeFitter.FitMode.Unconstrained, ContentSizeFitter.FitMode.PreferredSize, true),
            };
            foreach (int body in bodies.Order)
            {
                int picked = body;
                int depth = Depth(bodies, body);
                string name = bodies.Bodies[body].Name;
                DialogGUIButton button = new DialogGUIButton(body == current ? "<b>" + name + "</b>" : name,
                    () => { Close(); chosen(picked); }, rowWidth - depth * 14f, rowHeight, false);
                rows.Add(new DialogGUIHorizontalLayout(rowWidth, rowHeight, 0f, new RectOffset(depth * 14, 0, 0, 0),
                    TextAnchor.MiddleLeft, button));
            }
            float height = Mathf.Min(440f, bodies.Order.Count * (rowHeight + 2f) + 16f);
            DialogGUIVerticalLayout list = new DialogGUIVerticalLayout(rowWidth, -1f, 2f, new RectOffset(4, 4, 4, 4),
                TextAnchor.UpperLeft, rows.ToArray());

            DialogGUIScrollList scroll = new DialogGUIScrollList(new Vector2(listWidth, height), false, true, list);
            MultiOptionDialog dialog = new MultiOptionDialog("ReDefinitionPlannerBodies", "", "Body", skin,
                new Rect(0.5f, 0.5f, width, height + 60f), scroll,
                new DialogGUIButton(KSP.Localization.Localizer.Format("#autoLOC_174783"), Close, listWidth, rowHeight, false));
            open = UnityMouseEvents.Shield(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), dialog,
                false, skin, false));
            PlannerWidgets.Settle(scroll);
        }

        internal static void Close()
        {
            if (open != null) open.Dismiss();
            open = null;
            owner = null;
        }

        // Closes the list only if `by` opened it.
        internal static void Close(object by)
        {
            if (owner == by) Close();
        }

        private static int Depth(PlannerBodies bodies, int body)
        {
            int depth = 0;
            for (int at = bodies.Bodies[body].Parent; at >= 0 && depth < 8; at = bodies.Bodies[at].Parent) depth++;
            return depth;
        }
    }

    // One entry of a ChoiceList.
    internal sealed class Choice
    {
        public string Label;
        public string Tooltip;
        public bool Current;
        public Callback Pick;
    }

    // A small list to pick one of a few choices from, in the middle of the screen,
    // as the list of bodies (BodyPicker); the current one in bold.
    internal static class ChoiceList
    {
        private static PopupDialog open;
        private static object owner;

        internal static void Show(object by, string title, List<Choice> choices)
        {
            Close();
            owner = by;
            if (choices == null || choices.Count == 0) return;
            UISkinDef skin = UISkinManager.GetSkin("MiniSettingsSkin") ?? HighLogic.UISkin;
            const float width = 200f;
            const float rowHeight = 24f;
            float rowWidth = width - 20f;
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            foreach (Choice choice in choices)
            {
                Choice picked = choice;
                DialogGUIButton button = new DialogGUIButton(choice.Current ? "<b>" + choice.Label + "</b>" : choice.Label,
                    () => { Close(); picked.Pick(); }, rowWidth, rowHeight, false);
                button.tooltipText = choice.Tooltip;
                rows.Add(button);
            }
            rows.Add(new DialogGUIButton(KSP.Localization.Localizer.Format("#autoLOC_174783"), Close, rowWidth, rowHeight, false));
            DialogGUIVerticalLayout list = new DialogGUIVerticalLayout(rowWidth, -1f, 4f, new RectOffset(0, 0, 0, 0),
                TextAnchor.UpperLeft, rows.ToArray());
            MultiOptionDialog dialog = new MultiOptionDialog("ReDefinitionPlannerChoice", "", title, skin,
                new Rect(0.5f, 0.5f, width, rows.Count * (rowHeight + 4f) + 50f), list);
            open = UnityMouseEvents.Shield(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), dialog,
                false, skin, false));
            ReDefinition.Window.DialogTooltips.Attach(new DialogGUIBase[] { list });
        }

        internal static void Close()
        {
            if (open != null) open.Dismiss();
            open = null;
            owner = null;
        }

        // Closes the list only if `by` opened it.
        internal static void Close(object by)
        {
            if (owner == by) Close();
        }
    }
}
