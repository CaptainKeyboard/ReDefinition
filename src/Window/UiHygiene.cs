using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ReDefinition.Window
{
    // What every control ReDefinition makes itself has to get right, in one place.
    //
    // The keyboard. KSP's event system sends navigation events, with Submit on
    // Return and on Space and the directions on the arrow keys and W, A, S, D
    // (InputManager, globalgamemanagers; EventSystem.sendNavigationEvents,
    // sharedassets7). A Selectable is selected by a click unless its navigation is
    // None (Selectable.OnPointerDown; UnityEngine.UI.dll, decompiled), and what is
    // selected is pressed again by Space or Return and hands the selection on at a
    // direction key: a button clicked in flight would be pressed again by the
    // staging key. KSP's own dialog prefabs have navigation None (UIButtonPrefab,
    // UITogglePrefab, UISliderPrefab; sharedassets0); a Button added at run time
    // has Automatic. So every Selectable made or added here is set to None
    // (MouseOnly). A text field selects itself at a click whatever its navigation
    // (TMP_InputField.OnPointerDown) and stays selected after its edit ends, when
    // Space or Return would open it again: it is let go then (Release).
    //
    // The layer. A GameObject made at run time is on layer 0; KSP's UI is on its
    // UI layer (Child).
    //
    // The tooltip. KSP's own: a TooltipController_Text on the object with the
    // game's Tooltip_Text prefab (Tooltip). On the HUD and in the editors its text
    // is as KSP's there: a name or two or three words in title case, "Toggle SAS",
    // "Warp to Sunrise". Rows of a settings dialog explain in sentences, as KSP's
    // settings do.
    internal static class UiHygiene
    {
        internal static void MouseOnly(Selectable selectable)
        {
            if (selectable == null) return;
            Navigation navigation = selectable.navigation;
            if (navigation.mode == Navigation.Mode.None) return;
            navigation.mode = Navigation.Mode.None;
            selectable.navigation = navigation;
        }

        // Every Selectable under `root`, itself included: buttons, toggles,
        // sliders, scroll bars and text fields.
        internal static void MouseOnly(GameObject root)
        {
            if (root == null) return;
            foreach (Selectable selectable in root.GetComponentsInChildren<Selectable>(true)) MouseOnly(selectable);
        }

        // KSP's tooltip on `target`, which has to answer the pointer itself or hold
        // something that does. Null, and no tooltip, where the game's prefab is
        // not there.
        internal static KSP.UI.TooltipTypes.TooltipController_Text Tooltip(GameObject target, string text)
        {
            if (target == null) return null;
            try
            {
                KSP.UI.TooltipTypes.Tooltip_Text prefab = AssetBase.GetPrefab<KSP.UI.TooltipTypes.Tooltip_Text>("Tooltip_Text");
                if (prefab == null) return null;
                KSP.UI.TooltipTypes.TooltipController_Text tooltip =
                    target.GetComponent<KSP.UI.TooltipTypes.TooltipController_Text>()
                    ?? target.AddComponent<KSP.UI.TooltipTypes.TooltipController_Text>();
                tooltip.prefab = prefab;
                tooltip.textString = text;
                return tooltip;
            }
            catch (Exception e)
            {
                ReDefinition.Core.CompatibilityLog.Warn("tooltip", "A control has no tooltip ("
                                                                   + ReDefinition.Core.CompatibilityLog.Reason(e) + ").");
                return null;
            }
        }

        // A new object under `parent`, on its layer, placed in its space.
        internal static GameObject Child(string name, Transform parent, params Type[] components)
        {
            GameObject child = new GameObject(name, components);
            child.layer = parent.gameObject.layer;
            child.transform.SetParent(parent, false);
            return child;
        }

        // A dialog element goes grey or live as its OptionInteractableCondition
        // turns (DialogGUIBase.Update; decompiled): KSP notes the new state and
        // sets it on the element's Selectables -- found without the inactive ones,
        // so that an element hidden at that moment (another page, a folded group)
        // is noted as changed, nothing is set, and it is never set again. Each
        // condition is wrapped: while the element is hidden it answers what it
        // answered last, and the change is seen once the element shows.
        internal static void DeferInteractable(System.Collections.Generic.IEnumerable<DialogGUIBase> elements)
        {
            if (elements == null) return;
            foreach (DialogGUIBase element in elements)
            {
                if (element == null) continue;
                // Not KSP's own default, which is always true.
                if (element.OptionInteractableCondition != null && !KspOwn(element.OptionInteractableCondition))
                {
                    DialogGUIBase held = element;
                    Func<bool> condition = element.OptionInteractableCondition;
                    bool last = true;
                    element.OptionInteractableCondition = () =>
                        held.uiItem != null && !held.uiItem.activeInHierarchy ? last : (last = condition());
                }
                DeferInteractable(element.children);
                DialogGUIScrollList scroll = element as DialogGUIScrollList;
                if (scroll != null && scroll.layout != null) DeferInteractable(new DialogGUIBase[] { scroll.layout });
            }
        }

        private static bool KspOwn(Func<bool> condition)
        {
            Type owner = condition.Method.DeclaringType;
            while (owner != null)
            {
                if (owner.Assembly == typeof(DialogGUIBase).Assembly) return true;
                owner = owner.DeclaringType;
            }
            return false;
        }

        // Whether a text field of KSP's UI has the keyboard and is being typed
        // into. While it is, a key typed is text, not a hotkey. A mod's IMGUI
        // field is not asked: IMGUI keeps its keyboard control on a field long
        // after the typing (until another of its controls is clicked), and the
        // hotkeys would stay dead for as long.
        internal static bool Typing
        {
            get
            {
                EventSystem events = EventSystem.current;
                GameObject selected = events != null ? events.currentSelectedGameObject : null;
                if (selected == null) return false;
                TMP_InputField field = selected.GetComponent<TMP_InputField>();
                if (field != null) return field.isFocused;
                InputField plain = selected.GetComponent<InputField>();
                return plain != null && plain.isFocused;
            }
        }

        // Asked every frame for a dialog: lets go of its text field once an edit
        // in it has ended. Only a field that was seen being typed into: one just
        // clicked is selected a frame before it takes the keyboard, and letting
        // go of it then would keep it from ever taking it.
        private static TMP_InputField typed;

        internal static void ReleaseEnded(GameObject root)
        {
            EventSystem events = EventSystem.current;
            GameObject selected = events != null ? events.currentSelectedGameObject : null;
            if (typed != null && (selected == null || selected != typed.gameObject)) typed = null;
            if (selected == null || root == null || !selected.transform.IsChildOf(root.transform)) return;
            TMP_InputField field = selected.GetComponent<TMP_InputField>();
            if (field == null) return;
            if (field.isFocused)
            {
                typed = field;
                return;
            }
            if (typed != field) return;
            typed = null;
            events.SetSelectedGameObject(null);
        }

        // Lets go of a text field under `root` that is still selected though its
        // edit has ended: for the moment the typing is seen to end.
        internal static void Release(GameObject root)
        {
            EventSystem events = EventSystem.current;
            GameObject selected = events != null ? events.currentSelectedGameObject : null;
            if (selected == null || root == null || !selected.transform.IsChildOf(root.transform)) return;
            TMP_InputField field = selected.GetComponent<TMP_InputField>();
            if (field != null && !field.isFocused) events.SetSelectedGameObject(null);
        }
    }

    // Passes the mouse wheel on to what is above: on a text field, which takes the
    // wheel and, with one line, does nothing with it (TMP_InputField.OnScroll), so
    // that the list it stands in scrolls (ScrollThrough).
    public class ScrollForwarder : MonoBehaviour, IScrollHandler
    {
        public void OnScroll(PointerEventData data)
        {
            Transform above = transform.parent;
            if (above == null) return;
            ExecuteEvents.ExecuteHierarchy(above.gameObject, data,
                (IScrollHandler handler, BaseEventData sent) => handler.OnScroll((PointerEventData)sent));
        }
    }
}
