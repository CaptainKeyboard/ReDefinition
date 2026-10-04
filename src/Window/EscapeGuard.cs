using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ReDefinition.Window
{
    // An Escape that cancelled something in one of ReDefinition's dialogs -- a row
    // listening for a key or an axis, a text field being typed into -- does
    // nothing else.
    //
    // Both of those end as the key goes down. KSP acts on the key going up, a few
    // frames later: every PopupDialog dismisses itself (PopupDialog.Update:
    // Input.GetKeyUp(KeyCode.Escape), with no lock asked; decompiled), and the
    // pause menu opens (PauseMenu.Update: GameSettings.PAUSE.GetKeyUp, where the
    // PAUSE control is unlocked). So from the press that was taken until the frame
    // after the key is up, no PopupDialog dismisses itself on it (a prefix on
    // PopupDialog.Update, which runs the dialog's own update and skips the rest),
    // and KeyCapture keeps its lock, which holds PAUSE.
    internal static class EscapeGuard
    {
        private static bool held;
        private static int releasedFrame = -10;
        // The last frame a text field of these dialogs was being typed into. The
        // field itself takes the Escape in the event system's update and lets go
        // of the keyboard there; whether that runs before or after this is not
        // fixed, so the frame before counts too.
        private static int typingFrame = -10;
        private static bool hooked;
        // The dialogs whose text fields count.
        private static readonly List<GameObject> own = new List<GameObject>();

        // While an Escape taken here is down, and for the frame after it is up.
        internal static bool Active
        {
            get { return held || Time.frameCount <= releasedFrame + 1; }
        }

        // The Escape going down now was taken.
        internal static void Swallow()
        {
            held = true;
        }

        // A dialog of ReDefinition's: Escape in one of its text fields ends the
        // typing, not the dialog.
        internal static void Own(PopupDialog dialog)
        {
            own.RemoveAll(gone => gone == null);
            if (dialog != null && !own.Contains(dialog.gameObject)) own.Add(dialog.gameObject);
        }

        // Every frame, before the captures' own polls.
        internal static void Poll()
        {
            if (held && !Input.GetKey(KeyCode.Escape))
            {
                held = false;
                releasedFrame = Time.frameCount;
            }
            bool typing = TypingHere();
            if (!held && Input.GetKeyDown(KeyCode.Escape) && (typing || typingFrame >= Time.frameCount - 1)) Swallow();
            if (typing) typingFrame = Time.frameCount;
        }

        private static bool TypingHere()
        {
            EventSystem events = EventSystem.current;
            GameObject selected = events != null ? events.currentSelectedGameObject : null;
            if (selected == null) return false;
            TMP_InputField field = selected.GetComponent<TMP_InputField>();
            if (field == null || !field.isFocused) return false;
            for (int i = own.Count - 1; i >= 0; i--)
            {
                if (own[i] == null)
                {
                    own.RemoveAt(i);
                    continue;
                }
                if (selected.transform.IsChildOf(own[i].transform)) return true;
            }
            return false;
        }

        // Once, as the add-on starts.
        internal static void Install()
        {
            if (hooked) return;
            hooked = true;
            HarmonyHooks.Safely("escape-guard", "An Escape that cancels a key binding or a text field also closes the window",
                () => HarmonyHooks.Install("ReDefinition.EscapeGuard", typeof(EscapeGuard), new List<HarmonyHook>
                {
                    new HarmonyHook(typeof(PopupDialog).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic),
                        nameof(BeforeUpdate), null, null),
                }));
        }

        // What PopupDialog.Update does before it looks at Escape, and nothing of
        // what it does after, while the Escape was taken.
        private static bool BeforeUpdate(PopupDialog __instance)
        {
            if (!Active) return true;
            try
            {
                if (__instance.dialogToDisplay != null) __instance.dialogToDisplay.Update();
            }
            catch (System.Exception e)
            {
                CompatibilityLog.Warn("escape-guard-update", "A dialog could not be updated while an Escape was held back ("
                                                             + CompatibilityLog.Reason(e) + ").");
            }
            return false;
        }
    }
}
