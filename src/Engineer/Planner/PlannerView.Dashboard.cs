using System;
using System.Collections.Generic;
using System.Globalization;

namespace ReDefinition.Engineer
{
    // The planner as the dashboard's VAB page shows and works it (src/Dashboard/EditorTelemetry.cs):
    // what its rows show, in the texts they show it in, KSP's rich text and all, for the page to
    // draw as the game does; the lists its buttons fold out; and the same changes its buttons make,
    // through the same handlers. Every row is read, folded in the game or not.
    internal sealed partial class PlannerView
    {
        // Set while the dashboard reads the rows: the fuel plan and the thrust part give every
        // step's texts, as if each were open (FuelStepShown, FuelIsOpen, ThrustAt, OpenThrust).
        private bool everyRow;
        private int snapshotVersion = -1;
        private Dictionary<string, object> snapshot;

        // What the rows show, made again only where their texts may have changed (textVersion).
        internal Dictionary<string, object> Snapshot()
        {
            if (snapshot != null && snapshotVersion == textVersion) return snapshot;
            everyRow = true;
            try
            {
                snapshot = MakeSnapshot();
            }
            finally
            {
                everyRow = false;
            }
            snapshotVersion = textVersion;
            return snapshot;
        }

        private Dictionary<string, object> MakeSnapshot()
        {
            var shot = new Dictionary<string, object>
            {
                { "vessel", vessel },
                { "stockOff", EditorReadout.StockOff },
                { "ready", Ready() },
            };
            if (!Ready()) return shot;
            shot["craft"] = VesselText();

            var sections = new List<object>();
            for (int row = 0; row < live.Count && row < MaxSections; row++)
            {
                var section = new Dictionary<string, object>
                {
                    { "row", row }, { "name", SectionLabel(row) }, { "parts", SectionText(row) },
                    { "kind", live[row].Plan.Kind.ToString() }, { "warning", SectionWarning(row) },
                };
                if (IsKind(row, SectionKind.Satellite)) section["deploy"] = DeployText(row);
                sections.Add(section);
            }
            shot["sections"] = sections;
            shot["canAddSection"] = state.Sections.Count < MaxSections;
            shot["margin"] = Math.Round(state.Margin * 100.0);
            shot["planes"] = state.PlaneChanges;

            var stops = new List<object>();
            for (int i = 0; i < state.Stops.Count; i++)
            {
                var stop = new Dictionary<string, object>
                {
                    { "letter", Letter(i) }, { "body", BodyName(i) }, { "hasBody", HasBody(i) }, { "kind", Kind(i).ToString() },
                    { "removable", state.Stops.Count > 2 },
                };
                if (HasBody(i) && Kind(i) == StopKind.Orbit) stop["km"] = AltitudeKm(i);
                if (i > 0 && HasAtmosphere(i))
                    stop["air"] = new Dictionary<string, object> { { "label", Kind(i) == StopKind.Surface ? "Chutes" : "Aerobrake" }, { "on", UsesAtmosphere(i) } };
                var after = new List<object>();
                foreach (StepRole role in Roles)
                    if (Slot(i, role) >= 0) after.Add(StepShot(i, role));
                stop["steps"] = after;
                stops.Add(stop);
            }
            shot["stops"] = stops;
            shot["canAddStop"] = state.Stops.Count < MaxStops && PickedStops().Count == state.Stops.Count;
            shot["nextLetter"] = Letter(state.Stops.Count);
            shot["hasSections"] = HasSections();
            if (!Worked()) return shot;

            shot["summary"] = SummaryText();
            shot["total"] = TotalBar();
            shot["fuel"] = FuelShot();
            shot["thrust"] = ThrustShot();
            return shot;
        }

        // A step on the route: where it goes, who flies it, its bar on the route's scale, its
        // delta-v, the grey lines, its further choices and in red what blocks it.
        private Dictionary<string, object> StepShot(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            double longest = 1.0;
            foreach (double value in budget.Needed) if (value > longest) longest = value;
            var bar = new List<object>();
            foreach (StageBudget.StageUse use in budget.Uses)
                if (use.Leg == index) bar.Add(Share(use.DeltaV / longest, StageColour(use.Stage)));
            if (budget.Short[index] > 0.5) bar.Add(Share(budget.Short[index] / longest, PlannerColors.Short));
            var step = new Dictionary<string, object>
            {
                { "leg", leg }, { "role", (int)role }, { "place", StepPlace(leg, role) }, { "dv", StepSpeed(leg, role) }, { "bar", bar },
                { "separation", SeparationText(leg, role) }, { "deployed", DeployedText(leg, role) },
            };
            if (HasSections()) step["flyer"] = VehicleText(leg, role);
            if (Docks(leg, role))
                step["docks"] = new Dictionary<string, object> { { "refuel", Refuels(leg, role) }, { "tanker", Refuels(leg, role) ? TankerText(leg, role) : "" } };
            if (IntoTheAir(leg, role)) step["orbitFirst"] = OrbitFirst(leg + 1);
            if (role == StepRole.Transfer && CanSplit(leg)) step["split"] = SplitsBurns(leg + 1);
            var lines = new List<object>();
            for (int line = 0; line < MaxWarnings; line++)
            {
                string text = WarningText(leg, role, line);
                if (text.Length > 0) lines.Add(text);
            }
            step["warnings"] = lines;
            return step;
        }

        private static Dictionary<string, object> Share(double share, UnityEngine.Color colour)
        {
            return new Dictionary<string, object> { { "share", Math.Round(share, 4) }, { "color", PlannerColors.Hex(colour) } };
        }

        // The mission's bar as UpdateBars draws it: the stages' share, what is left, what is missing.
        private List<object> TotalBar()
        {
            var parts = new List<KeyValuePair<double, UnityEngine.Color>>();
            for (int i = 0; i < stages.Count; i++)
            {
                double used = 0.0;
                foreach (StageBudget.StageUse use in budget.Uses) if (use.Stage == stages[i].Number) used += use.DeltaV;
                if (used > 0.5) parts.Add(new KeyValuePair<double, UnityEngine.Color>(used, PlannerColors.Stage(i)));
            }
            if (budget.TotalLeft > 0.5) parts.Add(new KeyValuePair<double, UnityEngine.Color>(budget.TotalLeft, PlannerColors.Spare));
            if (budget.TotalShort > 0.5) parts.Add(new KeyValuePair<double, UnityEngine.Color>(budget.TotalShort, PlannerColors.Short));
            double sum = 0.0;
            foreach (KeyValuePair<double, UnityEngine.Color> part in parts) sum += part.Key;
            double scale = Math.Max(1.0, Math.Max(sum, Math.Max(TotalNeeded(), TotalHas())));
            var bar = new List<object>();
            foreach (KeyValuePair<double, UnityEngine.Color> part in parts) bar.Add(Share(part.Key / scale, part.Value));
            return bar;
        }

        // The fuel plan: every step with what it needs and leaves, its stages with Use and Drop
        // after, its burns and the margin, and what blocks it.
        private Dictionary<string, object> FuelShot()
        {
            var rows = new List<object>();
            for (int step = 0; step < steps.Count; step++)
            {
                var stageRows = new List<object>();
                int count = FuelStageCount(step);
                for (int row = 0; row < count; row++)
                {
                    StageBudget.StepStage stage = FuelStage(step, row);
                    if (stage == null) continue;
                    stageRows.Add(new Dictionary<string, object>
                    {
                        { "number", stage.Stage }, { "name", ColouredStage(stage.Stage) }, { "color", PlannerColors.Hex(StageColour(stage.Stage)) },
                        { "gives", StageGivesText(step, row) }, { "keeps", StageKeepsText(step, row) }, { "twr", StageTwrText(step, row) },
                        { "use", Burnable(step, row) }, { "canUse", !AirStage(stage.Stage) },
                        { "drop", stage.CanDrop && DropsAfter(step, stage.Stage) },
                        { "canDrop", !AirStage(stage.Stage) && (stage.CanDrop || DropsAfter(step, stage.Stage)) },
                    });
                }
                var lines = new List<object>();
                foreach (KeyValuePair<string, string> line in FuelLines(step)) lines.Add(new List<object> { line.Key, line.Value });
                var warningLines = new List<object>();
                for (int line = 0; line < MaxWarnings; line++)
                {
                    string text = FuelWarning(step, line);
                    if (text.Length > 0) warningLines.Add(text);
                }
                RcsInfo rcs = RcsAt(step);
                rows.Add(new Dictionary<string, object>
                {
                    { "place", FuelPlace(step) }, { "flyer", FuelFlyer(step) }, { "needs", FuelNeedsText(step) }, { "left", FuelLeftText(step) },
                    { "stages", stageRows }, { "rcs", rcs != null ? EngineerFormat.Speed(rcs.DeltaV) : null }, { "lines", lines },
                    { "warnings", warningLines },
                });
            }
            return new Dictionary<string, object> { { "summary", FuelSummary() }, { "steps", rows } };
        }

        // The thrust part: every step that burns, with what it needs and has and how fast it
        // turns; its stages, its RCS, its burns, its notes and the body.
        private Dictionary<string, object> ThrustShot()
        {
            var rows = new List<object>();
            for (int row = 0; row < thrustSteps.Count; row++)
            {
                ThrustStep entry = OpenThrust(row);
                if (entry == null) continue;
                var stageRows = new List<object>();
                for (int stage = 0; stage < entry.Uses.Count; stage++)
                    stageRows.Add(new List<object>
                    {
                        ThrustStageName(entry, stage), StageMassText(entry, stage), StageIspText(entry, stage), TwrValue(entry, stage, true, false),
                        TwrValue(entry, stage, false, false), TwrValue(entry, stage, true, true), StageThrustText(entry, stage),
                        StageBurnText(entry, stage),
                    });
                List<object> rcs = null;
                if (RcsAt(entry.Step) != null)
                    rcs = new List<object>
                    {
                        "RCS", RcsCell(entry, info => EngineerFormat.Mass(info.Mass)), RcsCell(entry, info => EngineerFormat.Isp(info.Isp)),
                        RcsCell(entry, info => RcsRatio(info.StartTwr)), RcsCell(entry, info => RcsRatio(info.EndTwr)), "",
                        RcsCell(entry, info => EngineerFormat.Force(info.Thrust)), RcsCell(entry, info => EngineerFormat.Time(info.Seconds)),
                    };
                var burns = new List<object>();
                for (int burn = 0; burn < entry.Burns.Count; burn++) burns.Add(new List<object> { BurnName_(entry, burn), BurnText(entry, burn) });
                rows.Add(new Dictionary<string, object>
                {
                    { "place", StepName(entry.Step) }, { "flyer", StepFlyer(entry.Step) }, { "needs", ThrustNeedsText(entry) },
                    { "has", ThrustHasText(entry) }, { "turn", TurnText(entry) }, { "stages", stageRows }, { "rcs", rcs }, { "burns", burns },
                    { "notes", new List<object>(entry.Notes) }, { "body", ThrustBodyText(entry) },
                });
            }
            return new Dictionary<string, object> { { "summary", ThrustSummary() }, { "steps", rows } };
        }

        // ------------------------------------------------------------- lists

        // The list a button folds out, by what it is for: "body" (stop), "flyer" (leg, role),
        // "tanker" (leg, role), "parts" (section row), "newSection", "deploy" (section row);
        // empty for anything else.
        internal List<Choice> ChoicesFor(string list, int a, int b)
        {
            if (!Ready()) return new List<Choice>();
            switch (list)
            {
                case "body": return BodyChoices(a);
                case "flyer": return Enum.IsDefined(typeof(StepRole), b) ? FlyerChoices(a, (StepRole)b) : new List<Choice>();
                case "tanker": return Enum.IsDefined(typeof(StepRole), b) ? TankerChoices(a, (StepRole)b) : new List<Choice>();
                case "parts": return RowSection(a) != null ? PartChoices(RowSection(a).Plan) : new List<Choice>();
                case "newSection": return NewSectionChoices(null);
                case "deploy": return DeployChoices(a);
                default: return new List<Choice>();
            }
        }

        // The choice at `index` of that list, where it still reads `label`: the list may have
        // changed since the dashboard showed it.
        internal bool Pick(string list, int a, int b, int index, string label)
        {
            List<Choice> choices = ChoicesFor(list, a, b);
            if (index < 0 || index >= choices.Count || choices[index].Label != label) return false;
            Dropdown.Close(this);
            choices[index].Pick();
            return true;
        }

        // --------------------------------------------------------- changes

        // A change the dashboard asks for, as the planner's button for it makes it: the name and
        // its numbers. False for one it does not know.
        internal bool Change(string name, string[] args)
        {
            if (!Ready()) return false;
            int a = Int(args, 0), b = Int(args, 1);
            switch (name)
            {
                case "margin": SetMargin((float)Number(args, 0)); return true;
                case "planes": SetPlaneChanges(a != 0); return true;
                case "kind": SetKind(a, b == (int)StopKind.Surface ? StopKind.Surface : StopKind.Orbit); return true;
                case "altitude": SetAltitude(a, args.Length > 1 ? args[1] : ""); return true;
                case "altitudeStep": StepAltitude(a, b < 0 ? -1 : 1); return true;
                case "air": ToggleAtmosphere(a); return true;
                case "addStop": AddStop(); return true;
                case "removeStop": RemoveStop(a); return true;
                case "removeSection": RemoveSection(a); return true;
                case "use": ToggleSaved(a, b); return true;
                case "drop": ToggleDropAfter(a, b); return true;
                case "refuel": if (Enum.IsDefined(typeof(StepRole), b)) ToggleRefuel(a, (StepRole)b); return true;
                case "orbitFirst": SetOrbitFirst(a, b != 0); return true;
                case "split": SetSplitBurns(a, b != 0); return true;
                default: return false;
            }
        }

        private static int Int(string[] args, int at)
        {
            return (int)Math.Round(Number(args, at));
        }

        private static double Number(string[] args, int at)
        {
            double value;
            return at < args.Length && double.TryParse(args[at], NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : -1;
        }
    }
}
