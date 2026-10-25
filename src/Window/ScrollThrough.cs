using System;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ReDefinition.Window
{
    // The mouse wheel over a button still scrolls the list under it.
    //
    // KSP's dialog button prefab carries an EventTrigger (UIButtonPrefab in
    // sharedassets0), and Unity's EventTrigger implements IScrollHandler: the
    // input module sends a scroll to the first object above the cursor that has
    // such a handler, so the button takes the wheel and the scroll list below it
    // never sees it. KSP's own answer is EventTriggerForwarder, which adds a
    // Scroll entry that passes the event up the hierarchy; the same entry is
    // added here to every EventTrigger of the window that has none. A text field
    // takes the wheel too and drops it (TMP_InputField.OnScroll with one line):
    // each gets a ScrollForwarder beside it, which the input module calls along
    // with the field.
    internal static class ScrollThrough
    {
        // Adds the Scroll entry to every EventTrigger of the window and a ScrollForwarder to every text field.
        internal static void Apply(GameObject window)
        {
            try
            {
                if (window == null) return;
                foreach (EventTrigger trigger in window.GetComponentsInChildren<EventTrigger>(true))
                {
                    if (trigger.triggers == null) trigger.triggers = new System.Collections.Generic.List<EventTrigger.Entry>();
                    if (trigger.triggers.Exists(entry => entry != null && entry.eventID == EventTriggerType.Scroll)) continue;

                    GameObject self = trigger.gameObject;
                    EventTrigger.Entry scroll = new EventTrigger.Entry { eventID = EventTriggerType.Scroll };
                    scroll.callback.AddListener(data =>
                    {
                        Transform above = self.transform.parent;
                        if (above == null) return;
                        ExecuteEvents.ExecuteHierarchy(above.gameObject, data,
                            (IScrollHandler handler, BaseEventData sent) => handler.OnScroll((PointerEventData)sent));
                    });
                    trigger.triggers.Add(scroll);
                }
                foreach (TMPro.TMP_InputField field in window.GetComponentsInChildren<TMPro.TMP_InputField>(true))
                    if (field.GetComponent<ScrollForwarder>() == null) field.gameObject.AddComponent<ScrollForwarder>();
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("scroll-through", "The mouse wheel over a button does not scroll the list under it"
                                                        + " (" + CompatibilityLog.Reason(e) + ").");
            }
        }
    }
}
