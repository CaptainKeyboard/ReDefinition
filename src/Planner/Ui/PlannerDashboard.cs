using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using ReDefinition.Planner.Engineer;
using ReDefinition.Planner.Planning;
using static ReDefinition.Planner.Planning.PlanText;

namespace ReDefinition.Planner.Ui
{
    // The planner as the dashboard's VAB page shows and works it (src/Dashboard/EditorTelemetry.cs). It gives the page what the rows show, in
    // the texts they show it in, KSP's rich text and all, for the page to draw as the game does. It gives the lists the buttons fold out, and
    // makes the same changes the buttons make, through the same commands of the model. Every row is read, folded in the game or not: the fuel
    // plan's and the thrust part's steps as if each were open.
    internal sealed class PlannerDashboard
    {
        // The roles in the order their steps come.
        private static readonly StepRole[] Roles = { StepRole.Launch, StepRole.Transfer, StepRole.Arrive, StepRole.Land };

        private readonly PlannerModel model;
        // Closes the lists the rows folded out, before a pick from the dashboard changes the plan under them.
        private readonly Action closeLists;
        private int snapshotVersion = -1;
        private Dictionary<string, object> snapshot;

        internal PlannerDashboard(PlannerModel model, Action closeLists)
        {
            this.model = model;
            this.closeLists = closeLists;
        }

        // What the rows show, made again only where their texts may have changed (PlannerModel.Version).
        internal Dictionary<string, object> Snapshot()
        {
            if (snapshot != null && snapshotVersion == model.Version) return snapshot;
            snapshot = MakeSnapshot();
            snapshotVersion = model.Version;
            return snapshot;
        }

        // The rows as the page takes them: the craft, the sections, the settings, the stops with their steps, and once the
        // plan is worked out the summary, the route's bar, the fuel plan and the thrust part.
        private Dictionary<string, object> MakeSnapshot()
        {
            var shot = new Dictionary<string, object>
            {
                { "vessel", model.Vessel },
                { "stockOff", EditorReadout.StockOff },
                { "ready", model.Ready() },
            };
            if (!model.Ready()) return shot;
            PlanSections sections = model.Sections;
            PlannerState state = model.State;
            shot["craft"] = model.VesselText();

            var sectionRows = new List<object>();
            for (int row = 0; row < sections.Live.Count && row < PlanSections.MaxSections; row++)
            {
                var section = new Dictionary<string, object>
                {
                    { "row", row }, { "name", sections.SectionLabel(row) }, { "parts", sections.SectionText(row) },
                    { "kind", sections.Live[row].Plan.Kind.ToString() }, { "warning", sections.SectionWarning(row) },
                };
                if (sections.IsKind(row, SectionKind.Satellite)) section["deploy"] = sections.DeployText(row);
                sectionRows.Add(section);
            }
            shot["sections"] = sectionRows;
            shot["canAddSection"] = state.Sections.Count < PlanSections.MaxSections;
            shot["margin"] = Math.Round(state.Margin * 100.0);

            var stops = new List<object>();
            for (int i = 0; i < state.Stops.Count; i++)
            {
                var stop = new Dictionary<string, object>
                {
                    { "letter", Letter(i) }, { "body", model.BodyName(i) }, { "hasBody", model.HasBody(i) },
                    { "kind", model.Kind(i).ToString() }, { "removable", state.Stops.Count > 2 },
                };
                if (model.HasBody(i) && model.Kind(i) == StopKind.Orbit) stop["km"] = model.AltitudeKm(i);
                if (i > 0 && model.HasAtmosphere(i))
                    stop["air"] = new Dictionary<string, object>
                    {
                        { "label", model.Kind(i) == StopKind.Surface ? "Chutes" : "Aerobrake" }, { "on", model.UsesAtmosphere(i) },
                    };
                var after = new List<object>();
                foreach (StepRole role in Roles)
                    if (model.Slot(i, role) >= 0) after.Add(StepShot(i, role));
                stop["steps"] = after;
                stops.Add(stop);
            }
            shot["stops"] = stops;
            shot["canAddStop"] = model.CanAddStop();
            shot["nextLetter"] = Letter(state.Stops.Count);
            shot["hasSections"] = sections.HasSections();
            if (!model.Worked()) return shot;

            shot["summary"] = model.SummaryText();
            shot["total"] = TotalBar();
            shot["fuel"] = FuelShot();
            shot["thrust"] = ThrustShot();
            return shot;
        }

        // A step on the route: where it goes, who flies it, its bar on the route's scale, its
        // delta-v, the grey lines, its further choices and in red what blocks it.
        private Dictionary<string, object> StepShot(int leg, StepRole role)
        {
            StageBudget.Result budget = model.Budget;
            int index = model.Slot(leg, role);
            double longest = 1.0;
            foreach (double value in budget.Needed) if (value > longest) longest = value;
            var bar = new List<object>();
            foreach (StageBudget.StageUse use in budget.Uses)
                if (use.Leg == index) bar.Add(Share(use.DeltaV / longest, model.StageColour(use.Stage)));
            if (budget.Short[index] > 0.5) bar.Add(Share(budget.Short[index] / longest, PlannerColors.Short));
            var step = new Dictionary<string, object>
            {
                { "leg", leg }, { "role", (int)role }, { "place", model.StepPlace(leg, role) }, { "dv", model.StepSpeed(leg, role) },
                { "bar", bar }, { "separation", model.SeparationText(leg, role) }, { "deployed", model.DeployedText(leg, role) },
            };
            if (model.Sections.HasSections()) step["flyer"] = model.VehicleText(leg, role);
            if (model.Docks(leg, role))
                step["docks"] = new Dictionary<string, object>
                {
                    { "refuel", model.Refuels(leg, role) }, { "tanker", model.Refuels(leg, role) ? model.TankerText(leg, role) : "" },
                };
            if (model.IntoTheAir(leg, role)) step["orbitFirst"] = model.OrbitFirst(leg + 1);
            if (role == StepRole.Transfer && model.CanSplit(leg)) step["split"] = model.SplitsBurns(leg + 1);
            var lines = new List<object>();
            for (int line = 0; line < PlannerModel.MaxWarnings; line++)
            {
                string text = model.WarningText(leg, role, line);
                if (text.Length > 0) lines.Add(text);
            }
            step["warnings"] = lines;
            return step;
        }

        // One piece of a bar for the page: its share of the scale and its colour.
        private static Dictionary<string, object> Share(double share, Color colour)
        {
            return new Dictionary<string, object> { { "share", Math.Round(share, 4) }, { "color", PlannerColors.Hex(colour) } };
        }

        // The mission's bar as the view draws it: the stages' share, what is left, what is missing.
        private List<object> TotalBar()
        {
            StageBudget.Result budget = model.Budget;
            List<StageRow> stages = model.Stages;
            var parts = new List<KeyValuePair<double, Color>>();
            for (int i = 0; i < stages.Count; i++)
            {
                double used = 0.0;
                foreach (StageBudget.StageUse use in budget.Uses) if (use.Stage == stages[i].Number) used += use.DeltaV;
                if (used > 0.5) parts.Add(new KeyValuePair<double, Color>(used, PlannerColors.Stage(i)));
            }
            if (budget.TotalLeft > 0.5) parts.Add(new KeyValuePair<double, Color>(budget.TotalLeft, PlannerColors.Spare));
            if (budget.TotalShort > 0.5) parts.Add(new KeyValuePair<double, Color>(budget.TotalShort, PlannerColors.Short));
            double sum = 0.0;
            foreach (KeyValuePair<double, Color> part in parts) sum += part.Key;
            double scale = Math.Max(1.0, Math.Max(sum, Math.Max(model.TotalNeeded(), model.TotalHas())));
            var bar = new List<object>();
            foreach (KeyValuePair<double, Color> part in parts) bar.Add(Share(part.Key / scale, part.Value));
            return bar;
        }

        // The fuel plan: every step with what it needs and leaves, its stages with Use and Drop
        // after, its burns and the margin, and what blocks it.
        private Dictionary<string, object> FuelShot()
        {
            PlanFuel fuel = model.Fuel;
            var rows = new List<object>();
            for (int step = 0; step < model.Steps.Count; step++)
            {
                var stageRows = new List<object>();
                int count = fuel.StageCount(step);
                for (int row = 0; row < count; row++)
                {
                    StageBudget.StepStage stage = fuel.Stage(step, row);
                    if (stage == null) continue;
                    stageRows.Add(new Dictionary<string, object>
                    {
                        { "number", stage.Stage }, { "name", model.ColouredStage(stage.Stage) },
                        { "color", PlannerColors.Hex(model.StageColour(stage.Stage)) },
                        { "gives", fuel.GivesText(stage) }, { "keeps", fuel.KeepsText(stage) }, { "twr", fuel.TwrText(step, stage) },
                        { "use", fuel.Burnable(step, row) }, { "canUse", !model.AirStage(stage.Stage) },
                        { "drop", stage.CanDrop && model.DropsAfter(step, stage.Stage) },
                        { "canDrop", !model.AirStage(stage.Stage) && (stage.CanDrop || model.DropsAfter(step, stage.Stage)) },
                    });
                }
                var lines = new List<object>();
                foreach (KeyValuePair<string, string> line in fuel.Lines(step)) lines.Add(new List<object> { line.Key, line.Value });
                var warningLines = new List<object>();
                for (int line = 0; line < PlannerModel.MaxWarnings; line++)
                {
                    string text = fuel.WarningText(step, line);
                    if (text.Length > 0) warningLines.Add(text);
                }
                RcsInfo rcs = model.Thrust.RcsAt(step);
                rows.Add(new Dictionary<string, object>
                {
                    { "place", model.StepName(step) }, { "flyer", model.StepFlyer(step) }, { "needs", fuel.NeedsText(step) },
                    { "left", fuel.LeftText(step) }, { "stages", stageRows }, { "rcs", rcs != null ? EngineerFormat.Speed(rcs.DeltaV) : null },
                    { "lines", lines }, { "warnings", warningLines },
                });
            }
            return new Dictionary<string, object> { { "summary", fuel.Summary() }, { "steps", rows } };
        }

        // The thrust part: every step that burns, with what it needs and has and how fast it
        // turns; its stages, its RCS, its burns, its notes and the body.
        private Dictionary<string, object> ThrustShot()
        {
            PlanThrust thrust = model.Thrust;
            var rows = new List<object>();
            for (int row = 0; row < thrust.Entries.Count; row++)
            {
                ThrustStep entry = thrust.Entry(row);
                if (entry == null) continue;
                var stageRows = new List<object>();
                for (int stage = 0; stage < entry.Uses.Count; stage++)
                    stageRows.Add(new List<object>
                    {
                        thrust.StageText(entry, stage), thrust.MassText(entry, stage), thrust.IspText(entry, stage),
                        thrust.TwrValue(entry, stage, true, false), thrust.TwrValue(entry, stage, false, false),
                        thrust.TwrValue(entry, stage, true, true), thrust.ThrustText(entry, stage), thrust.StageBurnText(entry, stage),
                    });
                List<object> rcs = null;
                if (thrust.RcsAt(entry.Step) != null)
                    rcs = new List<object>
                    {
                        "RCS", thrust.RcsCell(entry, info => EngineerFormat.Mass(info.Mass)),
                        thrust.RcsCell(entry, info => EngineerFormat.Isp(info.Isp)),
                        thrust.RcsCell(entry, info => PlanThrust.RcsRatio(info.StartTwr)),
                        thrust.RcsCell(entry, info => PlanThrust.RcsRatio(info.EndTwr)), "",
                        thrust.RcsCell(entry, info => EngineerFormat.Force(info.Thrust)),
                        thrust.RcsCell(entry, info => EngineerFormat.Time(info.Seconds)),
                    };
                var burns = new List<object>();
                for (int burn = 0; burn < entry.Burns.Count; burn++)
                    burns.Add(new List<object> { thrust.BurnNameText(entry, burn), thrust.BurnText(entry, burn) });
                rows.Add(new Dictionary<string, object>
                {
                    { "place", model.StepName(entry.Step) }, { "flyer", model.StepFlyer(entry.Step) }, { "needs", thrust.NeedsText(entry) },
                    { "has", thrust.HasText(entry) }, { "turn", thrust.TurnText(entry) }, { "stages", stageRows }, { "rcs", rcs },
                    { "burns", burns }, { "notes", new List<object>(entry.Notes) }, { "body", thrust.BodyText(entry) },
                });
            }
            return new Dictionary<string, object> { { "summary", thrust.Summary() }, { "steps", rows } };
        }

        // ------------------------------------------------------------- lists

        // The list a button folds out, by what it is for: "body" (stop), "flyer" (leg, role),
        // "tanker" (leg, role), "parts" (section row), "newSection", "deploy" (section row);
        // empty for anything else.
        internal List<Choice> ChoicesFor(string list, int a, int b)
        {
            if (!model.Ready()) return new List<Choice>();
            PlanSections sections = model.Sections;
            switch (list)
            {
                case "body": return model.BodyChoices(a);
                case "flyer": return Enum.IsDefined(typeof(StepRole), b) ? sections.FlyerChoices(a, (StepRole)b) : new List<Choice>();
                case "tanker": return Enum.IsDefined(typeof(StepRole), b) ? model.TankerChoices(a, (StepRole)b) : new List<Choice>();
                case "parts": return sections.RowSection(a) != null ? sections.PartChoices(sections.RowSection(a).Plan) : new List<Choice>();
                case "newSection": return sections.NewSectionChoices(null);
                case "deploy": return sections.DeployChoices(a);
                default: return new List<Choice>();
            }
        }

        // The choice at `index` of that list, where it still reads `label`: the list may have
        // changed since the dashboard showed it.
        internal bool Pick(string list, int a, int b, int index, string label)
        {
            List<Choice> choices = ChoicesFor(list, a, b);
            if (index < 0 || index >= choices.Count || choices[index].Label != label) return false;
            closeLists();
            choices[index].Pick();
            return true;
        }

        // --------------------------------------------------------- changes

        // A change the dashboard asks for, as the planner's button for it makes it: the name and
        // its numbers. False for one it does not know.
        internal bool Change(string name, string[] args)
        {
            if (!model.Ready()) return false;
            int a = Int(args, 0), b = Int(args, 1);
            switch (name)
            {
                case "margin": model.SetMargin((float)Number(args, 0)); return true;
                case "kind": model.SetKind(a, b == (int)StopKind.Surface ? StopKind.Surface : StopKind.Orbit); return true;
                case "altitude": model.SetAltitude(a, args.Length > 1 ? args[1] : ""); return true;
                case "altitudeStep": model.StepAltitude(a, b < 0 ? -1 : 1); return true;
                case "air": model.ToggleAtmosphere(a); return true;
                case "addStop": model.AddStop(); return true;
                case "removeStop": model.RemoveStop(a); return true;
                case "removeSection": model.Sections.RemoveSection(a); return true;
                case "use": model.ToggleSaved(a, b); return true;
                case "drop": model.ToggleDropAfter(a, b); return true;
                case "refuel": if (Enum.IsDefined(typeof(StepRole), b)) model.ToggleRefuel(a, (StepRole)b); return true;
                case "orbitFirst": model.SetOrbitFirst(a, b != 0); return true;
                case "split": model.SetSplitBurns(a, b != 0); return true;
                default: return false;
            }
        }

        // The argument at `at`, rounded to a whole number; -1 where there is none.
        private static int Int(string[] args, int at)
        {
            return (int)Math.Round(Number(args, at));
        }

        // The argument at `at` as a number; -1 where there is none or it is no number.
        private static double Number(string[] args, int at)
        {
            double value;
            return at < args.Length && double.TryParse(args[at], NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : -1;
        }
    }
}
