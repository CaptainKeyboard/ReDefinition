using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace ReDefinition.Framework
{
    // A key binding: up to two modifiers and one key.
    //
    // Written and read as text -- "LeftAlt+F10", "F11", "None" -- the way the
    // registrations, the store and KSP's settings.cfg hold it. Left and right
    // modifiers are told apart, as Scatterer's settings do.
    //
    // Everything but Pressed, Held and Released works without the game, for the
    // tests and the check outside it.
    internal struct KeyCombination : IEquatable<KeyCombination>
    {
        public const string NoneText = "None";

        // The modifiers, in the order they are written.
        private static readonly KeyCode[] ModifierOrder =
        {
            KeyCode.LeftControl, KeyCode.RightControl,
            KeyCode.LeftAlt, KeyCode.RightAlt,
            KeyCode.LeftShift, KeyCode.RightShift,
            KeyCode.LeftCommand, KeyCode.RightCommand
        };

        public readonly KeyCode Key;
        public readonly KeyCode FirstModifier;
        public readonly KeyCode SecondModifier;

        public KeyCombination(KeyCode key, KeyCode firstModifier, KeyCode secondModifier)
        {
            Key = key;
            FirstModifier = KeyCode.None;
            SecondModifier = KeyCode.None;
            // In the written order, so that Alt+Shift+F1 and Shift+Alt+F1 are one
            // combination.
            List<KeyCode> modifiers = new List<KeyCode>(2);
            foreach (KeyCode modifier in ModifierOrder)
            {
                if ((firstModifier == modifier || secondModifier == modifier) && !modifiers.Contains(modifier))
                    modifiers.Add(modifier);
            }
            if (modifiers.Count > 0) FirstModifier = modifiers[0];
            if (modifiers.Count > 1) SecondModifier = modifiers[1];
        }

        public static readonly KeyCombination None = new KeyCombination(KeyCode.None, KeyCode.None, KeyCode.None);

        public bool IsBound
        {
            get { return Key != KeyCode.None; }
        }

        public int ModifierCount
        {
            get { return (FirstModifier != KeyCode.None ? 1 : 0) + (SecondModifier != KeyCode.None ? 1 : 0); }
        }

        public bool HasModifier(KeyCode modifier)
        {
            return modifier != KeyCode.None && (FirstModifier == modifier || SecondModifier == modifier);
        }

        // The same combination with that modifier added, or without it where it is
        // already there; a third modifier replaces the second.
        public KeyCombination Toggled(KeyCode modifier)
        {
            if (!IsModifier(modifier)) return this;
            if (HasModifier(modifier))
            {
                KeyCode kept = FirstModifier == modifier ? SecondModifier : FirstModifier;
                return new KeyCombination(Key, kept, KeyCode.None);
            }
            // A third modifier takes the second one's place.
            return new KeyCombination(Key, FirstModifier, modifier);
        }

        public static bool IsModifier(KeyCode key)
        {
            foreach (KeyCode modifier in ModifierOrder)
            {
                if (modifier == key) return true;
            }
            return false;
        }

        // Which keys a binding can hold: the keyboard, and the mouse from its third
        // button on. Mouse0 and Mouse1 are the game's own -- selecting, and the
        // camera -- and the wheel is an axis, not a key. A modifier alone is no
        // binding either.
        public static bool CanBind(KeyCode key)
        {
            if (key == KeyCode.None || IsModifier(key)) return false;
            if (key == KeyCode.Mouse0 || key == KeyCode.Mouse1) return false;
            return true;
        }

        // "LeftAlt+F10". Unbound is "None", as KSP's settings.cfg writes it.
        public override string ToString()
        {
            if (!IsBound) return NoneText;
            string text = string.Empty;
            if (FirstModifier != KeyCode.None) text += FirstModifier + "+";
            if (SecondModifier != KeyCode.None) text += SecondModifier + "+";
            return text + Key;
        }

        // Takes what the mods and KSP write: a key on its own, modifiers before it
        // separated by "+", and "None" or nothing for unbound. False for anything
        // else, with None in combination.
        public static bool TryParse(string text, out KeyCombination combination)
        {
            combination = None;
            if (text == null) return false;
            string trimmed = text.Trim();
            if (trimmed.Length == 0 || string.Equals(trimmed, NoneText, StringComparison.OrdinalIgnoreCase)) return true;

            string[] parts = trimmed.Split('+');
            KeyCode key = KeyCode.None;
            KeyCode first = KeyCode.None;
            KeyCode second = KeyCode.None;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0) return false;
                KeyCode parsed;
                if (!TryParseKey(part, out parsed)) return false;
                if (i < parts.Length - 1)
                {
                    if (!IsModifier(parsed)) return false;
                    if (first == KeyCode.None) first = parsed;
                    else if (second == KeyCode.None) second = parsed;
                    else return false;
                }
                else
                {
                    if (!CanBind(parsed)) return false;
                    key = parsed;
                }
            }
            combination = new KeyCombination(key, first, second);
            return true;
        }

        // What the text means, or None where it means nothing.
        public static KeyCombination Parse(string text)
        {
            KeyCombination combination;
            TryParse(text, out combination);
            return combination;
        }

        private static bool TryParseKey(string text, out KeyCode key)
        {
            key = KeyCode.None;
            try
            {
                object parsed = Enum.Parse(typeof(KeyCode), text, true);
                key = (KeyCode)parsed;
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        public bool Equals(KeyCombination other)
        {
            return Key == other.Key && FirstModifier == other.FirstModifier && SecondModifier == other.SecondModifier;
        }

        public override bool Equals(object other)
        {
            return other is KeyCombination && Equals((KeyCombination)other);
        }

        public override int GetHashCode()
        {
            return (int)Key ^ ((int)FirstModifier << 9) ^ ((int)SecondModifier << 18);
        }

        // In the game: the key this frame, with exactly this combination's
        // modifiers held. Another modifier held means another binding is meant --
        // F10 does not fire while Alt+F10 is pressed.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool Pressed()
        {
            return IsBound && Input.GetKeyDown(Key) && ModifiersHeld();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool Held()
        {
            return IsBound && Input.GetKey(Key) && ModifiersHeld();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool Released()
        {
            return IsBound && Input.GetKeyUp(Key) && ModifiersHeld();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool ModifiersHeld()
        {
            foreach (KeyCode modifier in ModifierOrder)
            {
                bool wanted = HasModifier(modifier);
                if (wanted != Input.GetKey(modifier)) return false;
            }
            return true;
        }
    }
}
