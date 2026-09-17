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

        // The rows others share a combination with, for a row's tooltip.
        internal static List<string> Others(string key)
        {
            Count();
            List<string> others = new List<string>();
            Entry mine = entries.Find(entry => entry.Key == key);
            if (mine == null) return others;
            KeyCombination combination = KeyCombination.Parse(Safe(mine));
            if (!combination.IsBound) return others;
            foreach (Entry other in entries)
            {
                if (other == mine || (other.Modes & mine.Modes) == 0) continue;
                if (KeyCombination.Parse(Safe(other)).Equals(combination)) others.Add(other.Key);
            }
            return others;
        }

        // Once a frame: the rows read their label every frame, and every row would
        // otherwise walk the whole list.
        private static void Count()
        {
            if (countedFrame == Time.frameCount) return;
            countedFrame = Time.frameCount;
            List<string> keys = new List<string>(entries.Count);
            List<string> texts = new List<string>(entries.Count);
            List<int> modes = new List<int>(entries.Count);
            foreach (Entry entry in entries)
            {
                keys.Add(entry.Key);
                texts.Add(Safe(entry));
                modes.Add(entry.Modes);
            }
            sharing.Clear();
            foreach (string key in Sharing(keys, texts, modes)) sharing.Add(key);
        }

        // Which of the bindings given share a combination with another that counts
        // in the same situations. Without the game, for the tests.
        internal static List<string> Sharing(IList<string> keys, IList<string> texts, IList<int> modes)
        {
            List<string> shared = new List<string>();
            for (int i = 0; i < keys.Count; i++)
            {
                KeyCombination first = KeyCombination.Parse(texts[i]);
                if (!first.IsBound) continue;
                for (int j = i + 1; j < keys.Count; j++)
                {
                    if ((modes[i] & modes[j]) == 0) continue;
                    if (!KeyCombination.Parse(texts[j]).Equals(first)) continue;
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
