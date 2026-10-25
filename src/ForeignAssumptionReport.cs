using System.Collections.Generic;
using System.Text;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition
{
    // What came of the assumptions about other mods' code (ForeignAssumptions):
    // once in KSP.log as the main menu first opens -- every assembly is loaded by
    // then --, and as lines in the diagnostics window (Other mods).
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    internal sealed class ForeignAssumptionReport : MonoBehaviour
    {
        // Logs the assumptions of the installed mods once: the verified ones by id on one line, every other one on a
        // line of its own.
        private void Start()
        {
            List<ForeignAssumption> installed = ForeignAssumptions.Installed();
            if (installed.Count == 0) return;
            List<string> verified = new List<string>();
            StringBuilder others = new StringBuilder();
            foreach (ForeignAssumption assumption in installed)
            {
                if (assumption.State == ForeignState.Verified)
                {
                    verified.Add(assumption.Id);
                    continue;
                }
                others.Append("\n  ").Append(Line(assumption));
            }
            Debug.Log(Log.Tag + " Other mods' code: " + verified.Count + " of " + installed.Count
                      + " assumptions verified" + (verified.Count > 0 ? " (" + string.Join(", ", verified.ToArray()) + ")" : "")
                      + (others.Length > 0 ? "; the others:" + others : "."));
        }

        // One assumption for a reader: its state, what it serves, and where it does
        // not stand verified, why and what is done instead.
        internal static string Line(ForeignAssumption assumption)
        {
            StringBuilder line = new StringBuilder();
            line.Append(assumption.Mod).Append(", ").Append(assumption.Uses).Append(": ");
            switch (assumption.State)
            {
                case ForeignState.Verified:
                    line.Append("verified");
                    break;
                case ForeignState.Changed:
                    line.Append("changed, ").Append(assumption.Holds ? "kept" : "not used").Append(" -- ")
                        .Append(assumption.Problem);
                    if (!assumption.Holds) line.Append(" -- ").Append(assumption.Fallback);
                    break;
                case ForeignState.Broken:
                    line.Append("not usable -- ").Append(assumption.Problem).Append(" -- ").Append(assumption.Fallback);
                    break;
                default:
                    line.Append("not installed");
                    break;
            }
            if (assumption.Version != null) line.Append(" [").Append(assumption.Version).Append(']');
            return line.ToString();
        }
    }
}
