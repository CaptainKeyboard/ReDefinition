using System;
using System.Collections.Generic;
using System.Text;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // What the settings window edits and shows, without any game UI: the groups and their rows from the key table, the state of each row
    // (open, or greyed out with its reason), the tooltip, the changes and Reset. The window (SettingsWindow) draws this and nothing else,
    // so that every rule of it is tested outside the game.
    internal sealed class WindowModel
    {
        private readonly Action save;
        private readonly Action<QualityTier> markTier;
        private readonly Func<List<BodyRow>> bodies;
        private bool dirty;

        // The environment is read again by Refresh (the optional mods are there from the start, the host's caps change).
        internal WindowEnvironment Env;

        // save writes settings.cfg; markTier notes the tier whose row was written (profile.cfg); bodies lists the bodies.
        internal WindowModel(WindowEnvironment env, Action save, Action<QualityTier> markTier, Func<List<BodyRow>> bodies)
        {
            Env = env ?? WindowEnvironment.Alone();
            this.save = save ?? (() => { });
            this.markTier = markTier ?? (tier => { });
            this.bodies = bodies ?? (() => new List<BodyRow>());
        }

        // The model of the game: the live environment, the settings file, the marker, the bodies of the game.
        internal static WindowModel ForGame()
        {
            return new WindowModel(WindowEnvironment.Detect(), AtmospheresSettings.Save,
                tier => QualityProfileModule.WriteMarker(AtmospheresSettings.Mod, tier), GameBodies);
        }

        // The groups in the order of the key table, those that have rows.
        internal List<string> Categories()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (SettingDef def in AtmospheresSettings.Definitions) seen.Add(def.Category);
            var list = new List<string>();
            foreach (string category in AtmospheresSettings.Categories.All)
            {
                if (seen.Contains(category)) list.Add(category);
            }
            return list;
        }

        // The rows of a group in the order of the key table.
        internal List<SettingDef> Rows(string category)
        {
            var rows = new List<SettingDef>();
            foreach (SettingDef def in AtmospheresSettings.Definitions)
            {
                if (def.Category == category) rows.Add(def);
            }
            return rows;
        }

        internal RowState State(SettingDef def)
        {
            return Dependencies.Evaluate(def, AtmospheresSettings.Number, Env);
        }

        internal string Value(SettingDef def)
        {
            return def.Kind == SettingKind.Text ? AtmospheresSettings.Text(def.Key) : ValueFormat.Of(def, AtmospheresSettings.Number(def.Key));
        }

        // The tooltip of a row: what it does, the default and range, why it is greyed out, and what is in force where the value cannot run.
        internal string Tooltip(SettingDef def)
        {
            var text = new StringBuilder();
            text.Append(SettingHelp.For(def.Key));
            string range = ValueFormat.Range(def);
            text.Append("\nDefault: ").Append(ValueFormat.DefaultOf(def));
            if (range.Length > 0) text.Append(". Range: ").Append(range);
            text.Append('.');
            RowState state = State(def);
            if (!state.Enabled) text.Append("\nGreyed out: ").Append(state.Reason);
            if (state.Note.Length > 0) text.Append('\n').Append(state.Note);
            if (def.Key == AtmospheresSettings.Keys.QualityTier) text.Append('\n').Append(TierLine());
            return ReDefinition.Ui.TooltipText.Wrap(text.ToString());
        }

        // The tooltip of one choice of a row.
        internal string ChoiceTooltip(SettingDef def, int index)
        {
            string why = Dependencies.ChoiceUnavailable(def, index, Env);
            return why == null ? "" : ReDefinition.Ui.TooltipText.Wrap("Not offered: " + SettingHelp.ReasonText(why));
        }

        // The tier in force and how it was reached, for the quality row.
        internal string TierLine()
        {
            QualityTier requested = AtmospheresSettings.QualityTier;
            QualityTier effective = QualityProfile.Effective();
            if (requested != QualityTier.Auto) return "In force: " + effective + ".";
            long vram = QualityProfile.Vram();
            if (vram > 0 && vram < QualityProfile.LowVramBelowMb) return "In force: " + effective + " (Auto: under 10 GB of video memory).";
            return QualityProfile.HostTier() != QualityTier.Auto
                ? "In force: " + effective + " (Auto: from the main mod's profile)."
                : "In force: " + effective + " (Auto: no main mod profile, so Medium).";
        }

        // Sets a number, switch or choice; false where the row is greyed out or the value is not offered. A change of the tier writes the
        // budget keys of its row.
        internal bool Set(SettingDef def, double value)
        {
            if (def.Kind == SettingKind.Text) return false;
            if (!State(def).Enabled) return false;
            if (def.Kind == SettingKind.Choice && Dependencies.ChoiceUnavailable(def, (int)Math.Round(value), Env) != null) return false;
            if (!AtmospheresSettings.SetNumber(def.Key, value)) return false;
            if (def.Key == AtmospheresSettings.Keys.QualityTier) ApplyTier();
            dirty = true;
            SettingChanges.Raise(def.Key);
            return true;
        }

        internal bool SetText(SettingDef def, string value)
        {
            if (def.Kind != SettingKind.Text || !State(def).Enabled) return false;
            if (value == AtmospheresSettings.Text(def.Key)) return true;
            if (!AtmospheresSettings.SetText(def.Key, value)) return false;
            dirty = true;
            SettingChanges.Raise(def.Key);
            return true;
        }

        // Writes the budget keys of the tier in force.
        internal QualityTier ApplyTier()
        {
            QualityTier effective = QualityProfile.Effective();
            QualityProfile.Apply(effective);
            markTier(effective);
            dirty = true;
            return effective;
        }

        // Reset: every setting to its default, foreign settings this product changed back, the tier's row, the bodies' own numbers.
        internal QualityTier Reset()
        {
            QualityTier tier = QualityProfile.Reset();
            markTier(tier);
            ResetBodies();
            dirty = true;
            Flush();
            SettingChanges.Raise(SettingChanges.All);
            return tier;
        }

        // Saves settings.cfg and bodies.cfg where something changed.
        internal void Flush()
        {
            if (bodiesDirty)
            {
                bodiesDirty = false;
                writeBodies(bodiesOptions);
            }
            if (!dirty) return;
            dirty = false;
            save();
        }

        internal bool Dirty
        {
            get { return dirty; }
        }

        // The status line of the wind.
        internal string WindLine()
        {
            return WindStatus.Text(AtmospheresSettings.WindSource, Env.ForeignWindOwner);
        }

        internal string InjectionLine()
        {
            return WindStatus.InjectionText(AtmospheresSettings.WindInjection, Env.ModularFlightIntegrator, AtmospheresSettings.WindForces);
        }

        // --- Bodies ---

        // The numbers set in the window and not yet written; null until a body is changed.
        private Dictionary<string, BodyPlayerOptions> bodiesOptions;
        private bool bodiesDirty;

        // The rows of the bodies, each with the player's numbers (those set in the window included).
        internal List<BodyRow> BodyList()
        {
            List<BodyRow> rows = bodies();
            if (bodiesOptions == null) return rows;
            foreach (BodyRow row in rows)
            {
                BodyPlayerOptions own;
                if (!bodiesOptions.TryGetValue(row.Name, out own)) own = BodyPlayerOptions.Default;
                row.YearFactor = own.YearFactor;
                row.Frequency = own.Frequency;
            }
            return rows;
        }

        // Sets the year factor and the frequency of a body; bodies.cfg is written with the next Flush, as a slider moves many times.
        internal void SetBody(string name, double yearFactor, double frequency)
        {
            if (bodiesOptions == null) bodiesOptions = readBodies();
            bodiesOptions = BodyRows.With(bodiesOptions, name, yearFactor, frequency);
            bodiesDirty = true;
        }

        // As above, and the row the window holds shows the numbers now in force (brought into their ranges).
        internal void SetBody(BodyRow row, double yearFactor, double frequency)
        {
            SetBody(row.Name, yearFactor, frequency);
            var own = new BodyPlayerOptions(yearFactor, frequency);
            row.YearFactor = own.YearFactor;
            row.Frequency = own.Frequency;
        }

        private void ResetBodies()
        {
            bodiesOptions = new Dictionary<string, BodyPlayerOptions>(StringComparer.OrdinalIgnoreCase);
            bodiesDirty = false;
            writeBodies(bodiesOptions);
        }

        private Func<Dictionary<string, BodyPlayerOptions>> readBodies = BodyRows.Read;
        private Action<IDictionary<string, BodyPlayerOptions>> writeBodies = options => BodyRows.Write(options);

        // The reading and writing of bodies.cfg, for the tests.
        internal void UseBodiesFile(Func<Dictionary<string, BodyPlayerOptions>> read, Action<IDictionary<string, BodyPlayerOptions>> write)
        {
            readBodies = read;
            writeBodies = write;
        }

        private static List<BodyRow> GameBodies()
        {
            var climates = new List<BodyClimate>();
            if (FlightGlobals.Bodies != null)
            {
                foreach (CelestialBody body in FlightGlobals.Bodies)
                {
                    BodyClimate climate = Bodies_Get(body);
                    if (climate != null) climates.Add(climate);
                }
            }
            return BodyRows.Build(climates, BodyRows.Read(), GameReadiness);
        }

        private static BodyClimate Bodies_Get(CelestialBody body)
        {
            try
            {
                return Atmospheres.Bodies.Get(body);
            }
            catch (Exception e)
            {
                AtmospheresLog.WarnOnce("the climate of " + body.name + " could not be read for the window (" + CompatibilityLog.Reason(e) + ")");
                return null;
            }
        }

        // How far the simulation of a body is along: the whole field's state until the generator reports per body (C25).
        internal static Func<int, FieldReadiness> ReadinessSource = index => WeatherHub.Field.Readiness;

        private static FieldReadiness GameReadiness(int index)
        {
            return ReadinessSource(index);
        }
    }
}
