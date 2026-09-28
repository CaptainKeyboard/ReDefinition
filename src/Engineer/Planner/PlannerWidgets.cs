using System.Collections.Generic;
using System;
using ReDefinition.Window;
using UnityEngine;
using UnityEngine.EventSystems;
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

        internal static string Hex(Color color)
        {
            return "#" + ColorUtility.ToHtmlStringRGB(color);
        }
    }

    // What has to happen once the dialog elements are created: symbols laid on
    // buttons, bars reached for their images.
    // A background behind an element's rows, from `left` to `right`, once it is
    // created: an image under its children that takes no part in the layout. Of a
    // fixed width, since the scroll list stretches its rows under its bar.
    internal static class Tint
    {
        internal static void Behind(DialogGUIBase element, Color color, float left, float right, AfterCreate after)
        {
            after.Add(() =>
            {
                if (element.uiItem == null) return;
                GameObject tint = new GameObject("ReDefinitionTint", typeof(RectTransform));
                tint.transform.SetParent(element.uiItem.transform, false);
                tint.transform.SetAsFirstSibling();
                RectTransform rect = (RectTransform)tint.transform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 0.5f);
                rect.anchoredPosition = new Vector2(left, 0f);
                rect.sizeDelta = new Vector2(right - left, 0f);
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
            return IconButton(icon, click, size, size, size, tip, after);
        }

        // A button of `width` by `height` with a symbol of `symbol` units on it.
        internal static DialogGUIButton IconButton(string icon, Callback click, float width, float height, float symbol, string tip,
                                                   AfterCreate after)
        {
            DialogGUIButton button = new DialogGUIButton("", click, width, height, false);
            button.tooltipText = tip;
            after.Add(() => AddIcon(button.uiItem, icon, symbol, Color.white));
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

    // One entry of a Dropdown.
    internal sealed class Choice
    {
        public string Label;
        public string Tooltip;
        public bool Current;
        public Callback Pick;
        // Room left of it, for a list with levels: a moon under its planet.
        public float Indent;
        // Told when the pointer comes onto the entry and leaves it, or the list closes.
        public System.Action<bool> Hover;
    }

    // A list that folds out under a button, to pick one of a few choices; the
    // current one in bold. Built from KSP's dialog elements on the button's own
    // canvas, over everything else there, with a clear layer behind it that closes it
    // on a click anywhere else, so that the click does not reach the editor.
    internal static class Dropdown
    {
        private const float RowHeight = 24f;
        // Kept free between the list and the screen's edges.
        private const float Margin = 8f;
        private static readonly Color Background = new Color(0.13f, 0.13f, 0.14f, 0.98f);
        private static GameObject blocker;
        private static GameObject list;
        private static object owner;
        // The entry the pointer is on, told when the list closes.
        private static Choice hovered;

        internal static bool Open
        {
            get { return list != null; }
        }

        internal static void Show(object by, DialogGUIBase anchor, List<Choice> choices, float width)
        {
            Close();
            if (anchor == null || anchor.uiItem == null || choices == null || choices.Count == 0) return;
            RectTransform at = anchor.uiItem.transform as RectTransform;
            Canvas canvas = at != null ? at.GetComponentInParent<Canvas>() : null;
            if (canvas == null) return;
            Transform root = canvas.rootCanvas.transform;
            owner = by;

            blocker = new GameObject("ReDefinitionDropdownBlocker", typeof(RectTransform));
            blocker.transform.SetParent(root, false);
            RectTransform cover = (RectTransform)blocker.transform;
            cover.anchorMin = Vector2.zero;
            cover.anchorMax = Vector2.one;
            cover.offsetMin = new Vector2(-10000f, -10000f);
            cover.offsetMax = new Vector2(10000f, 10000f);
            Image clear = blocker.AddComponent<Image>();
            clear.color = new Color(0f, 0f, 0f, 0f);
            blocker.AddComponent<Button>().onClick.AddListener(Close);

            // Where it fits: under the button, over it, or else moved onto the screen;
            // with a scroll bar where it is taller than the screen. In the root
            // canvas's own units.
            RectTransform rootRect = (RectTransform)root;
            Rect screen = rootRect.rect;
            Vector3[] corners = new Vector3[4];
            at.GetWorldCorners(corners);
            Vector2 under = rootRect.InverseTransformPoint(corners[0]);
            Vector2 over = rootRect.InverseTransformPoint(corners[1]);
            float height = choices.Count * RowHeight + (choices.Count - 1) * 2f + 8f;
            float highest = screen.height - 2f * Margin;
            bool scrolls = height > highest;
            if (scrolls) height = highest;
            float top;
            if (under.y - height >= screen.yMin + Margin) top = under.y;
            else if (over.y + height <= screen.yMax - Margin) top = over.y + height;
            else top = Mathf.Min(screen.yMax - Margin, Mathf.Max(screen.yMin + Margin + height, under.y));
            float left = Mathf.Min(under.x, screen.xMax - Margin - width - 8f);

            list = new GameObject("ReDefinitionDropdown", typeof(RectTransform));
            list.transform.SetParent(root, false);
            RectTransform box = (RectTransform)list.transform;
            box.pivot = new Vector2(0f, 1f);
            box.anchorMin = box.anchorMax = new Vector2(0.5f, 0.5f);
            box.sizeDelta = new Vector2(width + 8f, height);
            box.localPosition = new Vector3(left, top, 0f);
            list.AddComponent<Image>().color = Background;

            // A scroll list's bar takes room from the rows (PlannerWidgets.RowRoom).
            float rowWidth = scrolls ? PlannerWidgets.RowRoom(width + 8f) - 8f : width;
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            List<KeyValuePair<DialogGUIButton, Choice>> hovers = new List<KeyValuePair<DialogGUIButton, Choice>>();
            foreach (Choice choice in choices)
            {
                Choice picked = choice;
                DialogGUIButton button = new DialogGUIButton(choice.Current ? "<b>" + choice.Label + "</b>" : choice.Label, () =>
                {
                    Close();
                    picked.Pick();
                }, rowWidth - choice.Indent, RowHeight, false);
                button.tooltipText = choice.Tooltip;
                if (choice.Hover != null) hovers.Add(new KeyValuePair<DialogGUIButton, Choice>(button, picked));
                rows.Add(choice.Indent > 0f
                    ? new DialogGUIHorizontalLayout(rowWidth, RowHeight, 0f, new RectOffset((int)choice.Indent, 0, 0, 0),
                        TextAnchor.MiddleLeft, button)
                    : (DialogGUIBase)button);
            }
            DialogGUIVerticalLayout layout = new DialogGUIVerticalLayout(rowWidth, -1f, 2f, new RectOffset(4, 4, 4, 4),
                TextAnchor.UpperLeft, rows.ToArray());
            DialogGUIBase content = layout;
            DialogGUIScrollList scroll = null;
            if (scrolls)
            {
                List<DialogGUIBase> sized = new List<DialogGUIBase>
                {
                    new DialogGUIContentSizer(ContentSizeFitter.FitMode.Unconstrained, ContentSizeFitter.FitMode.PreferredSize, true),
                    layout,
                };
                scroll = new DialogGUIScrollList(new Vector2(width + 8f, height), false, true,
                    new DialogGUIVerticalLayout(rowWidth, -1f, 0f, new RectOffset(), TextAnchor.UpperLeft, sized.ToArray()));
                content = scroll;
            }
            Stack<Transform> layouts = new Stack<Transform>();
            layouts.Push(list.transform);
            UISkinDef skin = UISkinManager.GetSkin("MiniSettingsSkin") ?? HighLogic.UISkin;
            content.Create(ref layouts, skin);
            RectTransform inner = content.uiItem != null ? content.uiItem.transform as RectTransform : null;
            if (inner != null)
            {
                inner.anchorMin = Vector2.zero;
                inner.anchorMax = Vector2.one;
                inner.offsetMin = Vector2.zero;
                inner.offsetMax = Vector2.zero;
            }
            if (scroll != null) PlannerWidgets.Settle(scroll);
            foreach (KeyValuePair<DialogGUIButton, Choice> pair in hovers) Hovering(pair.Key, pair.Value);
            DialogTooltips.Attach(new DialogGUIBase[] { layout });
        }

        // The entry's Hover, from the pointer entering and leaving its button.
        private static void Hovering(DialogGUIButton button, Choice choice)
        {
            if (button.uiItem == null) return;
            EventTrigger trigger = button.uiItem.GetComponent<EventTrigger>() ?? button.uiItem.AddComponent<EventTrigger>();
            EventTrigger.Entry enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enter.callback.AddListener(data =>
            {
                hovered = choice;
                choice.Hover(true);
            });
            EventTrigger.Entry exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            exit.callback.AddListener(data =>
            {
                if (hovered == choice) hovered = null;
                choice.Hover(false);
            });
            trigger.triggers.Add(enter);
            trigger.triggers.Add(exit);
        }

        internal static void Close()
        {
            if (hovered != null && hovered.Hover != null)
            {
                Choice was = hovered;
                hovered = null;
                was.Hover(false);
            }
            if (list != null) UnityEngine.Object.Destroy(list);
            if (blocker != null) UnityEngine.Object.Destroy(blocker);
            list = null;
            blocker = null;
            owner = null;
        }

        // Closes the list only if `by` opened it.
        internal static void Close(object by)
        {
            if (owner == by) Close();
        }
    }
}
