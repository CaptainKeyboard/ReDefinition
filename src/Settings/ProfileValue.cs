using System;
using System.Collections.Generic;
using ReDefinition.Bridges;

namespace ReDefinition.Settings
{
    // A value in a profile's MODULE node that differs by the graphics card:
    //
    //   dlssModel = K (Rtx20 Rtx30), Default
    //
    // The alternatives stand in order, each with the classes it is for in
    // brackets (GpuClass, by name, case aside); one without a list is for every
    // other card and stands last. A card that none is for gets no value, and the
    // setting stays as it is.
    internal static class ProfileValue
    {
        // One alternative: its value and the classes of card it is for.
        internal sealed class Alternative
        {
            // The value the setting takes on such a card.
            public string Value;
            // The classes of card it is for; empty: every card.
            public List<GpuClass> Classes = new List<GpuClass>();

            // Whether the alternative is for the card: listed, or for every card.
            public bool Takes(GpuClass gpu)
            {
                return Classes.Count == 0 || Classes.Contains(gpu);
            }
        }

        // The alternatives of a value; null with the reason in `problem` where it is no such list.
        public static List<Alternative> Parse(string text, out string problem)
        {
            problem = null;
            List<Alternative> alternatives = new List<Alternative>();
            if (string.IsNullOrEmpty(text))
            {
                problem = "no value";
                return null;
            }
            string[] parts = text.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                Alternative alternative = new Alternative();
                int open = part.IndexOf('(');
                if (open < 0)
                {
                    alternative.Value = part;
                }
                else
                {
                    int close = part.LastIndexOf(')');
                    if (close != part.Length - 1 || close < open)
                    {
                        problem = "'" + part + "': the classes stand in brackets at its end";
                        return null;
                    }
                    alternative.Value = part.Substring(0, open).Trim();
                    foreach (string name in part.Substring(open + 1, close - open - 1)
                                 .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        GpuClass gpu = GpuClasses.Parse(name);
                        if (gpu == GpuClass.Unknown)
                        {
                            problem = "'" + name + "' is no class of graphics card";
                            return null;
                        }
                        alternative.Classes.Add(gpu);
                    }
                    if (alternative.Classes.Count == 0)
                    {
                        problem = "'" + part + "': empty brackets";
                        return null;
                    }
                }
                if (alternative.Value.Length == 0)
                {
                    problem = "an alternative without a value";
                    return null;
                }
                if (alternative.Classes.Count == 0 && i != parts.Length - 1)
                {
                    problem = "'" + alternative.Value + "' is for every card and must stand last";
                    return null;
                }
                alternatives.Add(alternative);
            }
            return alternatives;
        }

        // The value for a card, or null where none of the alternatives is for it.
        public static string Resolve(List<Alternative> alternatives, GpuClass gpu)
        {
            foreach (Alternative alternative in alternatives)
                if (alternative.Takes(gpu)) return alternative.Value;
            return null;
        }
    }
}
