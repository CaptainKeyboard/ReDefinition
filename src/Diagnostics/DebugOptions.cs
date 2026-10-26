using System;
using System.Collections.Generic;

namespace ReDefinition.Diagnostics
{
    // The diagnostics window's pages. Overview, Previews and Other mods are drawn by the
    // window itself; Overview also holds options (the upscaler and frame generation
    // switches).
    internal enum DebugCategory
    {
        // In the order the window lists them (DebugOptionSet.Title names them).
        Overview,
        UpscalerInputs,
        FrameGeneration,
        GameSettings,
        Capture,
        Previews,
        OtherMods,
    }

    // How an option is drawn and used.
    internal enum DebugKind
    {
        // On or off, as two segments.
        Switch,
        // One of a few named states, shown as segments.
        Choice,
        // Does something once when pressed.
        Action,
    }

    // Whether what an option is set to survives, and for how long.
    internal enum DebugPersistence
    {
        // Kept in the settings file; for this session only; taking effect after KSP starts again.
        Saved,
        SessionOnly,
        NeedsRestart,
    }

    // One switch, choice or button of the diagnostics window, as data: the window
    // draws it by its kind, the table (DebugOptionSet) filters and resets it, and DebugPins pins it.
    // Free of Unity, so that those decisions are tested outside the game.
    internal sealed class DebugOption
    {
        // Fixed at creation: the id the pins and the probe name it by, its page, its label, its kind and its persistence.
        public readonly string Id;
        public readonly DebugCategory Category;
        public readonly string Label;
        public readonly DebugKind Kind;
        public readonly DebugPersistence Persistence;

        // Where else the option is shown, besides its own page.
        public DebugCategory? Also;
        // A heading within the page the option stands under.
        public string Group = "";
        // Words in Title Case for the pointer, like KSP's own tooltips; never a sentence.
        public string Tooltip = "";
        // Further words the search finds it by.
        public string[] Keywords = new string[0];

        // The states of a Switch (off, on) or a Choice, lowest first.
        public string[] Choices = { "Off", "On" };
        // What an Action's button says.
        public string ActionText = "";
        // The label as it reads now, for one that counts down.
        public Func<string> DynamicLabel;

        // The value as an index (a switch's 0 or 1, a choice's index), its setter, an action's work, and the default value.
        public Func<int> Get = () => 0;
        public Action<int> Set = value => { };
        public Action Run = () => { };
        public int Default;

        // Whether the option takes part in "Changed" and the reset. The upscaler and frame
        // generation switches are the player's settings, not debug switches.
        public bool Resettable = true;

        // Where it does not apply: the reason, null where it does. The row is greyed out and
        // says it -- unless it is away from its default and may have to be put back (PutBack).
        public Func<string> Blocked = () => null;
        public bool PutBack = true;

        // An option with its fixed parts; the rest is set by the initializer that makes it.
        public DebugOption(string id, DebugCategory category, string label, DebugKind kind, DebugPersistence persistence)
        {
            Id = id;
            Category = category;
            Label = label;
            Kind = kind;
            Persistence = persistence;
        }

        // The label shown now: the dynamic one where there is one.
        public string CurrentLabel
        {
            get { return DynamicLabel != null ? DynamicLabel() : Label; }
        }

        // Away from its default. An Action has none.
        public bool Changed
        {
            get { return Kind != DebugKind.Action && Resettable && Get() != Default; }
        }

        // Why the row is greyed out; null where it can be used.
        public string Unavailable()
        {
            string reason = Blocked();
            if (reason == null) return null;
            if (Kind != DebugKind.Action && PutBack && Get() != Default) return null;
            return reason;
        }

        // Whether the search finds it: a part of its words, whatever the case.
        public bool Matches(string query)
        {
            if (string.IsNullOrEmpty(query)) return true;
            string wanted = query.Trim();
            if (wanted.Length == 0) return true;
            if (Contains(Label, wanted) || Contains(Group, wanted) || Contains(Tooltip, wanted)
                || Contains(DebugOptionSet.Title(Category), wanted))
                return true;
            foreach (string keyword in Keywords)
                if (Contains(keyword, wanted)) return true;
            if (Kind == DebugKind.Choice)
                foreach (string choice in Choices)
                    if (Contains(choice, wanted)) return true;
            return false;
        }

        // Whether the text contains the part, case aside; false for no text.
        private static bool Contains(string text, string part)
        {
            return text != null && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    // The window's options in the order they are shown, with the decisions on them.
    internal sealed class DebugOptionSet
    {
        // The options in the order they were added, which is the order they are shown.
        private readonly List<DebugOption> options = new List<DebugOption>();

        // Every option, in that order.
        public IList<DebugOption> All
        {
            get { return options; }
        }

        // Adds an option at the end; two of the same id are a mistake and throw.
        public void Add(DebugOption option)
        {
            foreach (DebugOption known in options)
                if (known.Id == option.Id) throw new ArgumentException("Two debug options are called " + option.Id + ".");
            options.Add(option);
        }

        // The option of that id; null where there is none.
        public DebugOption ById(string id)
        {
            foreach (DebugOption option in options)
                if (option.Id == id) return option;
            return null;
        }

        // A page's name in the list and as the heading of found options.
        public static string Title(DebugCategory category)
        {
            switch (category)
            {
                case DebugCategory.Overview: return "Overview";
                case DebugCategory.UpscalerInputs: return "Upscaler inputs";
                case DebugCategory.FrameGeneration: return "Frame generation & latency";
                case DebugCategory.GameSettings: return "Game settings";
                case DebugCategory.Capture: return "Capture & log";
                case DebugCategory.Previews: return "Previews";
                default: return "Other mods";
            }
        }

        // The options a page shows: its own, and those shown there as well.
        public List<DebugOption> Of(DebugCategory category)
        {
            List<DebugOption> result = new List<DebugOption>();
            foreach (DebugOption option in options)
                if (option.Category == category || option.Also == category) result.Add(option);
            return result;
        }

        // The options the search and the "Changed" filter let through, each once, in the order
        // of the table.
        public List<DebugOption> Filter(string query, bool changedOnly)
        {
            List<DebugOption> result = new List<DebugOption>();
            foreach (DebugOption option in options)
                if (Passes(option, query, changedOnly)) result.Add(option);
            return result;
        }

        // The same for a page: its options that pass.
        public List<DebugOption> Filter(DebugCategory category, string query, bool changedOnly)
        {
            List<DebugOption> result = new List<DebugOption>();
            foreach (DebugOption option in Of(category))
                if (Passes(option, query, changedOnly)) result.Add(option);
            return result;
        }

        // Whether the option matches the search, and is away from its default where only those are wanted.
        private static bool Passes(DebugOption option, string query, bool changedOnly)
        {
            return option.Matches(query) && (!changedOnly || option.Changed);
        }

        // How many options of a page pass, for the category list.
        public int Count(DebugCategory category, string query, bool changedOnly)
        {
            return Filter(category, query, changedOnly).Count;
        }

        // Every option of the window that is away from its default, put back to it. Through
        // the options' own setters, so that each does what a click does. Returns how many.
        public int Reset()
        {
            int reset = 0;
            foreach (DebugOption option in options)
            {
                if (!option.Changed) continue;
                option.Set(option.Default);
                reset++;
            }
            return reset;
        }
    }

    // The options pinned to the strip at the window's foot: ids, in the order they were
    // pinned, kept as one text in the settings.
    internal sealed class DebugPins
    {
        // The pinned ids, in pin order.
        private readonly List<string> ids = new List<string>();

        // The pinned ids, in pin order, as the window reads them.
        public IList<string> Ids
        {
            get { return ids; }
        }

        // Whether the option of that id is pinned.
        public bool Has(string id)
        {
            return ids.Contains(id);
        }

        // Pins an option that is not pinned, unpins one that is.
        public void Toggle(string id)
        {
            if (!ids.Remove(id)) ids.Add(id);
        }

        // The ids as the settings keep them, separated by commas.
        public string Text
        {
            get { return string.Join(",", ids.ToArray()); }
        }

        // From the settings' text; ids no option has are dropped, doubles kept once.
        public void Load(string text, DebugOptionSet known)
        {
            ids.Clear();
            if (string.IsNullOrEmpty(text)) return;
            foreach (string part in text.Split(','))
            {
                string id = part.Trim();
                if (id.Length > 0 && !ids.Contains(id) && known.ById(id) != null) ids.Add(id);
            }
        }

        // The pinned options, in pin order.
        public List<DebugOption> Options(DebugOptionSet known)
        {
            List<DebugOption> result = new List<DebugOption>();
            foreach (string id in ids)
            {
                DebugOption option = known.ById(id);
                if (option != null) result.Add(option);
            }
            return result;
        }
    }

    // The last frame times, newest last: what the window's strip draws.
    internal sealed class FrameTimeRing
    {
        // The ring's storage, the slot the next time goes into, and how many are kept so far.
        private readonly float[] times;
        private int next;
        private int count;

        // A ring that keeps the last `capacity` frame times.
        public FrameTimeRing(int capacity)
        {
            times = new float[capacity];
        }

        // How many frame times are kept, up to the capacity.
        public int Count
        {
            get { return count; }
        }

        // How many frame times the ring keeps at most.
        public int Capacity
        {
            get { return times.Length; }
        }

        // Adds a frame time in milliseconds, over the oldest once the ring is full.
        public void Add(float milliseconds)
        {
            times[next] = milliseconds;
            next = (next + 1) % times.Length;
            if (count < times.Length) count++;
        }

        // 0 is the oldest of those kept.
        public float this[int index]
        {
            get
            {
                if (index < 0 || index >= count) throw new ArgumentOutOfRangeException("index");
                int first = count < times.Length ? 0 : next;
                return times[(first + index) % times.Length];
            }
        }

        // The longest frame time kept; 0 while there is none.
        public float Max
        {
            get
            {
                float max = 0f;
                for (int i = 0; i < count; i++) max = Math.Max(max, times[i]);
                return max;
            }
        }

        // The frame time that this share of the frames stays under (0.5 the median).
        public float Percentile(float share)
        {
            if (count == 0) return 0f;
            float[] sorted = new float[count];
            Array.Copy(times, sorted, count);
            Array.Sort(sorted);
            int at = (int)Math.Round((count - 1) * Math.Max(0f, Math.Min(1f, share)));
            return sorted[at];
        }
    }
}
