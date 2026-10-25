using System.Collections.Generic;
using System;
using ReDefinition.Settings;
using UnityEngine;

namespace ReDefinition.Window
{
    // Taking a key from the player: the row says it is listening, the next key
    // pressed is taken, Escape cancels (and does nothing else: EscapeGuard). It
    // stops listening where the player moves on: the row is no longer shown (its
    // tab left, its group folded, the search), or a text field takes the keyboard. Modifiers are not taken from the keyboard --
    // the row's switches set them -- since Windows turns AltGr into left Ctrl and
    // right Alt at once.
    //
    // While it listens, KSP's controls are locked (InputLockManager), so that the
    // keys pressed do not stage a vessel or open a scene on the way. The same
    // while a text field in ReDefinition's window has the keyboard -- the search
    // field: W is a letter there, not a pitch. The field itself takes Enter to
    // confirm and Escape to cancel, and neither reaches the game.
    internal static class KeyCapture
    {
        // The id of the control lock held while listening or typing.
        private const string LockId = "ReDefinition-key-capture";

        // The row listening (null for none), what it does with the key taken, whether it can still be seen, whether a
        // modifier counts as a key, and whether the lock is set.
        private static string listening;
        private static Action<string> take;
        private static Func<bool> shown;
        private static bool anyKey;
        private static bool locked;
        // The frame a combination was taken in: its keys are still down, and the
        // game's controls are unlocked again, so nothing else may act on them.
        private static int takenFrame = -1;

        // The last frame a text field of ReDefinition's window had the keyboard.
        // Kept one frame longer: Escape and Enter leave the field before this
        // runs, and KSP must not open its pause menu on the Escape that did.
        private static int typingFrame = -10;

        // The keys that can be bound, once: Unity's KeyCode holds every mouse
        // button and joystick button as well.
        private static KeyCode[] candidates;

        // Whether that row is listening.
        internal static bool Listening(string key)
        {
            return listening != null && listening == key;
        }

        // While a row listens, and for the rest of the frame the combination was
        // taken in: the keys of that combination are down in that frame, and a
        // hotkey or a mod reading them would fire on the binding being set.
        internal static bool Busy
        {
            get { return listening != null || takenFrame == Time.frameCount || Typing || EscapeGuard.Active; }
        }

        // Whether a text field of ReDefinition's window had the keyboard this frame or the one before.
        private static bool Typing
        {
            get { return Time.frameCount <= typingFrame + 1; }
        }

        // Starts listening for that row; a row already listening stops. With
        // anyKey a modifier is a key as well: KSP binds LeftShift to the throttle.
        // `shown`: whether the row can still be seen.
        internal static void Start(string key, Action<string> taken, bool anyKey = false, Func<bool> shown = null)
        {
            if (Listening(key))
            {
                Stop();
                return;
            }
            AxisCapture.Stop();
            listening = key;
            take = taken;
            KeyCapture.shown = shown;
            KeyCapture.anyKey = anyKey;
            Lock();
        }

        // Stops listening; the lock goes at the next Poll unless something still holds it.
        internal static void Stop()
        {
            listening = null;
            take = null;
            shown = null;
            Unlock();
        }

        // From the add-on's Update, every frame.
        internal static void Poll()
        {
            EscapeGuard.Poll();
            if (SettingsWindow.TextFieldFocused()) typingFrame = Time.frameCount;
            // The player moved on: no key pressed now is meant for the row.
            if (listening != null && ((shown != null && !shown()) || UiHygiene.Typing))
            {
                listening = null;
                take = null;
                shown = null;
            }
            if (listening == null)
            {
                // Not before the frame the combination was taken in is over, nor
                // while a text field has the keyboard.
                if (Busy) Lock();
                else Unlock();
                return;
            }
            Lock();
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                // The lock stays until the key is up again (Busy).
                EscapeGuard.Swallow();
                listening = null;
                take = null;
                shown = null;
                return;
            }

            KeyCode pressed = KeyCode.None;
            foreach (KeyCode key in anyKey ? AnyCandidates() : Candidates())
            {
                if (!Input.GetKeyDown(key)) continue;
                pressed = key;
                break;
            }
            if (pressed == KeyCode.None) return;

            Action<string> taken = take;
            string text = pressed.ToString();
            // The lock stays for this frame: KSP's handlers run after this one.
            listening = null;
            take = null;
            takenFrame = Time.frameCount;
            if (taken != null) taken(text);
        }

        // The keys KSP's own bindings can hold: the same, the modifiers, and a
        // controller's buttons -- KSP's own screen binds those too
        // (SettingsInputBinding, ExtendedInput.DetectKeyDown).
        private static KeyCode[] anyCandidates;

        // Those keys, made once.
        private static KeyCode[] AnyCandidates()
        {
            if (anyCandidates != null) return anyCandidates;
            List<KeyCode> keys = new List<KeyCode>(Candidates());
            foreach (KeyCode modifier in KeyCombination.Modifiers) keys.Add(modifier);
            foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
            {
                // JoystickButton0 is a button of any controller; Joystick1Button0
                // one of the first. KSP stores the one pressed.
                if (key.ToString().StartsWith("Joystick", StringComparison.Ordinal) && !keys.Contains(key)) keys.Add(key);
            }
            anyCandidates = keys.ToArray();
            return anyCandidates;
        }

        // The keys a binding of ReDefinition's or a mod's can hold, made once: every KeyCode CanBind allows but
        // Escape and the joystick buttons.
        private static KeyCode[] Candidates()
        {
            if (candidates != null) return candidates;
            List<KeyCode> keys = new List<KeyCode>();
            foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
            {
                // Escape cancels, and a joystick button is no binding here.
                if (key == KeyCode.Escape || !KeyCombination.CanBind(key)) continue;
                if (key.ToString().StartsWith("Joystick", StringComparison.Ordinal)) continue;
                keys.Add(key);
            }
            candidates = keys.ToArray();
            return candidates;
        }

        // Set again where KSP cleared every lock meanwhile (the flight UI's
        // docking mode, the pause menus: InputLockManager.ClearControlLocks).
        private static void Lock()
        {
            if (locked && InputLockManager.GetControlLock(LockId) != ControlTypes.None) return;
            InputLockManager.SetControlLock(ControlTypes.All, LockId);
            locked = true;
        }

        // Takes the control lock back where it is set.
        private static void Unlock()
        {
            if (!locked) return;
            InputLockManager.RemoveControlLock(LockId);
            locked = false;
        }
    }
}
