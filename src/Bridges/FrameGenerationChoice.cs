using System.Collections.Generic;
using System.Text;

namespace ReDefinition.Bridges
{
    // The frame generation row's choices -- Off, 2x to 6x, Dynamic -- and which of them the
    // technique that runs allows (what the proxy reports through FrameGenerationBridge.Limits).
    // Free of Unity, for the tests (FrameGenerationChoiceTests).
    internal static class FrameGenerationChoice
    {
        // The row's ends: frame generation switched off, and DLSS-G's dynamic mode, where Streamline chooses the
        // frames shown.
        public const string Off = "Off";
        public const string Dynamic = "Dynamic";

        // The multipliers: frames shown per rendered frame, one generated frame more than the
        // step before.
        public static readonly string[] Steps = { "2x", "3x", "4x", "5x", "6x" };

        // The row's values in the order it shows them.
        public static readonly string[] All = { Off, "2x", "3x", "4x", "5x", "6x", Dynamic };

        // The multiplier row's buttons: Dynamic first, then the steps.
        public static readonly string[] Shown = { Dynamic, "2x", "3x", "4x", "5x", "6x" };

        // The multiplier a fresh settings file holds: Dynamic, which DLSS-G does where it supports
        // multi frame generation; everywhere else it runs as 2x (Effective), the one step every
        // technique does.
        public const string Default = Dynamic;

        // What Dynamic runs as where the technique does not do it.
        public const string DynamicFallback = "2x";

        // A value the setting can hold while frame generation is on (the multiplier's own
        // field: Off is the switch's).
        public static bool Valid(string value)
        {
            return value == Dynamic || StepIndex(value) >= 0;
        }

        // Whether the value asks for DLSS-G's dynamic mode.
        public static bool IsDynamic(string value)
        {
            return value == Dynamic;
        }

        // The frames shown per rendered frame; 0 for Dynamic or a value that is none.
        public static int Multiplier(string value)
        {
            int at = StepIndex(value);
            return at < 0 ? 0 : at + 2;
        }

        // The value's position among Steps; -1 for Dynamic, Off or anything else.
        private static int StepIndex(string value)
        {
            return System.Array.IndexOf(Steps, value);
        }

        // What the proxy says the technique allows now. Technique as KspFgTechnique: 0 none,
        // 1 FSR, 2 DLSS, 3 XeSS. Most: the most frames shown per rendered frame, 0 while it does not
        // say. Dynamic: -1 not known yet, 0 not supported, 1 supported.
        public struct Limits
        {
            // The technique that runs, the most frames it shows per rendered frame, and its dynamic support.
            public int Technique;
            public int Most;
            public int Dynamic;

            // Everything is offered while the technique has said nothing.
            public bool Known
            {
                get { return Technique != 0 && Most >= 2; }
            }
        }

        // Whether the choice can run as it is.
        public static bool Offered(string choice, Limits limits)
        {
            if (choice == Off) return true;
            if (!limits.Known) return true;
            if (choice == Dynamic) return limits.Technique == 2 && limits.Dynamic != 0;
            return Multiplier(choice) <= limits.Most;
        }

        // The choice as it runs: a multiplier above the most is that most, Dynamic where it is not
        // supported 2x; the choice itself where it is offered.
        public static string Effective(string choice, Limits limits)
        {
            if (Offered(choice, limits)) return choice;
            if (choice == Dynamic) return DynamicFallback;
            if (Multiplier(choice) > limits.Most) return limits.Most + "x";
            return choice;
        }

        // Why the step is not offered by the technique that runs, in a few words; null where it is.
        public static string StepReason(string choice, Limits limits)
        {
            if (!limits.Known || Offered(choice, limits)) return null;
            if (choice == Dynamic)
                return limits.Technique == 2
                    ? "Dynamic needs multi frame generation and NVIDIA driver 595.41 or newer."
                    : "Dynamic is DLSS frame generation only.";
            if (limits.Technique == 1) return "FSR 3 generates one frame between two.";
            return (limits.Technique == 3 ? "XeSS frame generation" : "DLSS frame generation") + " allows up to "
                   + limits.Most + "x here.";
        }

        // Why each step that is not offered is not, in a few lines, or null where all are.
        // amperePackage: the RTX 30/20 package is installed, whose MaxGeneratedFrames sets the most.
        public static string Note(Limits limits, bool amperePackage, string choice = null)
        {
            if (!limits.Known) return null;
            List<string> missing = new List<string>();
            foreach (string step in Steps)
                if (Multiplier(step) > limits.Most) missing.Add(step);
            StringBuilder note = new StringBuilder();
            if (missing.Count > 0)
            {
                note.Append(Range(missing)).Append(": ");
                if (limits.Technique == 1)
                    note.Append("FSR 3 generates one frame between two.");
                else if (limits.Technique == 3)
                    note.Append("XeSS-FG generates up to ").Append(limits.Most).Append("x on this GPU.");
                else if (amperePackage)
                    note.Append("the package allows up to ").Append(limits.Most)
                        .Append("x; raise its multiplier under DLSS-G on RTX 30/20 below (applies after a restart of KSP).");
                else
                    note.Append("this GPU allows up to ").Append(limits.Most).Append("x.");
            }
            string runs = choice == Dynamic ? " " + DynamicFallback + " runs instead." : "";
            if (limits.Technique == 2 && limits.Dynamic == 0)
            {
                if (note.Length > 0) note.Append(' ');
                note.Append("Dynamic: not supported here (needs multi frame generation and NVIDIA driver 595.41 or newer).")
                    .Append(runs);
            }
            else if (limits.Technique != 2)
            {
                if (note.Length > 0) note.Append(' ');
                note.Append("Dynamic: DLSS frame generation only.").Append(runs);
            }
            return note.Length == 0 ? null : note.ToString();
        }

        // "5x and 6x", "3x to 6x".
        private static string Range(List<string> steps)
        {
            if (steps.Count == 1) return steps[0];
            if (steps.Count == 2) return steps[0] + " and " + steps[1];
            return steps[0] + " to " + steps[steps.Count - 1];
        }
    }
}
