using System.Collections.Generic;
using System;
using ReDefinition.Ui;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ReDefinition.Engineer
{
    // The planner's symbols (GameData/ReDefinition/Icons/Planner_*.png, drawn by
    // tools/make_icon.py) are white on clear. They are laid over a button of KSP's skin, so
    // that the button's own states and a UI theme stay as they are.
    internal static class PlannerIcons
    {
        // Where the symbols lie, without the symbol's name.
        private const string Path = "ReDefinition/Icons/Planner_";

        // The symbol of that name; null where it is not loaded.
        internal static Sprite Get(string name)
        {
            return IconSprites.Load(Path + name);
        }
    }

    // The colours the planner draws with: one per stage, in firing order, and the
    // red of a leg no stage pays for. Picked apart from green, which marks the
    // parts on the vessel, and from each other.
    internal static class PlannerColors
    {
        // The stages' colours, by their place in firing order, repeated past the eighth.
        private static readonly Color[] Stages =
        {
            new Color(0.36f, 0.61f, 1f), new Color(0.94f, 0.69f, 0.29f), new Color(0.69f, 0.49f, 1f),
            new Color(0.31f, 0.82f, 0.85f), new Color(1f, 0.49f, 0.71f), new Color(0.78f, 0.70f, 0.48f),
            new Color(0.54f, 0.63f, 0.72f), new Color(0.95f, 0.85f, 0.40f),
        };

        // What no stage covers; the timeline and dividers; what is left over.
        internal static readonly Color Short = new Color(0.90f, 0.28f, 0.30f);
        internal static readonly Color Line = new Color(0.55f, 0.55f, 0.58f);
        internal static readonly Color Spare = new Color(0.40f, 0.40f, 0.43f);
        // Behind a step: a little lighter than the panel, whatever theme it has.
        internal static readonly Color StepBackground = new Color(1f, 1f, 1f, 0.07f);

        // The colour of the stage at that place in firing order.
        internal static Color Stage(int order)
        {
            return Stages[((order % Stages.Length) + Stages.Length) % Stages.Length];
        }

        // The colour as rich text takes it: "#5c9cff".
        internal static string Hex(Color color)
        {
            return "#" + ColorUtility.ToHtmlStringRGB(color);
        }
    }

    // A background behind an element's rows, from `left` to `right`, once it is
    // created: an image under its children that takes no part in the layout. Of a
    // fixed width, since the scroll list stretches its rows under its bar.
    internal static class Tint
    {
        // `clickable`: the background answers the pointer, so that a click anywhere on
        // the element reaches its handler. `sprite`: a shape stretched over it, such
        // as rounded corners (Rounded). The image once created, to change its shape.
        internal static TintImage Behind(DialogGUIBase element, Color color, float left, float right, AfterCreate after,
                                         bool clickable = false, Sprite sprite = null)
        {
            TintImage made = new TintImage();
            after.Add(() =>
            {
                if (element.uiItem == null) return;
                GameObject tint = UiHygiene.Child("ReDefinitionTint", element.uiItem.transform, typeof(RectTransform));
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
                image.raycastTarget = clickable;
                if (sprite != null)
                {
                    image.sprite = sprite;
                    image.type = Image.Type.Sliced;
                }
                made.Image = image;
            });
            return made;
        }
    }

    // The image Tint.Behind made, once the element is created.
    internal sealed class TintImage
    {
        // Null until the element is created.
        public Image Image;

        // Its shape, set only when it changes.
        internal void Shape(Sprite sprite)
        {
            if (Image == null || Image.sprite == sprite) return;
            Image.sprite = sprite;
            Image.type = Image.Type.Sliced;
        }
    }

    // A white shape with slightly rounded corners, top, bottom or both, as KSP's crew
    // panel rounds its lists: 9-sliced, tinted by the image's colour.
    internal static class Rounded
    {
        // The texture's size and the corners' radius, in pixels; each of the four shapes, made once.
        private const int Size = 16;
        private const int Radius = 5;
        private static readonly Sprite[] made = new Sprite[4];

        // The shape with its top corners, its bottom corners, both or neither rounded.
        internal static Sprite Get(bool top, bool bottom)
        {
            int key = (top ? 1 : 0) | (bottom ? 2 : 0);
            if (made[key] == null) made[key] = Make(top, bottom);
            return made[key];
        }

        // Draws the shape into a texture of its own, its edges smoothed, sliced one pixel inside the corners.
        private static Sprite Make(bool top, bool bottom)
        {
            Texture2D texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                name = "ReDefinitionRounded", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
            };
            Color32[] pixels = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float alpha = 1f;
                    bool left = x < Radius, right = x >= Size - Radius, low = y < Radius, high = y >= Size - Radius;
                    if ((left || right) && ((low && bottom) || (high && top)))
                    {
                        float cx = left ? Radius : Size - Radius;
                        float cy = low ? Radius : Size - Radius;
                        float distance = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                        alpha = Mathf.Clamp01(Radius - distance + 0.5f);
                    }
                    pixels[y * Size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            float border = Radius + 1;
            return Sprite.Create(texture, new Rect(0f, 0f, Size, Size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                new Vector4(border, border, border, border));
        }
    }

    // What has to happen once the dialog elements are created: symbols laid on buttons, bars reached for their images.
    // Run once; an action that fails is logged and the others still run.
    internal sealed class AfterCreate
    {
        // The actions, in the order they were added.
        private readonly List<Action> actions = new List<Action>();

        // Adds an action for once the elements are created.
        internal void Add(Action action)
        {
            actions.Add(action);
        }

        // Runs every action, once.
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

    // The planner's own dialog elements: buttons with symbols, rows that answer a click, scroll lists as KSP's panels
    // show them, symbols and blocks in a row.
    internal static class PlannerWidgets
    {
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
                UiHygiene.MouseOnly(button);
                button.onClick.AddListener(() => click());
            });
        }

        // A scroll list as KSP's own panels show one. Its content is held at
        // the top edge and always as tall as its rows. KSP's DialogGUIContentSizer
        // (decompiled) sizes it only while it is taller than the view and then
        // leaves it at its last height. So a list grown short stays scrollable
        // into empty room below its rows (as TabScrollList). Its background, a
        // lighter box of KSP's scroll view prefab, is cleared, so that the panel
        // it stands in shows one background.
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
            ContentSizeFitter fitter = scroll.content.GetComponent<ContentSizeFitter>();
            if (fitter == null) fitter = scroll.content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.enabled = true;
            scroll.verticalNormalizedPosition = 1f;
        }

        // What a row may fill of a list this wide. KSP's scroll view insets its content 4 units
        // at each side, and its vertical bar, 16 wide, stands 3 units in from the right edge
        // (the prefab, read with UnityPy).
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

        // Lays the symbol over the item, centred, a little smaller than it, not taking the pointer.
        private static void AddIcon(GameObject item, string icon, float size, Color tint)
        {
            Sprite sprite = PlannerIcons.Get(icon);
            if (item == null || sprite == null) return;
            GameObject holder = UiHygiene.Child("ReDefinitionIcon", item.transform, typeof(RectTransform), typeof(Image),
                typeof(LayoutElement));
            RectTransform rect = (RectTransform)holder.transform;
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

    // Elements whose width is set after they are created: the room after each
    // step's bar, so that the figures beside the bars stand in one column.
    internal sealed class Sizer
    {
        // The elements it sizes, and the width set last; -1 until set.
        private readonly List<DialogGUIBase> elements = new List<DialogGUIBase>();
        private float width = -1f;

        // The width set before the elements were created is applied once they are.
        internal Sizer(AfterCreate after)
        {
            after.Add(() =>
            {
                float wanted = width;
                width = -1f;
                if (wanted > 0f) Set(wanted);
            });
        }

        // Adds an element to size, and hands it back.
        internal T Add<T>(T element) where T : DialogGUIBase
        {
            elements.Add(element);
            return element;
        }

        // Sets every element's width, where it changed by half a unit or more.
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
        // Each piece's image, width and colour; their images and layout elements once created; the bar's height.
        private readonly DialogGUIImage[] pieces;
        private readonly float[] widths;
        private readonly Color[] colors;
        private readonly RawImage[] images;
        private readonly LayoutElement[] layouts;
        private readonly float height;

        // The row of pieces, to put into the dialog.
        internal readonly DialogGUIHorizontalLayout Layout;

        // `count` pieces, each `height` high.
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
            Layout = new GatedRow(false, false, 1f, new RectOffset(), TextAnchor.MiddleLeft, pieces);
            after.Add(Reach);
        }

        // Once created: reaches every piece's image and layout element, and sets them.
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

        // One piece's colour and width, a unit at least, on its image and layout.
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

    // One entry of a Dropdown.
    internal sealed class Choice
    {
        // The entry's text, its tooltip, whether it is the current choice, and what picking it does.
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
        // An entry's height, in units.
        private const float RowHeight = 24f;
        // Kept free between the list and the screen's edges.
        private const float Margin = 8f;
        private static readonly Color Background = new Color(0.13f, 0.13f, 0.14f, 0.98f);
        private static GameObject blocker;
        private static GameObject list;
        private static object owner;
        // The entry the pointer is on, told when the list closes.
        private static Choice hovered;

        // Whether a list is folded out.
        internal static bool Open
        {
            get { return list != null; }
        }

        // Folds the list out under `anchor`, in place of one open: `by` is its owner, for Close(object); `width` the
        // entries' width. Nothing where there are no choices.
        internal static void Show(object by, DialogGUIBase anchor, List<Choice> choices, float width)
        {
            Close();
            if (anchor == null || anchor.uiItem == null || choices == null || choices.Count == 0) return;
            RectTransform at = anchor.uiItem.transform as RectTransform;
            Canvas canvas = at != null ? at.GetComponentInParent<Canvas>() : null;
            if (canvas == null) return;
            Transform root = canvas.rootCanvas.transform;
            owner = by;

            blocker = UiHygiene.Child("ReDefinitionDropdownBlocker", root, typeof(RectTransform));
            RectTransform cover = (RectTransform)blocker.transform;
            cover.anchorMin = Vector2.zero;
            cover.anchorMax = Vector2.one;
            cover.offsetMin = new Vector2(-10000f, -10000f);
            cover.offsetMax = new Vector2(10000f, 10000f);
            Image clear = blocker.AddComponent<Image>();
            clear.color = new Color(0f, 0f, 0f, 0f);
            Button away = blocker.AddComponent<Button>();
            away.transition = Selectable.Transition.None;
            UiHygiene.MouseOnly(away);
            away.onClick.AddListener(Close);

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

            list = UiHygiene.Child("ReDefinitionDropdown", root, typeof(RectTransform));
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
                button.tooltipText = TooltipText.Wrap(choice.Tooltip);
                if (choice.Hover != null) hovers.Add(new KeyValuePair<DialogGUIButton, Choice>(button, picked));
                rows.Add(choice.Indent > 0f
                    ? new GatedRow(rowWidth, RowHeight, 0f, new RectOffset((int)choice.Indent, 0, 0, 0),
                        TextAnchor.MiddleLeft, button)
                    : (DialogGUIBase)button);
            }
            DialogGUIVerticalLayout layout = new GatedColumn(rowWidth, -1f, 2f, new RectOffset(4, 4, 4, 4),
                TextAnchor.UpperLeft, rows.ToArray());
            DialogGUIBase content = layout;
            DialogGUIScrollList scroll = null;
            if (scrolls)
            {
                List<DialogGUIBase> sized = new List<DialogGUIBase>
                {
                    layout,
                };
                scroll = new DialogGUIScrollList(new Vector2(width + 8f, height), false, true,
                    new GatedColumn(rowWidth, -1f, 0f, new RectOffset(), TextAnchor.UpperLeft, sized.ToArray()));
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
            // The wheel over an entry scrolls a list taller than the screen, and
            // nothing here takes the keyboard's selection.
            ScrollThrough.Apply(list);
            UiHygiene.MouseOnly(list);
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

        // Closes the list, telling the entry the pointer is on that it left.
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
