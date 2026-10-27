using System.Collections.Generic;
using ReDefinition.Modules;
using UnityEngine;

namespace ReDefinition.Window
{
    // ReDefinition's own settings as text, read once per frame for the rows of a window.
    // KSP runs every row's delegates in every frame, and a row asks for its setting several times -- its name,
    // its value, its control, its default -- each read allocating a string.
    // The answers are kept for the rest of the frame and dropped at the next one, and whenever the window writes a
    // setting (Written), so a click shows in the same frame.
    // They are kept per settings object: the window edits a copy, and compares it with the one it started from.
    internal sealed class OwnReads
    {
        // How many settings objects are followed at once: the copy edited and the one it started from.
        private const int Slots = 2;

        // The objects followed, the text each setting read as, the frame they were read in, and the slot taken next.
        private readonly OwnSettings[] owners = new OwnSettings[Slots];
        private readonly Dictionary<ModuleSetting, string>[] texts =
            { new Dictionary<ModuleSetting, string>(), new Dictionary<ModuleSetting, string>() };
        private int frame = -1;
        private int next;

        // A setting's value in the object as ModuleSetting.Read gives it; a null object is read as it always was.
        public string Read(ModuleSetting setting, OwnSettings settings)
        {
            if (settings == null) return setting.Read(settings);
            Dictionary<ModuleSetting, string> known = SlotOf(settings);
            string text;
            if (known.TryGetValue(setting, out text)) return text;
            text = setting.Read(settings);
            known[setting] = text;
            return text;
        }

        // The setting's value as ModuleSetting.Normalize spells it, made anew only when the value read is another text.
        public string Normalized(ModuleSetting setting, OwnSettings settings)
        {
            string raw = Read(setting, settings);
            KeyValuePair<string, string> made;
            if (normalized.TryGetValue(setting, out made) && string.Equals(made.Key, raw, System.StringComparison.Ordinal))
                return made.Value;
            string spelled = setting.Normalize(raw);
            normalized[setting] = new KeyValuePair<string, string>(raw, spelled);
            return spelled;
        }

        // The spellings made so far, by setting, with the text they were made from.
        private readonly Dictionary<ModuleSetting, KeyValuePair<string, string>> normalized =
            new Dictionary<ModuleSetting, KeyValuePair<string, string>>();

        // The window wrote a setting: what was read is read again.
        public void Written()
        {
            for (int i = 0; i < Slots; i++) texts[i].Clear();
        }

        // The answers kept for that object in this frame.
        private Dictionary<ModuleSetting, string> SlotOf(OwnSettings settings)
        {
            int now = Time.frameCount;
            if (now != frame)
            {
                frame = now;
                Written();
            }
            for (int i = 0; i < Slots; i++)
                if (owners[i] == settings) return texts[i];
            int taken = next;
            next = (next + 1) % Slots;
            owners[taken] = settings;
            texts[taken].Clear();
            return texts[taken];
        }
    }
}
