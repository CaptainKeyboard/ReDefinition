using System;
using System.Collections.Generic;
using ReDefinition.Framework;
using UnityEngine;

namespace ReDefinition
{
    // Which bindings in the Keys tab share a combination.
    //
    // KSP's own bindings carry a modeMask: in which situations they count
    // (settings.cfg). Two that never count at the same time are no conflict --
    // the same key stages a vessel in flight and does something else in the
    // editor. A binding of ReDefinition or of a mod counts everywhere, so it
    // shares with anything that holds the same combination.
    //
    // Nothing is refused: a shared combination is shown in yellow, and the player
    // decides.
    internal static class Conflicts
    {
        private sealed class Entry
        {
            public string Key;
            public Func<string> Text;
            public int Modes;
        }

        private static readonly List<Entry> entries = new List<Entry>();
        private static readonly HashSet<string> sharing = new HashSet<string>();
        // Filled again each frame, never built again: the tab holds every one of
        // KSP's bindings.
        private static readonly List<string> keysNow = new List<string>();
        private static readonly List<string> textsNow = new List<string>();
        private static readonly List<int> modesNow = new List<int>();
        private static KeyCombination[] parsedNow = new KeyCombination[0];
        private static int countedFrame = -1;

        internal static void Clear()
        {
            entries.Clear();
            sharing.Clear();
            countedFrame = -1;
        }

        // From the rows as they are built. modes is KSP's modeMask, or -1 for a
        // binding that counts in every situation.
        internal static void Register(string key, Func<string> text, int modes)
        {
            entries.Add(new Entry { Key = key, Text = text, Modes = modes });
        }

        internal static bool Shares(string key)
        {
            Count();
            return sharing.Contains(key);
        }

        // Once a frame: the rows read their label every frame, and every row would
        // otherwise walk the whole list.
        private static void Count()
        {
            if (countedFrame == Time.frameCount) return;
            countedFrame = Time.frameCount;
            keysNow.Clear();
            textsNow.Clear();
            modesNow.Clear();
            foreach (Entry entry in entries)
            {
                keysNow.Add(entry.Key);
                textsNow.Add(Safe(entry));
                modesNow.Add(entry.Modes);
            }
            sharing.Clear();
            foreach (string key in Sharing(keysNow, textsNow, modesNow)) sharing.Add(key);
        }

        // Which of the bindings given share a combination with another that counts
        // in the same situations. Without the game, for the tests.
        internal static List<string> Sharing(IList<string> keys, IList<string> texts, IList<int> modes)
        {
            // Parsed once each, not once per pair: the tab holds every one of KSP's
            // bindings, and this runs in a frame. The array grows with the rows and
            // is kept, rather than built again every frame.
            if (parsedNow.Length < keys.Count) parsedNow = new KeyCombination[keys.Count];
            KeyCombination[] combinations = parsedNow;
            for (int i = 0; i < keys.Count; i++) combinations[i] = KeyCombination.Parse(texts[i]);

            List<string> shared = new List<string>();
            for (int i = 0; i < keys.Count; i++)
            {
                if (!combinations[i].IsBound) continue;
                for (int j = i + 1; j < keys.Count; j++)
                {
                    if ((modes[i] & modes[j]) == 0) continue;
                    if (!combinations[j].Equals(combinations[i])) continue;
                    if (!shared.Contains(keys[i])) shared.Add(keys[i]);
                    if (!shared.Contains(keys[j])) shared.Add(keys[j]);
                }
            }
            return shared;
        }

        private static string Safe(Entry entry)
        {
            try
            {
                return entry.Text();
            }
            catch (Exception)
            {
                return KeyCombination.NoneText;
            }
        }
    }
}
