using System;
using System.Collections.Generic;
using ReDefinition.Framework;
using UnityEngine;

namespace ReDefinition
{
    // Taking a key binding from the player: the row says it is listening, the next
    // combination of up to two modifiers and one key is taken, Escape cancels.
    //
    // While it listens, KSP's controls are locked (InputLockManager), so that the
    // keys pressed do not stage a vessel or open a scene on the way.
    internal static class KeyCapture
    {
        private const string LockId = "ReDefinition-key-capture";

        private static string listening;
        private static Action<string> take;
        private static bool locked;

        // The keys that can be bound, once: Unity's KeyCode holds every mouse
        // button and joystick button as well.
        private static KeyCode[] candidates;

        internal static bool Listening(string key)
        {
            return listening != null && listening == key;
        }

        internal static bool Busy
        {
            get { return listening != null; }
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
                Unlock();
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

            KeyCode first = KeyCode.None;
            KeyCode second = KeyCode.None;
            foreach (KeyCode modifier in KeyCombination.Modifiers)
            {
                if (!Input.GetKey(modifier)) continue;
                if (first == KeyCode.None) first = modifier;
                else if (second == KeyCode.None) second = modifier;
            }

            Action<string> taken = take;
            string text = new KeyCombination(pressed, first, second).ToString();
            Stop();
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
