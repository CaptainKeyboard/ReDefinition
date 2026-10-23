using System.Collections.Generic;
using System;
using ReDefinition.Settings;
using UnityEngine;

namespace ReDefinition.Window
{
    // Which bindings in the Keys tab share a combination in the same situation.
    //
    // A binding counts in situations -- flying a vessel, on EVA, in the editor, in
    // map view (KspKeyBindings): B brakes a vessel and boards one on EVA, and the
    // two never meet. Within flight KSP's switchState tells modes apart as well:
    // Space stages, and in docking mode switches translation and rotation. Two
    // bindings KSP itself ships on the same key -- W pitches and drives a rover --
    // are KSP's choice, not the player's, and are not marked while both stand
    // at KSP's default. A binding of ReDefinition or of a mod counts everywhere.
    // KSP's own bindings fire on their key whatever modifiers are held
    // (ExtendedInput.GetKeyDown asks Input.GetKeyDown of the key and nothing
    // more): RightCtrl+RightShift+U switches KSP's lights on U as well. Against one
    // of KSP's, only the key counts.
    //
    // Nothing is refused: a shared combination is shown in yellow, and the player
    // decides.
    internal static class Conflicts
    {
        private sealed class Entry
        {
            public string Key;
            public Func<string> Text;
            public int Situations;
            public int Modes;
            // What KSP ships for it; null for a binding that is not KSP's.
            public Func<string> Shipped;
            // The row it marks: its own key, or for a crossed combination
            // (RegisterCrossed) the row whose key it holds.
            public string Row;
            // Entries of one mod's crossed bindings, which are never compared with
            // each other; null for the others.
            public string Apart;
        }

        private static readonly List<Entry> entries = new List<Entry>();
        private static readonly HashSet<string> sharing = new HashSet<string>();
        // Filled again at each count, never built again: the tab holds every one
        // of KSP's bindings.
        private static readonly List<string> keysNow = new List<string>();
        private static readonly List<string> textsNow = new List<string>();
        private static readonly List<int> modesNow = new List<int>();
        private static readonly List<int> situationsNow = new List<int>();
        private static readonly List<string> shippedNow = new List<string>();
        private static KeyCombination[] parsedNow = new KeyCombination[0];
        private static int countedVersion = int.MinValue;

        internal static void Clear()
        {
            entries.Clear();
            sharing.Clear();
            countedVersion = int.MinValue;
        }

        // From the rows as they are built: where the binding counts, KSP's
        // switchState within flight, or -1, and what KSP ships for one of its own.
        internal static void Register(string key, Func<string> text, int situations, int modes, Func<string> shipped)
        {
            entries.Add(new Entry
            {
                Key = key, Text = text, Situations = situations, Modes = modes, Shipped = shipped, Row = key,
            });
        }

        // A mod that opens one thing on either modifier with either key -- Scatterer's
        // window: its row and the combination crossed with the other row's modifier
        // both count, the crossed one marking the row, and the mod's own rows are no
        // conflict with each other (they open the same window).
        internal static void RegisterCrossed(string key, string apart, Func<string> crossed, int situations)
        {
            foreach (Entry own in entries)
                if (own.Key == key) own.Apart = apart;
            entries.Add(new Entry
            {
                Key = key + "~crossed", Text = crossed, Situations = situations, Modes = -1, Row = key, Apart = apart,
            });
        }

        // The other row's modifier with this row's key. Where the other row holds no
        // modifier its key is written as its modifier (ScattererBehaviour.ReachBinding),
        // so that key held is the modifier crossed with this one.
        internal static string Crossed(string own, string other)
        {
            KeyCombination mine = KeyCombination.Parse(own);
            KeyCombination theirs = KeyCombination.Parse(other);
            if (!mine.IsBound || !theirs.IsBound) return KeyCombination.NoneText;
            UnityEngine.KeyCode modifier = theirs.FirstModifier != UnityEngine.KeyCode.None ? theirs.FirstModifier : theirs.Key;
            if (modifier == mine.Key) return KeyCombination.NoneText;
            return new KeyCombination(mine.Key, modifier, UnityEngine.KeyCode.None).ToString();
        }

        // Counted again only when the rows' version has moved: every row asks in
        // every frame, and the count reads every binding.
        internal static bool Shares(string key, int version)
        {
            Count(version);
            return sharing.Contains(key);
        }

        private static void Count(int version)
        {
            if (countedVersion == version) return;
            countedVersion = version;
            keysNow.Clear();
            textsNow.Clear();
            modesNow.Clear();
            situationsNow.Clear();
            shippedNow.Clear();
            apartNow.Clear();
            foreach (Entry entry in entries)
            {
                keysNow.Add(entry.Key);
                apartNow.Add(entry.Apart);
                // Only KSP's own bindings -- the ones with what KSP ships -- hold a
                // modifier as a key of its own; a mod's is read as its row shows it,
                // where a lone modifier is no binding.
                string text = Safe(entry.Text);
                textsNow.Add(entry.Shipped != null ? text : KeyCombination.Parse(text).ToString());
                situationsNow.Add(entry.Situations);
                modesNow.Add(entry.Modes);
                shippedNow.Add(entry.Shipped != null ? Safe(entry.Shipped) : null);
            }
            sharing.Clear();
            List<string> shared = Sharing(keysNow, textsNow, situationsNow, modesNow, shippedNow, apartNow);
            foreach (Entry entry in entries)
                if (shared.Contains(entry.Key)) sharing.Add(entry.Row);
        }

        private static readonly List<string> apartNow = new List<string>();

        internal static List<string> Sharing(IList<string> keys, IList<string> texts, IList<int> situations,
                                             IList<int> modes, IList<string> shipped)
        {
            return Sharing(keys, texts, situations, modes, shipped, null);
        }

        // Which of the bindings given share a combination with another that counts
        // in the same situation. shipped holds KSP's default of each of its own
        // bindings, null for the others; two with the same `apart` are never
        // compared (null: none are kept apart). Without the game, for the tests.
        internal static List<string> Sharing(IList<string> keys, IList<string> texts, IList<int> situations,
                                             IList<int> modes, IList<string> shipped, IList<string> apart)
        {
            // Parsed once each, not once per pair: the tab holds every one of KSP's
            // bindings. The array grows with the rows and is kept, rather than built
            // again at every count.
            if (parsedNow.Length < keys.Count) parsedNow = new KeyCombination[keys.Count];
            KeyCombination[] combinations = parsedNow;
            // Loosely: KSP binds modifiers as keys of their own.
            for (int i = 0; i < keys.Count; i++) combinations[i] = KeyCombination.ParseLoose(texts[i]);

            List<string> shared = new List<string>();
            for (int i = 0; i < keys.Count; i++)
            {
                if (!combinations[i].IsBound) continue;
                for (int j = i + 1; j < keys.Count; j++)
                {
                    if ((situations[i] & situations[j]) == 0 || (modes[i] & modes[j]) == 0) continue;
                    if (apart != null && apart[i] != null && apart[i] == apart[j]) continue;
                    bool kspInvolved = shipped[i] != null || shipped[j] != null;
                    if (kspInvolved ? combinations[j].Key != combinations[i].Key : !combinations[j].Equals(combinations[i]))
                        continue;
                    if (ShippedTogether(texts[i], shipped[i]) && ShippedTogether(texts[j], shipped[j])) continue;
                    if (!shared.Contains(keys[i])) shared.Add(keys[i]);
                    if (!shared.Contains(keys[j])) shared.Add(keys[j]);
                }
            }
            return shared;
        }

        // A KSP binding that stands at what KSP ships.
        private static bool ShippedTogether(string text, string shipped)
        {
            return shipped != null && KeyCombination.ParseLoose(text).Equals(KeyCombination.ParseLoose(shipped));
        }

        private static string Safe(Func<string> read)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return KeyCombination.NoneText;
            }
        }
    }
}
