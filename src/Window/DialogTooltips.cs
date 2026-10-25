using System.Collections.Generic;
using System.Reflection;
using KSP.UI.TooltipTypes;
using UnityEngine;

namespace ReDefinition.Window
{
    // Tooltips for KSP's dialog elements. A DialogGUIBase shows its tooltipText only
    // through a TooltipController_Text on its object, which it looks for when it is
    // created and then keeps current (DialogGUIBase.Create, Update; decompiled). KSP's
    // button prefab carries none: UIButtonPrefab has an Image, a Button and an
    // EventTrigger (sharedassets0.assets, read with UnityPy). So after a dialog is
    // created, every element with a tooltip gets the controller with the game's own
    // prefab, as the toolbar button's does (ToolbarButton.SetTooltip), and the element
    // is handed it, so that a changing tooltip stays current. A label's text does
    // not answer the pointer (UITextPrefab: raycastTarget off), so the pointer never
    // enters it and its tooltip would not show: a label with a tooltip is made to
    // answer. A click on it still goes to the first handler above it.
    internal static class DialogTooltips
    {
        // DialogGUIBase's private controller field, which keeps a changing tooltip current, and the game's tooltip prefab.
        private static readonly FieldInfo ControllerField =
            typeof(DialogGUIBase).GetField("toolTip", BindingFlags.Instance | BindingFlags.NonPublic);
        private static Tooltip_Text prefab;

        // Gives every element with a tooltip, its children included, the game's tooltip controller.
        internal static void Attach(IEnumerable<DialogGUIBase> elements)
        {
            if (elements == null) return;
            if (prefab == null) prefab = AssetBase.GetPrefab<Tooltip_Text>("Tooltip_Text");
            if (prefab == null) return;
            foreach (DialogGUIBase element in elements) Attach(element);
        }

        // For an element KSP creates itself, in one of its own dialogs: attached
        // from its update, once its object stands.
        internal static void AttachWhenMade(DialogGUIBase element)
        {
            if (element == null) return;
            Callback before = element.OnUpdate;
            bool done = false;
            element.OnUpdate = () =>
            {
                if (before != null) before();
                if (done || element.uiItem == null) return;
                done = true;
                Attach(new[] { element });
            };
        }

        // One element and its children, a scroll list's layout among them.
        private static void Attach(DialogGUIBase element)
        {
            if (element == null) return;
            if (!string.IsNullOrEmpty(element.tooltipText) && element.uiItem != null)
            {
                TooltipController_Text controller = element.uiItem.GetComponent<TooltipController_Text>()
                                                    ?? element.uiItem.AddComponent<TooltipController_Text>();
                controller.prefab = prefab;
                controller.textString = element.tooltipText;
                if (element is DialogGUILabel)
                {
                    UnityEngine.UI.Graphic text = element.uiItem.GetComponent<UnityEngine.UI.Graphic>();
                    if (text != null) text.raycastTarget = true;
                }
                if (ControllerField != null) ControllerField.SetValue(element, controller);
            }
            foreach (DialogGUIBase child in element.children) Attach(child);
            DialogGUIScrollList scroll = element as DialogGUIScrollList;
            if (scroll != null && scroll.layout != null) Attach(scroll.layout);
        }
    }
}
