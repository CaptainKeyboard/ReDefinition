using System;
using System.Collections.Generic;
using ReDefinition.Core;
using ReDefinition.Settings;
using ReDefinition.Upscaler;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition
{
    // The diagnostics window's page of other mods, three lists -- their state that decides the
    // image, the registrations, and the assumptions about their code -- as tables the search
    // filters by line. The lines are made at the layout pass and read by the event after it.
    public partial class ReDefinitionAddon
    {
        private enum ModsList { State, Registrations, Code }

        private struct ModRow
        {
            public string Name;
            public string Text;
            public Color Tint;
        }

        private ModsList modsList = ModsList.State;
        private ModsList? pendingModsList;
        private readonly List<ModRow> modRows = new List<ModRow>();
        private string modRowsNote;

        private static readonly Color Caution = new Color(1f, 0.9f, 0.4f);
        private static readonly Color Orange = new Color(1f, 0.6f, 0.3f);
        private static readonly string[] ModsListNames = { "Their state", "Registrations", "Their code" };

        private void DrawOtherMods()
        {
            

            GUILayout.BeginHorizontal();
            int picked = Segments(ModsListNames, (int)modsList);
            if (picked != (int)modsList) pendingModsList = (ModsList)picked;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);

            if (Event.current.type == EventType.Layout) BuildModRows();

            if (!string.IsNullOrEmpty(modRowsNote)) GUILayout.Label(modRowsNote);

            string wanted = query.Trim();
            int shown = 0;
            Color previous = GUI.color;
            foreach (ModRow row in modRows)
            {
                if (wanted.Length > 0
                    && row.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0
                    && row.Text.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                shown++;
                GUI.color = row.Tint;
                GUILayout.BeginHorizontal();
                GUILayout.Label(row.Name, styles.Cell, GUILayout.Width(150f));
                GUILayout.Label(row.Text, styles.Cell, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();
            }
            GUI.color = previous;

            if (shown == 0 && modRows.Count > 0) GUILayout.Label("No line contains \"" + wanted + "\".", styles.Dim);
        }

        private void BuildModRows()
        {
            if (pendingModsList.HasValue) modsList = pendingModsList.Value;
            pendingModsList = null;
            modRows.Clear();
            modRowsNote = null;
            switch (modsList)
            {
                case ModsList.State: BuildStateRows(); break;
                case ModsList.Registrations: BuildRegistrationRows(); break;
                default: BuildCodeRows(); break;
            }
        }

        // The other mods' state that decides the image (HostStack), line by line.
        private void BuildStateRows()
        {
            // Refreshed only on the Layout event: OnGUI runs several times per
            // frame, and the number of controls must not change between Layout and
            // Repaint.
            HostStack.Refresh(false);
            List<HostStack.Line> lines = HostStack.Lines;
            for (int i = 0; i < lines.Count; i++)
            {
                HostStack.Line line = lines[i];
                modRows.Add(new ModRow
                {
                    Name = line.Label ?? "",
                    Text = line.Value ?? "",
                    Tint = !line.Present ? Color.gray : !line.Ok ? DiagnosticsStyles.Warning : Color.white,
                });
            }
            if (!string.IsNullOrEmpty(hostStackMessage)) modRowsNote = hostStackMessage;
        }

        // What the mod registrations built (docs/modders/registering-a-mod.md): the
        // mods bundled, each with how many settings; a mod that is loaded and not
        // bundled, with why; and every problem the registrations had.
        private void BuildRegistrationRows()
        {
            if (!BundledSettings.Built)
            {
                modRowsNote = "Not read yet: the registrations are read from the main menu on.";
                return;
            }

            List<string> bundled = new List<string>();
            List<ModRow> notBundled = new List<ModRow>();
            foreach (IBundledMod mod in BundledSettings.Mods)
            {
                if (mod.IsInstalled)
                {
                    bundled.Add(mod.ModName + " (" + mod.Settings.Count + ")");
                    continue;
                }
                RegisteredMod registered = mod as RegisteredMod;
                if (registered != null && (registered.Detected || registered.DroppedMembers.Count > 0))
                    notBundled.Add(new ModRow { Name = mod.ModName, Text = "Not bundled: " + NotBundledReason(mod), Tint = DiagnosticsStyles.Warning });
            }
            modRows.Add(new ModRow { Name = "Bundled", Text = bundled.Count > 0 ? string.Join(", ", bundled.ToArray()) : "none", Tint = Color.white });
            modRows.AddRange(notBundled);

            IList<string> problems = BundledSettings.RegistrationProblems;
            foreach (string problem in problems)
                modRows.Add(new ModRow { Name = "Problem", Text = problem, Tint = DiagnosticsStyles.Warning });
            if (problems.Count == 0)
                modRows.Add(new ModRow { Name = "Problems", Text = "No problems in the registrations.", Tint = Color.white });
        }

        // What came of the assumptions about other mods' code (ForeignAssumptions):
        // the installed mods' ones, those not verified first and coloured.
        private void BuildCodeRows()
        {
            List<ForeignAssumption> installed = ForeignAssumptions.Installed();
            if (installed.Count == 0)
            {
                modRowsNote = "None of the mods whose code ReDefinition relies on is installed.";
                return;
            }
            foreach (ForeignState state in new[] { ForeignState.Broken, ForeignState.Changed, ForeignState.Verified })
                foreach (ForeignAssumption assumption in installed)
                {
                    if (assumption.State != state) continue;
                    string line = ForeignAssumptionReport.Line(assumption);
                    string prefix = assumption.Mod + ", ";
                    modRows.Add(new ModRow
                    {
                        Name = assumption.Mod,
                        Text = line.StartsWith(prefix, StringComparison.Ordinal) ? line.Substring(prefix.Length) : line,
                        Tint = state == ForeignState.Verified ? Color.white : assumption.Holds ? Caution : Orange,
                    });
                }
        }

        // Why a loaded mod is not bundled: what took the whole mod with it, where
        // that was said, otherwise the first thing it left out.
        private static string NotBundledReason(IBundledMod mod)
        {
            string wholeMod = RegisteredMod.WholeMod + RegisteredMod.DropSeparator;
            foreach (string line in mod.DroppedMembers)
                if (line.StartsWith(wholeMod, StringComparison.Ordinal)) return line.Substring(wholeMod.Length);
            return mod.DroppedMembers.Count > 0 ? mod.DroppedMembers[0] : "this build offers none of its settings";
        }
    }
}
