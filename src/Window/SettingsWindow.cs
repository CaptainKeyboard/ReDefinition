using System.Collections.Generic;
using System.Globalization;
using System;
using KSP.Localization;
using ReDefinition.Bridges;
using ReDefinition.Core;
using ReDefinition.Settings;
using UnityEngine.UI;
using UnityEngine;

namespace ReDefinition.Window
{
    // One window for the settings -- KSP's own, ReDefinition's and those of the
    // mods bundled here -- sorted by feature, as KSP's own settings screen sorts,
    // with the mod a setting comes from named beside it.
    //
    // Built like KSP's in-game settings dialog (MiniSettings, decompiled): a
    // MultiOptionDialog in the MiniSettingsSkin, section buttons in the skin's
    // first custom style, a scroll list, and Apply, Accept and Cancel with
    // KSP's own strings -- so ZTheme, which replaces KSP's UI textures by
    // name, themes it with the rest of KSP's dialogs.
    //
    // It edits copies, as KSP's dialog does: the upscaler's settings in an
    // OwnSettings copy committed through ReDefinitionAddon.Apply, the other
    // mods' values in the edit model (SettingsEdit) committed through
    // BundledSettings. Cancel leaves everything as it was. This is the view:
    // what the rows hold, where their values came from and what Apply does with
    // them are the model's.
    internal static partial class SettingsWindow
    {
        private const float WindowWidth = 740f;
        private const float WindowHeight = 540f;
        private const float TabWidth = 170f;
        private const float PageWidth = 540f;
        private const float PageHeight = 440f;
        private const float NameWidth = 190f;
        private const float ControlWidth = 130f;
        private const float ValueWidth = 60f;
        private const float SourceWidth = 70f;
        private const float RowHeight = 18f;
        private const float ResetWidth = 80f;
        private const float RestartWidth = 140f;

        private static PopupDialog dialog;
        private static SettingCategory current = SettingCategory.Profiles;
        private static KspSettingsSection.Edit edit;

        // The bundled settings' rows: their values, where each came from, and
        // Apply's steps.
        private static readonly SettingsEdit model = new SettingsEdit { Offered = OfferChoice };

        // The graphics profiles, Low to Max, read once per opening.
        private static List<GraphicsProfile> profiles = new List<GraphicsProfile>();

        // The settings the window has a row for: what SyncFromMods follows, with
        // every setting of a mod whose own window is open.
        private static readonly HashSet<string> shownKeys = new HashSet<string>();

        // Mods whose every setting the next sync reads: KSP's, once its own
        // settings screen has applied (FollowModSoon).
        private static readonly HashSet<string> dueMods = new HashSet<string>();

        public static bool Visible { get { return dialog != null; } }

        // Whether something in the window waits for Accept.
        internal static bool HasUnapplied { get { return dialog != null && Unapplied(); } }

        // Whether a text field of this window -- the Keys tab's search -- has the
        // keyboard (KeyCapture keeps the game's keys from it meanwhile).
        internal static bool TextFieldFocused()
        {
            if (dialog == null) return false;
            // The search field, left selected after Return: Space or Return would
            // open it again.
            UiHygiene.ReleaseEnded(dialog.gameObject);
            UnityEngine.EventSystems.EventSystem events = UnityEngine.EventSystems.EventSystem.current;
            GameObject selected = events != null ? events.currentSelectedGameObject : null;
            if (selected == null || !selected.transform.IsChildOf(dialog.transform)) return false;
            TMPro.TMP_InputField field = selected.GetComponent<TMPro.TMP_InputField>();
            return field != null && field.isFocused;
        }

        public static void Toggle()
        {
            if (Visible) Close();
            else Open();
        }

        // The window open on a tab, built anew where it is open already: for the probe's shots
        // (-redefinitionProbeSettingsWindow), which show a setting after the probe changed it.
        internal static void OpenOn(SettingCategory category)
        {
            if (Visible) Close();
            current = category;
            Open();
        }

        public static void Close()
        {
            // The flight goes on as it was before this window held it.
            WindowPause.Release();
            // A row still listening would keep the game's controls locked.
            KeyCapture.Stop();
            AxisCapture.Stop();
            if (dialog != null) dialog.Dismiss();
            dialog = null;
        }

        private static void Open()
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon == null) return;
            KspUiState.ResyncShown();

            try
            {
                OwnSettings now = addon.Current();
                // ReDefinition's own rows follow the profile the other rows were filled from,
                // applied or not.
                edit = new KspSettingsSection.Edit
                {
                    Before = now,
                    After = now.Clone(),
                    ProfileChosen = () => !string.IsNullOrEmpty(model.Profile),
                };
                ReadBundled();
                LoadProfiles();

                UISkinDef skin = UISkinManager.GetSkin("MiniSettingsSkin") ?? HighLogic.UISkin;
                DialogGUIBase[] content = Build(skin);
                TooltipText.WrapAll(content);
                UiHygiene.DeferInteractable(content);
                MultiOptionDialog window = new MultiOptionDialog("ReDefinitionSettings", "", "Settings -- ReDefinition",
                    skin, new Rect(0.5f, 0.5f, WindowWidth, WindowHeight), content);
                dialog = PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), window,
                    false, skin, false);
                DialogTooltips.Attach(content);
                // Accept and Cancel dismiss the dialog themselves, and
                // PopupDialog.Dismiss does not call OnDismiss -- only Escape
                // does, from PopupDialog.Update (decompiled). Its onDestroy is
                // the one event every way out of the window passes, a scene
                // change included. It runs a frame later, once the object is
                // gone, and only for the dialog it belongs to: a window built
                // anew in the same frame has its own.
                PopupDialog spawned = dialog;
                UnityEngine.Events.UnityAction gone = () =>
                {
                    if (dialog != spawned) return;
                    dialog = null;
                    KeyCapture.Stop();
                    AxisCapture.Stop();
                    WindowPause.Release();
                };
                spawned.onDestroy.AddListener(gone);
                spawned.OnDismiss = () => gone();
                UnityMouseEvents.Shield(dialog);
                ScreenFit.Add(dialog);
                EscapeGuard.Own(dialog);
                UiHygiene.MouseOnly(dialog.gameObject);
                WindowPause.PlaceInTitleRow(dialog);
                // See-through only where a flight runs behind the window; in the
                // main menu, the space centre, the tracking station and the
                // editors, and while it holds the flight, it is drawn nearly opaque.
                WindowBackdrop.Add(dialog, !WindowPause.FlightRunsBehind);
                ScrollThrough.Apply(dialog.popupWindow);
                WindowPause.Refresh();
            }
            catch (Exception e)
            {
                dialog = null;
                Debug.LogWarning(Log.Tag + " The settings window could not be opened: " + e);
            }
        }

        // What the controls start from: each mod's value as it is now, the one
        // ReDefinition keeps where the mod cannot take it now (BundledSettings.Current). After Apply and
        // Restore too, where the rows are built already.
        private static void ReadBundled()
        {
            model.Open(BundledSettings.Enabled, BundledSettings.ProfileName, CurrentValues());
            model.OpenButtons(ButtonStates());
        }

        private static IEnumerable<KeyValuePair<string, string>> CurrentValues()
        {
            foreach (IBundledMod mod in BundledSettings.Installed())
            {
                foreach (BundledSetting setting in mod.Settings)
                    yield return new KeyValuePair<string, string>(setting.Key, BundledSettings.Current(setting));
            }
        }

        private static DialogGUIBase[] Build(UISkinDef skin)
        {
            choiceRows.Clear();
            shownKeys.Clear();
            Conflicts.Clear();
            kspPending.Clear();
            axisPending.Clear();
            AxisCapture.Stop();
            ClearLayoutPending();
            // The search field is built empty, and so is what it filters by.
            keySearch = "";
            searchTexts.Clear();
            pageTexts.Clear();
            foldRows.Clear();
            keysIn.Clear();
            pageMatchesFor = null;
            List<DialogGUIBase> tabs = new List<DialogGUIBase> { SearchRow() };
            // Sized by TabScrollList itself: KSP's DialogGUIContentSizer lets go
            // of the height once a page fits.
            List<DialogGUIBase> pages = new List<DialogGUIBase>();

            TabScrollList scroll = null;
            bool currentShown = false;
            if (InDetail(current)) current = SettingCategory.Detail;
            if (current == SettingCategory.General) current = SettingCategory.Display;
            foreach (SettingCategory category in Enum.GetValues(typeof(SettingCategory)))
            {
                if (NoTab(category)) continue;
                building = category;
                List<DialogGUIBase> rows = new List<DialogGUIBase>(Rows(category));
                if (rows.Count == 0) continue;
                MakeSearchable(category, rows);
                rows.Insert(0, PageHeading(category));

                SettingCategory shown = category;
                currentShown |= shown == current;
                // ReDefinition's own tabs set apart from the game's.
                if (shown == SettingCategory.Interface) tabs.Add(TabSeparator());
                tabs.Add(TabEntry(shown, on =>
                {
                    if (!on || current == shown) return;
                    current = shown;
                    if (scroll != null) scroll.ToTop();
                }));

                DialogGUIVerticalLayout page = new DialogGUIVerticalLayout(PageWidth - 30f, -1f, 4f,
                    new RectOffset(8, 24, 8, 8), TextAnchor.UpperLeft, rows.ToArray());
                page.OptionEnabledCondition = () => Searching ? PageMatches(shown) : current == shown;
                pages.Add(page);
            }
            // The category last shown may have nothing here now.
            if (!currentShown) current = SettingCategory.Profiles;

            // Last below the categories, though no page of its own: it opens the
            // diagnostics window.
            tabs.Add(new DialogGUIToggleButton(() => false, "Diagnostics", on => { if (on) ShowDiagnostics(); }, TabWidth,
                28f));

            DialogGUIVerticalLayout pageList = new DialogGUIVerticalLayout(PageWidth - 30f, -1f, 4f,
                new RectOffset(), TextAnchor.UpperLeft, pages.ToArray());
            scroll = new TabScrollList(new Vector2(PageWidth, PageHeight), pageList);
            DialogGUIVerticalLayout tabList = new DialogGUIVerticalLayout(TabWidth, PageHeight, 4f,
                new RectOffset(), TextAnchor.UpperLeft, tabs.ToArray());

            return new DialogGUIBase[]
            {
                // A row of its own here, and moved into the title row once the
                // dialog stands (WindowPause.PlaceInTitleRow).
                WindowPause.Button(),
                new DialogGUIHorizontalLayout(WindowWidth - 20f, PageHeight, 8f, new RectOffset(),
                    TextAnchor.UpperLeft, tabList, scroll),
                new DialogGUIHorizontalLayout(
                    ResetButton(),
                    // Before the space, not beside Accept: Accept hides as it is
                    // clicked, and what stood left of it would move under the
                    // pointer for a second click.
                    RestartButton(),
                    WaitingNotice(WindowWidth - 20f - ResetWidth - RestartWidth - 2f * 80f - 32f),
                    new DialogGUIFlexibleSpace(),
                    PendingButton(Localizer.Format("#autoLOC_149513"), ApplyAndAskRestart, true),
                    PendingButton(Localizer.Format("#autoLOC_174783"), Discard, true),
                    PendingButton(Localizer.Format("#autoLOC_149514"), () => { }, false)),
            };
        }

        // Accept and Cancel while something waits for them, Close otherwise. Accept
        // and Cancel keep the window open; Close closes it.
        private static DialogGUIButton PendingButton(string label, Callback onClick, bool whilePending)
        {
            DialogGUIButton button = new DialogGUIButton(label, onClick, 80f, 30f, !whilePending);
            button.OptionEnabledCondition = () => Unapplied() == whilePending;
            return button;
        }

        // Where a change waits for a restart: applied, then KSP saved where a game
        // is loaded and started again (GameRestart).
        private static DialogGUIButton RestartButton()
        {
            DialogGUIButton button = new DialogGUIButton(
                () => GameRestart.SavesGame ? "Save and restart" : "Apply and restart", ConfirmRestart, RestartWidth, 30f,
                false);
            button.tooltipText = "Applies what the window holds and starts KSP again, for the changes that take effect"
                                 + " only at a start. Where a game is loaded, it is saved first.";
            button.OptionEnabledCondition = () => GameRestart.Available && RestartWaiting();
            return button;
        }

        // KSP closes: asked first.
        private static void ConfirmRestart()
        {
            ConfirmDialog.OverWindow("ReDefinitionRestart", "Restart KSP",
                "What the window holds is applied and KSP starts again."
                + (GameRestart.SavesGame ? " The game is saved first." : ""), 380f,
                GameRestart.SavesGame ? "Save and restart" : "Apply and restart", ApplyAndRestart);
        }

        private static string Title(SettingCategory category)
        {
            switch (category)
            {
                case SettingCategory.Profiles: return "Graphics";
                case SettingCategory.Display: return "Display";
                case SettingCategory.General: return "Upscaling";
                case SettingCategory.Detail: return "Detail";
                case SettingCategory.ShadowsAndReflections: return "Shadows / Reflections";
                case SettingCategory.Planets: return "Planets";
                case SettingCategory.Effects: return "Effects";
                case SettingCategory.Audio: return "Audio";
                case SettingCategory.Gameplay: return "Gameplay";
                case SettingCategory.Devices: return "Devices";
                case SettingCategory.Keys: return "Controls";
                case SettingCategory.Axes: return "Axes";
                // Marked where a mod has settings this window cannot show: the tab says
                // which, and where they are.
                default: return AnyNotShown() ? "Mods and toolbar (!)" : "Mods / Toolbar";
            }
        }

        private static DialogGUIBase[] Rows(SettingCategory category)
        {
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            if (category == SettingCategory.Detail)
            {
                DetailRows(rows);
                return rows.ToArray();
            }
            if (category == SettingCategory.Profiles)
            {
                StringBuilderText profilesText = new StringBuilderText("Graphics profiles");
                foreach (GraphicsProfile profile in profiles) profilesText.Add(profile.Title);
                foreach (DialogGUIBase row in ProfileRows()) rows.Add(Searchable(row, profilesText.Text));
            }
            // ReDefinition's own upscaler and frame generation lead General, laid
            // out like the other rows there, KSP's among them.
            if (category == SettingCategory.General) return new DialogGUIBase[0];
            bool upscalingDone = category != SettingCategory.Display;
            AddTabRows(category, rows);
            AddBundledRows(category, rows, upscalingDone);
            if (rows.Count > 0 && category != SettingCategory.Profiles && category != SettingCategory.Interface
                && category != SettingCategory.Keys)
            {
                DialogGUIBase advanced = OwnWindowRow(category);
                if (advanced != null) rows.Add(advanced);
            }
            return rows.ToArray();
        }

        // The rows of the tabs that hold more than the registrations' settings:
        // Interface, Keys and Axes.
        private static void AddTabRows(SettingCategory category, List<DialogGUIBase> rows)
        {
            if (category == SettingCategory.Interface)
            {
                rows.Add(Searchable(ReplaceRow(), "Replace original settings KSP menu"));
                foreach (DialogGUIBase row in InterfaceRows())
                    rows.Add(Searchable(row, "Mods toolbar bundle hide restore before ReDefinition"));
                AddOwnInterfaceRows(rows);
            }
            if (category == SettingCategory.Keys)
            {
                foreach (DialogGUIBase row in LayoutRows()) rows.Add(Searchable(row, "Keyboard layout"));
                rows.AddRange(KeyRows());
            }
            if (category == SettingCategory.Axes) rows.AddRange(AxisRows());
        }

        // The registrations' settings of a tab, by section, with ReDefinition's own
        // rows in theirs, and the upscaling section where it is not done yet.
        private static void AddBundledRows(SettingCategory category, List<DialogGUIBase> rows, bool upscalingDone)
        {
            // The Keys tab builds its sections itself (KeyRows). A heading goes
            // before the first row of each group a registration names (`section`);
            // in the long tabs the groups open and fold instead (AddFold).
            string section = null;
            bool folding = Folding(category);
            List<DialogGUIBase> fold = new List<DialogGUIBase>();
            bool firstFold = true;
            // ReDefinition's own rows join the group of their section.
            Dictionary<string, List<DialogGUIBase>> own = OwnRowsBySection(category);
            foreach (BundledSetting setting in category == SettingCategory.Keys
                         ? new List<BundledSetting>()
                         : WindowLayout.In(category))
            {
                DialogGUIBase row = Searchable(BundledRow(setting), setting.Title + " " + setting.Owner.ModName);
                if (row == null) continue;
                shownKeys.Add(setting.Key);
                NoteKey(category, setting.Key);
                bool newSection = setting.Section != null && setting.Section != section;
                if (folding)
                {
                    if (newSection && fold.Count > 0)
                    {
                        fold.AddRange(TakeOwn(own, section));
                        AddFold(rows, category, section, fold, firstFold);
                        firstFold = false;
                        fold = new List<DialogGUIBase>();
                    }
                    section = setting.Section ?? section ?? Title(category);
                    fold.Add(row);
                    continue;
                }
                // Display's upscaling section follows its screen section.
                if (newSection && !upscalingDone && section == "Screen")
                {
                    AddUpscaling(rows);
                    upscalingDone = true;
                }
                if (newSection) rows.Add(SectionHeading(setting.Section));
                section = setting.Section;
                rows.Add(row);
            }
            if (!upscalingDone) AddUpscaling(rows);
            if (folding && fold.Count > 0)
            {
                fold.AddRange(TakeOwn(own, section));
                AddFold(rows, category, section, fold, firstFold);
                firstFold = false;
            }
            foreach (KeyValuePair<string, List<DialogGUIBase>> left in own)
            {
                if (folding)
                {
                    AddFold(rows, category, left.Key, left.Value, firstFold);
                    firstFold = false;
                    continue;
                }
                rows.Add(SectionHeading(left.Key));
                rows.AddRange(left.Value);
            }
        }

        // ReDefinition's own rows, laid out as the bundled mods' are.
        private static KspSettingsSection.Layout OwnLayout()
        {
            return new KspSettingsSection.Layout
            {
                Name = NameWidth,
                Control = ControlWidth,
                ValueGap = 10f,
                Value = ValueWidth,
                Source = "ReDefinition",
                SourceWidth = SourceWidth,
                Marked = Marked,
                Default = OwnDefault,
            };
        }

        // ReDefinition's own rows of a tab other than General and Mods / Toolbar,
        // by the section they join (OurModules: Row, Section): the flight UI's in
        // Gameplay, with KSP's Flight group.
        private static Dictionary<string, List<DialogGUIBase>> OwnRowsBySection(SettingCategory category)
        {
            Dictionary<string, List<DialogGUIBase>> bySection = new Dictionary<string, List<DialogGUIBase>>();
            // Not the Keys tab either: its rows are bindings, which it builds
            // itself (KeyRows), and no control of this section.
            if (edit == null || category == SettingCategory.General || category == SettingCategory.Interface
                || category == SettingCategory.Keys)
                return bySection;
            List<ModuleSetting> settings = OurModules.Rows(category);
            DialogGUIBase[] rows = settings.Count == 0 ? new DialogGUIBase[0] : KspSettingsSection.Rows(edit, OwnLayout(), category);
            for (int i = 0; i < rows.Length && i < settings.Count; i++)
                AddOwn(bySection, settings[i].Section ?? Title(category), rows[i], settings[i].Title);
            // The dialogs' buttons after the settings of their section.
            foreach (ModuleDialog dialog in OurModules.DialogRows(category))
                AddOwn(bySection, dialog.Section ?? Title(category), OwnDialogRow(dialog), dialog.Title);
            return bySection;
        }

        private static void AddOwn(Dictionary<string, List<DialogGUIBase>> bySection, string section, DialogGUIBase row,
            string title)
        {
            List<DialogGUIBase> list;
            if (!bySection.TryGetValue(section, out list)) bySection[section] = list = new List<DialogGUIBase>();
            list.Add(Searchable(row, title + " " + section + " ReDefinition"));
        }

        // An own dialog's button where a row's control stands, as a mod's DIALOG row has it.
        private static DialogGUIBase OwnDialogRow(ModuleDialog dialog)
        {
            DialogGUIButton open = new DialogGUIButton(dialog.Button, () => OpenOwnDialog(dialog), ControlWidth,
                RowHeight + 6f, false);
            open.tooltipText = dialog.Tooltip;
            return new DialogGUIHorizontalLayout(0f, RowHeight + 6f, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel(dialog.Title, NameWidth), open, new DialogGUISpace(10f),
                new DialogGUILabel("", ValueWidth),
                new DialogGUILabel("<color=#9a9a9a>ReDefinition</color>", SourceWidth));
        }

        private static void OpenOwnDialog(ModuleDialog dialog)
        {
            DialogGUIBase[] content;
            try
            {
                content = dialog.Content();
            }
            catch (Exception e)
            {
                Debug.LogError(Log.Tag + " The dialog " + dialog.Title + " could not be opened: " + e);
                return;
            }
            if (content != null && content.Length > 0) OpenDialogOverWindow(dialog.Key, dialog.Heading ?? dialog.Title, dialog.Width, content);
        }

        private static List<DialogGUIBase> TakeOwn(Dictionary<string, List<DialogGUIBase>> own, string section)
        {
            List<DialogGUIBase> rows;
            if (section == null || !own.TryGetValue(section, out rows)) return new List<DialogGUIBase>();
            own.Remove(section);
            return rows;
        }

        // Whether one of ReDefinition's own rows in a tab waits for Accept; under
        // Mods / Toolbar, Better Time Warp's button among the toolbar's too.
        private static bool OwnPending(SettingCategory category, bool detail = false)
        {
            if (edit == null || edit.Before == null || edit.After == null) return false;
            if (category == SettingCategory.Interface && edit.Before.TimeWarpToolbarButton != edit.After.TimeWarpToolbarButton)
                return true;
            foreach (ModuleSetting setting in OurModules.Rows(category, detail))
                if (setting.Read(edit.Before) != setting.Read(edit.After)) return true;
            return false;
        }

        // Display's ReDefinition part, in three groups -- Upscaling, Frame generation, Raytracing -- each with
        // the settings that belong to it, and under a technique what can be downloaded or installed to
        // enable another (NVIDIA's, Intel's and the RTX 30 package's rows); then the rows other mods
        // register as General.
        private static void AddUpscaling(List<DialogGUIBase> rows)
        {
            DialogGUIBase[] own = KspSettingsSection.Rows(edit, OwnLayout());
            // One row per module setting, in the same order (KspSettingsSection.Rows).
            List<ModuleSetting> titles = new List<ModuleSetting>(OurModules.Rows(SettingCategory.General, false));
            string section = null;
            for (int i = 0; i < own.Length; i++)
            {
                ModuleSetting setting = i < titles.Count ? titles[i] : null;
                string group = setting != null && setting.Section != null ? setting.Section : "Upscaling";
                if (group != section)
                {
                    rows.Add(SectionHeading(group));
                    section = group;
                }
                rows.Add(Searchable(own[i], (setting != null ? setting.Title : "Upscaler") + " " + group + " ReDefinition"));
                if (setting == null || setting.Key != "technique") continue;
                // Under the technique row: what a download that was chosen came to, while it runs and after.
                if (setting.Title == "Upscaler")
                    AddHint(rows, StatusLine(() => JoinStatus(NvidiaStatus(), IntelStatus())), "download NVIDIA Intel files");
                else if (setting.Title == "Frame generation")
                {
                    AddHint(rows, StatusLine(() => JoinStatus(NvidiaStatus(), IntelStatus(), AmpereFrameGeneration.StatusText())),
                        "download NVIDIA Intel files dlssg_for_sm86");
                    // The package's preset, once it is in place.
                    if (AmpereFrameGeneration.Installed())
                        foreach (DialogGUIBase preset in AmpereOptionRows())
                            AddHint(rows, preset, "DLSS-G preset dlssg_for_sm86");
                }
            }
            bool others = false;
            foreach (BundledSetting setting in WindowLayout.In(SettingCategory.General))
            {
                DialogGUIBase row = Searchable(BundledRow(setting), setting.Title + " " + setting.Owner.ModName);
                if (row == null) continue;
                if (!others)
                {
                    rows.Add(SectionHeading("Other mods"));
                    others = true;
                }
                shownKeys.Add(setting.Key);
                NoteKey(SettingCategory.Display, setting.Key);
                rows.Add(row);
            }
        }

        private static void AddHint(List<DialogGUIBase> rows, DialogGUIBase hint, string searchText)
        {
            if (hint != null) rows.Add(Searchable(hint, searchText));
        }

        // Detail: KSP's render quality, textures, lights and antialiasing, then
        // shadows and reflections, planets and effects -- each a group that
        // opens and folds, with the Advanced buttons of the mods it holds.
        private static void DetailRows(List<DialogGUIBase> rows)
        {
            bool first = true;
            foreach (KeyValuePair<SettingCategory, string> group in new[]
                     {
                         new KeyValuePair<SettingCategory, string>(SettingCategory.Detail, "Quality"),
                         new KeyValuePair<SettingCategory, string>(SettingCategory.ShadowsAndReflections,
                             "Shadows / Reflections"),
                         new KeyValuePair<SettingCategory, string>(SettingCategory.Planets, "Planets"),
                         new KeyValuePair<SettingCategory, string>(SettingCategory.Effects, "Effects"),
                     })
            {
                List<DialogGUIBase> members = new List<DialogGUIBase>();
                foreach (BundledSetting setting in WindowLayout.In(group.Key))
                {
                    DialogGUIBase row = Searchable(BundledRow(setting), setting.Title + " " + setting.Owner.ModName);
                    if (row == null) continue;
                    shownKeys.Add(setting.Key);
                    NoteKey(SettingCategory.Detail, setting.Key);
                    members.Add(row);
                }
                if (members.Count == 0) continue;
                DialogGUIBase advanced = OwnWindowRow(group.Key);
                if (advanced != null) members.Add(Searchable(advanced, "Advanced " + group.Value));
                AddFold(rows, SettingCategory.Detail, group.Value, members, first);
                first = false;
            }

            // ReDefinition's own single settings of the raytracing.
            if (edit == null) return;
            List<ModuleSetting> own = OurModules.Rows(SettingCategory.General, true);
            if (own.Count == 0) return;
            DialogGUIBase[] ownRows = KspSettingsSection.Rows(edit, OwnLayout(), SettingCategory.General, true);
            List<DialogGUIBase> raytracing = new List<DialogGUIBase>();
            for (int i = 0; i < ownRows.Length && i < own.Count; i++)
                raytracing.Add(Searchable(ownRows[i], own[i].Title + " Raytracing ReDefinition"));
            AddFold(rows, SettingCategory.Detail, "Raytracing", raytracing, first);
        }

        // Texts joined for the search.
        private sealed class StringBuilderText
        {
            private readonly System.Text.StringBuilder text;

            public StringBuilderText(string start)
            {
                text = new System.Text.StringBuilder(start);
            }

            public void Add(string more)
            {
                if (!string.IsNullOrEmpty(more)) text.Append(' ').Append(more);
            }

            public string Text
            {
                get { return text.ToString(); }
            }
        }

        // A group's heading within a tab, as KSP's own screen heads its groups.
        private static DialogGUIBase SectionHeading(string title)
        {
            return new DialogGUIHorizontalLayout(0f, RowHeight + 4f, 0f, new RectOffset(), TextAnchor.LowerLeft,
                new DialogGUILabel("<b><color=#ffffff>" + title + "</color></b>", NameWidth + ControlWidth));
        }

        // A grey line under a technique row that shows only while there is something to say: a download's
        // progress or outcome.
        private static DialogGUIBase StatusLine(Func<string> text)
        {
            DialogGUILabel line = new DialogGUILabel(text, true);
            line.OptionEnabledCondition = () => !string.IsNullOrEmpty(text());
            return line;
        }

        private static string intelStatus;
        private static string intelStatusText = "";

        private static string IntelStatus()
        {
            string now = IntelDownloader.Status;
            if (now != intelStatus)
            {
                intelStatus = now;
                intelStatusText = now == null ? "" : "<color=#9a9a9a>" + now + "</color>";
            }
            return intelStatusText;
        }

        private static string JoinStatus(params string[] parts)
        {
            System.Text.StringBuilder joined = new System.Text.StringBuilder();
            foreach (string part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                if (joined.Length > 0) joined.Append('\n');
                joined.Append(part);
            }
            return joined.ToString();
        }

        // The download's progress or outcome, grey, made anew only when it changes:
        // the label asks every frame.
        private static string nvidiaStatus;
        private static string nvidiaStatusText = "";

        private static string NvidiaStatus()
        {
            string now = NvidiaDownloader.Status;
            if (now != nvidiaStatus)
            {
                nvidiaStatus = now;
                nvidiaStatusText = now == null ? "" : "<color=#9a9a9a>" + now + "</color>";
            }
            return nvidiaStatusText;
        }

        // The mods with rows here that have a settings window of their own, for
        // what this window leaves out -- TUFX's profile editor, Scatterer's
        // planet settings. Theirs opens through their own toolbar button beside
        // this one: a change made there shows in these rows (SyncFromMods), one
        // made here shows there once applied.
        private static DialogGUIBase OwnWindowRow(SettingCategory category)
        {
            List<IBundledMod> mods = new List<IBundledMod>();
            foreach (IBundledMod mod in WindowLayout.AdvancedIn(category))
            {
                if (mod.OwnWindow != null && ToolbarTakeover.HasButton(mod)) mods.Add(mod);
            }
            if (mods.Count == 0) return null;

            List<DialogGUIBase> items = new List<DialogGUIBase> { new DialogGUILabel("Advanced", NameWidth) };
            foreach (IBundledMod mod in mods)
            {
                IBundledMod shown = mod;
                DialogGUIButton open = new DialogGUIButton(shown.ModName + "...", () => OpenOwnWindow(shown), 110f, 24f,
                    false);
                open.tooltipText = "Opens " + shown.ModName + "'s own settings window beside this one, with everything"
                                   + " it offers; the X at its top right closes it.\n"
                                   + "A change made there shows here; one made here shows there once applied.";
                items.Add(open);
            }
            return new DialogGUIHorizontalLayout(0f, 24f, 4f, new RectOffset(), TextAnchor.MiddleLeft, items.ToArray());
        }

        private static void OpenOwnWindow(IBundledMod mod)
        {
            try
            {
                if (!ToolbarTakeover.OpenOwnWindow(mod))
                    ScreenMessages.PostScreenMessage(mod.ModName + " has no toolbar button in this scene to open its"
                                                     + " window with.", 5f);
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("own-window-" + mod.Id, mod.ModName + "'s own window could not be opened ("
                                                             + CompatibilityLog.Reason(e) + ").");
            }
        }

        // A change made meanwhile outside this window -- in a mod's own window
        // opened through Advanced, in KSP's settings -- shows in the rows, as the
        // model takes it (SettingsEdit.Sync). Asked a few times a second while
        // the window is open (ModWindowsAddon).
        internal static void SyncFromMods()
        {
            if (!Visible) return;
            SyncOwn();
            bool changed = false;
            try
            {
                foreach (IBundledMod mod in BundledSettings.Installed())
                {
                    // The rows, and every setting of a mod where the rest of them
                    // can change: while its own window is open, however it was
                    // opened, and KSP's once its own settings screen has applied.
                    // All of some 180 settings read by reflection four times a second
                    // would cost frame time.
                    bool whole = ModWindowClose.RecentlyOpen(mod) || dueMods.Remove(mod.Id);
                    foreach (BundledSetting setting in mod.Settings)
                    {
                        if (!whole && !shownKeys.Contains(setting.Key)) continue;
                        changed |= model.Sync(setting.Key, BundledSettings.Current(setting));
                    }
                }
            }
            finally
            {
                // Counted even when a read failed halfway: what was taken stays
                // taken.
                if (changed) model.ProfilesStale = true;
            }
            if (changed || !model.ProfilesStale) return;
            LoadProfiles();
        }

        // ReDefinition's own settings changed meanwhile outside this window -- in
        // the diagnostics window, by a hotkey: each row the player has not changed
        // here takes the value now; one changed here keeps the change, which
        // Accept sets. Not while a reset waits, which has set every one of them.
        private static string ownLiveSnapshot;

        private static void SyncOwn()
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon == null || edit == null || edit.Before == null || edit.After == null || model.Resetting) return;
            OwnSettings live = addon.Current();
            string snapshot = live.Snapshot();
            if (snapshot == ownLiveSnapshot) return;
            ownLiveSnapshot = snapshot;
            if (snapshot == edit.Before.Snapshot()) return;

            OwnSettings after = live.Clone();
            foreach (IGraphicsModule module in OurModules.All)
            {
                foreach (ModuleSetting setting in module.Settings)
                {
                    string changed = setting.Read(edit.After);
                    if (changed != setting.Read(edit.Before)) setting.Write(after, changed);
                }
            }
            if (edit.Before.ReplaceKspSettings != edit.After.ReplaceKspSettings)
                after.ReplaceKspSettings = edit.After.ReplaceKspSettings;
            if (edit.Before.TimeWarpToolbarButton != edit.After.TimeWarpToolbarButton)
                after.TimeWarpToolbarButton = edit.After.TimeWarpToolbarButton;
            edit.Before = live;
            edit.After = after;
            status = null;
        }

        // Every setting of that mod at the next sync: KSP's, as its own settings
        // screen applies -- no window this sees open.
        internal static void FollowModSoon(string id)
        {
            if (Visible) dueMods.Add(id);
        }

        // Under its own label where the setting's list has one: a row a
        // requirement limits leaves some of them out. Among numbers that only rise
        // or only fall it takes its place -- a frame limit of 144 between 140 and
        // 180 -- and in front of anything else.
        private static void OfferChoice(ChoiceRow row, string value)
        {
            if (Array.FindIndex(row.Choices, c => SettingValues.Same(c, value)) >= 0) return;
            int at = PlaceAmong(row.Choices, value);
            row.Choices = InsertAt(row.Choices, at, value);
            if (row.Labels != null)
            {
                int index = row.AllChoices != null ? Array.FindIndex(row.AllChoices, c => SettingValues.Same(c, value)) : -1;
                row.Labels = InsertAt(row.Labels, Math.Min(at, row.Labels.Length),
                    index >= 0 && row.AllLabels != null && index < row.AllLabels.Length ? row.AllLabels[index] : value);
            }
            // Not yet there while the row is built.
            if (row.Control == null) return;
            row.Control.max = row.Choices.Length - 1;
            if (row.Control.slider != null) row.Control.slider.maxValue = row.Control.max;
        }

        private static void ShowDiagnostics()
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon != null) addon.ShowDiagnostics();
        }
    }
}
