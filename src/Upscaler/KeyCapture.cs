using System;
using System.Collections.Generic;
using ReDefinition.Framework;
using UnityEngine;

namespace ReDefinition
{
    // Taking a key from the player: the row says it is listening, the next key
    // pressed is taken, Escape cancels. Modifiers are not taken from the keyboard --
    // the row's switches set them -- since Windows turns AltGr into left Ctrl and
    // right Alt at once.
    //
    // While it listens, KSP's controls are locked (InputLockManager), so that the
    // keys pressed do not stage a vessel or open a scene on the way.
    internal static class KeyCapture
    {
        private const string LockId = "ReDefinition-key-capture";

        private static string listening;
        private static Action<string> take;
        private static bool locked;
        // The frame a combination was taken in: its keys are still down, and the
        // game's controls are unlocked again, so nothing else may act on them.
        private static int takenFrame = -1;

        // The keys that can be bound, once: Unity's KeyCode holds every mouse
        // button and joystick button as well.
        private static KeyCode[] candidates;

        internal static bool Listening(string key)
        {
            return listening != null && listening == key;
        }

        // While a row listens, and for the rest of the frame the combination was
        // taken in: the keys of that combination are down in that frame, and a
        // hotkey or a mod reading them would fire on the binding being set.
        internal static bool Busy
        {
            get { return listening != null || takenFrame == Time.frameCount; }
        }

        // Starts listening for that row; a row already listening stops.
        internal static void Start(string key, Action<string> taken)
        {
            if (Listening(key))
            {
                Stop();
                return;
            }
            listening = key;
            take = taken;
            Lock();
        }

        internal static void Stop()
        {
            listening = null;
            take = null;
            Unlock();
        }

        // From the add-on's Update, every frame.
        internal static void Poll()
        {
            if (listening == null)
            {
                // Not before the frame the combination was taken in is over.
                if (takenFrame != Time.frameCount) Unlock();
                return;
            }
            Lock();
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Stop();
                return;
            }

            KeyCode pressed = KeyCode.None;
            foreach (KeyCode key in Candidates())
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

        private static void Lock()
        {
            if (locked) return;
            InputLockManager.SetControlLock(ControlTypes.All, LockId);
            locked = true;
        }

        private static void Unlock()
        {
            if (!locked) return;
            InputLockManager.RemoveControlLock(LockId);
            locked = false;
        }
    }
}
