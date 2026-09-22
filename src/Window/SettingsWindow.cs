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
    // One window for the graphics settings -- ReDefinition's and those of the
    // graphics mods bundled here -- sorted by feature, as KSP's own settings screen
    // sorts, with the mod a setting comes from named beside it.
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

        private static PopupDialog dialog;
        private static SettingCategory current = SettingCategory.Profiles;
        private static KspSettingsSection.Edit edit;

        // The bundled settings' rows: their values, where each came from, and
        // Apply's steps.
        private static readonly SettingsEdit model = new SettingsEdit { Offered = OfferChoice };

        // The graphics profiles, Low to Max, read once per opening.
        private static List<GraphicsProfile> profiles = new List<GraphicsProfile>();

        // A choice row's list, which a value from outside the list joins at its
        // front: one a mod's own window set while this one is open, or one a
        // profile hands back.
        private sealed class ChoiceRow
        {
            public string[] Choices;
            public string[] Labels;
            // The setting's own list, before a requirement limited it: where a
            // value joining the front finds its label.
            public string[] AllChoices;
            public string[] AllLabels;
            public DialogGUISlider Control;
        }

        private static readonly Dictionary<string, ChoiceRow> choiceRows = new Dictionary<string, ChoiceRow>();

        // The settings the window has a row for: what SyncFromMods follows, with
        // every setting of a mod whose own window is open.
        private static readonly HashSet<string> shownKeys = new HashSet<string>();

        // Mods whose every setting the next sync reads: KSP's, once its own
        // settings screen has applied (FollowModSoon).
        private static readonly HashSet<string> dueMods = new HashSet<string>();

        // The Profiles tab's status line, worked out again only when something
        // it depends on has changed: DialogGUILabel asks for it every frame.
        private static string status;
        private static int statusVersion;
        private static string statusProfile;
        private static string statusApplied;
        private static bool statusBundled;
        private static bool statusEnabled;
        private static bool statusUpscalerPending;
        // The values of ReDefinition's modules' quality settings the status line was worked
        // out with: a profile's differences count each of them, and no other module
        // setting.
        private static string[] statusModules;

        public static bool Visible { get { return dialog != null; } }

        // Whether a text field of this window -- the Keys tab's search -- has the
        // keyboard (KeyCapture keeps the game's keys from it meanwhile).
        internal static bool TextFieldFocused()
        {
            if (dialog == null) return false;
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

            try
            {
                OwnSettings now = addon.Current();
                // ReDefinition's own rows follow the profile the other rows were filled from,
                // applied or not -- with the bundling, without which no profile is
                // stored.
                edit = new KspSettingsSection.Edit
                {
                    Before = now,
                    After = now.Clone(),
                    ProfileChosen = () => model.Bundled && !string.IsNullOrEmpty(model.Profile),
                };
                ReadBundled();
                LoadProfiles();

                UISkinDef skin = UISkinManager.GetSkin("MiniSettingsSkin") ?? HighLogic.UISkin;
                DialogGUIBase[] content = Build(skin);
                TooltipText.WrapAll(content);
                MultiOptionDialog window = new MultiOptionDialog("ReDefinitionSettings", "", "Settings -- ReDefinition",
                    skin, new Rect(0.5f, 0.5f, WindowWidth, WindowHeight), content);
                dialog = PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), window,
                    false, skin, false);
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
                WindowPause.PlaceInTitleRow(dialog);
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

        // The mods whose toolbar button ReDefinition can hide, with what it does
        // with it now. A mod without a button of its own, or without a window to
        // open in its place, has nothing to choose here.
        private static IEnumerable<KeyValuePair<string, bool>> ButtonStates()
        {
            foreach (IBundledMod mod in BundledSettings.Installed())
                if (ToolbarTakeover.CanHide(mod))
                    yield return new KeyValuePair<string, bool>(mod.Id, BundledSettings.HidesButton(mod.Id));
        }

        private static List<IBundledMod> ButtonMods()
        {
            List<IBundledMod> mods = new List<IBundledMod>();
            foreach (IBundledMod mod in BundledSettings.Installed())
                if (ToolbarTakeover.CanHide(mod)) mods.Add(mod);
            return mods;
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
            LoadRowDefaults();
            List<DialogGUIBase> tabs = new List<DialogGUIBase> { SearchRow() };
            // Sized by TabScrollList itself: KSP's DialogGUIContentSizer lets go
            // of the height once a page fits.
            List<DialogGUIBase> pages = new List<DialogGUIBase>();

            TabScrollList scroll = null;
            bool currentShown = false;
            if (InDetail(current)) current = SettingCategory.Detail;
            foreach (SettingCategory category in Enum.GetValues(typeof(SettingCategory)))
            {
                if (InDetail(category)) continue;
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
                    WaitingNotice(WindowWidth - 20f - ResetWidth - 3f * 80f - 24f),
                    new DialogGUIFlexibleSpace(),
                    new DialogGUIButton(Localizer.Format("#autoLOC_149512"), Apply, 80f, 30f, false),
                    new DialogGUIButton(Localizer.Format("#autoLOC_149513"), Apply, 80f, 30f, true),
                    new DialogGUIButton(Localizer.Format("#autoLOC_149514"), () => { }, 80f, 30f, true)),
            };
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
            if (category == SettingCategory.General)
            {
                DialogGUIBase[] own = KspSettingsSection.Rows(edit, new KspSettingsSection.Layout
                {
                    Name = NameWidth,
                    Control = ControlWidth,
                    ValueGap = 10f,
                    Value = ValueWidth,
                    Source = "ReDefinition",
                    SourceWidth = SourceWidth,
                });
                // One row per module setting, in the same order (KspSettingsSection.Rows).
                List<ModuleSetting> titles = new List<ModuleSetting>(OurModules.Rows());
                for (int i = 0; i < own.Length; i++)
                    rows.Add(Searchable(own[i], (i < titles.Count ? titles[i].Title : "Upscaler") + " ReDefinition"));
                DialogGUIBase nvidia = NvidiaRow();
                if (nvidia != null) rows.Add(Searchable(nvidia, "NVIDIA DLSS files download"));
            }
            if (category == SettingCategory.Interface)
            {
                rows.Add(Searchable(ReplaceRow(), "Replace original settings KSP menu"));
                foreach (DialogGUIBase row in InterfaceRows())
                    rows.Add(Searchable(row, "Mods toolbar bundle hide restore before ReDefinition"));
            }
            if (category == SettingCategory.Keys)
            {
                foreach (DialogGUIBase row in LayoutRows()) rows.Add(Searchable(row, "Keyboard layout"));
                rows.AddRange(KeyRows());
            }
            if (category == SettingCategory.Axes) rows.AddRange(AxisRows());

            // The Keys tab builds its sections itself (KeyRows). A heading goes
            // before the first row of each group a registration names (`section`);
            // in the long tabs the groups open and fold instead (AddFold).
            string section = null;
            bool folding = Folding(category);
            List<DialogGUIBase> fold = new List<DialogGUIBase>();
            bool firstFold = true;
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
                        AddFold(rows, category, section, fold, firstFold);
                        firstFold = false;
                        fold = new List<DialogGUIBase>();
                    }
                    section = setting.Section ?? section ?? Title(category);
                    fold.Add(row);
                    continue;
                }
                if (newSection) rows.Add(SectionHeading(setting.Section));
                section = setting.Section;
                rows.Add(row);
            }
            if (folding && fold.Count > 0) AddFold(rows, category, section, fold, firstFold);
            if (rows.Count > 0 && category != SettingCategory.Profiles && category != SettingCategory.Interface
                && category != SettingCategory.Keys)
            {
                DialogGUIBase advanced = OwnWindowRow(category);
                if (advanced != null) rows.Add(advanced);
            }
            return rows.ToArray();
        }

        // Whether KSP's own Settings buttons -- in the main menu and the pause
        // menus -- open this window instead of KSP's screens. One of
        // ReDefinition's own settings, applied with the others.
        private static DialogGUIBase ReplaceRow()
        {
            DialogGUIToggle toggle = new DialogGUIToggle(() => edit != null && edit.After.ReplaceKspSettings,
                () => KspSettingsSection.StateText(edit != null && edit.After.ReplaceKspSettings),
                b =>
                {
                    if (edit != null) edit.After.ReplaceKspSettings = b;
                }, ControlWidth);
            toggle.tooltipText = "On: every Settings button of KSP -- the main menu's and the pause menus' -- opens this"
                                 + " window, which holds all of KSP's settings, and ReDefinition adds no entry of its own"
                                 + " there.\nOff: KSP's buttons open KSP's own screens, and ReDefinition's entry stands"
                                 + " under them.\nTakes effect the next time a menu is built.";
            return new DialogGUIHorizontalLayout(0f, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel("Replace original settings", NameWidth), toggle, new DialogGUISpace(10f),
                new DialogGUILabel("", ValueWidth),
                new DialogGUILabel("<color=#9a9a9a>ReDefinition</color>", SourceWidth));
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

        // NVIDIA's DLLs for DLSS and DLSS frame generation, which the player fetches
        // from NVIDIA's release (NvidiaDownloader): offered where this GPU can use
        // them and they are missing, and followed by what the download came to.
        private static DialogGUIBase NvidiaRow()
        {
            List<NvidiaFiles.Entry> needed = NvidiaDownloader.Needed();
            if (needed.Count == 0 && NvidiaDownloader.Status == null) return null;

            string offer = needed.Count == 0
                ? ""
                : "<color=#9a9a9a>" + NvidiaDownloader.Megabytes(NvidiaFiles.DownloadSize(needed)) + " MB from NVIDIA</color>";

            bool dlss;
            bool frameGeneration;
            NvidiaDownloader.Uses(needed, out dlss, out frameGeneration);
            DialogGUIButton download = new DialogGUIButton("Download ...", () => NvidiaDownloader.Confirm(needed),
                ControlWidth, 24f, false);
            // Nothing needed says nothing about why: the files in place, or no answer
            // from NVAPI yet.
            download.tooltipText = needed.Count == 0
                ? "Nothing to download from NVIDIA for this GPU now."
                : "NVIDIA's DLLs for " + NvidiaDownloader.Purpose(dlss, frameGeneration)
                  + ".\n"
                  + "This fetches them from NVIDIA's release of the Streamline SDK on GitHub, once NVIDIA's\n"
                  + "licences are accepted, and places them where ReDefinition's dxgi.dll loads them from:\n"
                  + "next to KSP_x64.exe, unless its ini names other folders.\n"
                  + NvidiaDownloader.WhenUsed(dlss, frameGeneration);
            download.OptionInteractableCondition = () => needed.Count > 0 && !NvidiaDownloader.Running
                                                         && !NvidiaDownloader.Installed;

            return new DialogGUIVerticalLayout(0f, -1f, 2f, new RectOffset(), TextAnchor.UpperLeft,
                new DialogGUIHorizontalLayout(0f, 24f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                    new DialogGUILabel("NVIDIA DLSS files", NameWidth), download, new DialogGUILabel(offer, true)),
                new DialogGUILabel(NvidiaStatus, true));
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
        // the window is open (BundledSettingsAddon).
        internal static void SyncFromMods()
        {
            if (!Visible) return;
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

        // Every setting of that mod at the next sync: KSP's, as its own settings
        // screen applies -- no window this sees open.
        internal static void FollowModSoon(string id)
        {
            if (Visible) dueMods.Add(id);
        }

        // A value a choice row does not list goes in front of its list, as one
        // found as the window opened does.
        private static void OfferChoice(string key, string value)
        {
            ChoiceRow row;
            if (value == null || !choiceRows.TryGetValue(key, out row)) return;
            OfferChoice(row, value);
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

        // Where a value joins the choices: in order among numbers that only rise or
        // only fall, else at the front.
        private static int PlaceAmong(string[] choices, string value)
        {
            double number;
            if (choices.Length < 2 || !TryNumber(value, out number)) return 0;
            double[] numbers = new double[choices.Length];
            for (int i = 0; i < choices.Length; i++)
                if (!TryNumber(choices[i], out numbers[i])) return 0;
            bool rising = numbers[1] > numbers[0];
            for (int i = 1; i < numbers.Length; i++)
                if (numbers[i] == numbers[i - 1] || (numbers[i] > numbers[i - 1]) != rising) return 0;
            int at = 0;
            while (at < numbers.Length && (rising ? numbers[at] < number : numbers[at] > number)) at++;
            return at;
        }

        private static bool TryNumber(string text, out double number)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        private static string[] InsertAt(string[] list, int at, string value)
        {
            string[] all = new string[list.Length + 1];
            Array.Copy(list, 0, all, 0, at);
            all[at] = value;
            Array.Copy(list, at, all, at + 1, list.Length - at);
            return all;
        }

        // Whether anything in the window still waits for Apply.
        private static bool Unapplied()
        {
            // KSP's key bindings and axes wait in their tabs until Apply as well.
            return kspPending.Count > 0 || axisPending.Count > 0 || layoutPending != null
                   || model.Unapplied(UpscalerPending(), BundledSettings.Enabled, BundledSettings.ProfileName);
        }

        private static bool UpscalerPending()
        {
            return edit != null && edit.Before != null && edit.After != null && !SameUpscaler(edit.Before, edit.After);
        }

        // The profiles as the GameDatabase holds them -- a visual pack's patches
        // included -- with what choosing each fills in for the mods installed
        // now. What one names that cannot be set here reaches the log once per
        // run (ProfileApplier.Report).
        private static void LoadProfiles()
        {
            model.ProfilesStale = false;
            List<string> problems = new List<string>();
            profiles = ProfileApplier.Ordered(problems);

            Dictionary<string, Dictionary<BundledSetting, string>> own =
                new Dictionary<string, Dictionary<BundledSetting, string>>();
            foreach (GraphicsProfile profile in profiles) own[profile.Name] = ProfileApplier.Values(profile, problems);

            // Against the profile applied now, and the values the mods hold now:
            // read again after every Apply and restore.
            Dictionary<string, Dictionary<BundledSetting, string>> values =
                new Dictionary<string, Dictionary<BundledSetting, string>>();
            Dictionary<string, HashSet<BundledSetting>> releasedBy = new Dictionary<string, HashSet<BundledSetting>>();
            Dictionary<BundledSetting, string> applied;
            own.TryGetValue(BundledSettings.ProfileName, out applied);
            foreach (GraphicsProfile profile in profiles)
            {
                HashSet<BundledSetting> released = new HashSet<BundledSetting>();
                values[profile.Name] = ProfileApplier.WithReleased(own[profile.Name], applied, released);
                releasedBy[profile.Name] = released;
            }
            model.SetProfiles(values, releasedBy);
            ProfileApplier.Report("Graphics profiles", problems);
            status = null;
        }

        // Low to Max. Choosing one fills the rows of every tab with its values
        // and switches the bundling on, which a profile needs; nothing is set
        // until Apply or Accept, as with any other row.
        private static DialogGUIBase[] ProfileRows()
        {
            if (profiles.Count == 0) return new DialogGUIBase[0];

            List<DialogGUIBase> rows = new List<DialogGUIBase>
            {
                new DialogGUILabel(ProfileStatus, PageWidth - 60f),
                new DialogGUILabel("<color=#9a9a9a>Choosing a profile fills in the rows of every tab; Apply or Accept"
                                   + " sets them. A row changed afterwards makes it Custom. Calibrated for 1440p at"
                                   + " native resolution: at 4K a tier lower fits.</color>", true),
            };
            foreach (GraphicsProfile profile in profiles)
            {
                GraphicsProfile shown = profile;
                DialogGUIToggleButton choose = new DialogGUIToggleButton(() => model.Profile == shown.Name, shown.Title,
                    on => { if (on) ChooseProfile(shown); }, 110f, 30f);
                choose.tooltipText = shown.Description;
                rows.Add(new DialogGUIHorizontalLayout(0f, 30f, 8f, new RectOffset(), TextAnchor.MiddleLeft,
                    choose, new DialogGUILabel("<color=#9a9a9a>" + shown.Hardware + "</color>", true)));
                rows.Add(new DialogGUILabel(shown.Description, true));
            }

            return rows.ToArray();
        }

        // Everything back to its mod's default -- whether a profile or the player
        // changed it, and whether the window shows it -- once the question it asks
        // has been answered.
        private static DialogGUIButton ResetButton()
        {
            DialogGUIButton reset = new DialogGUIButton("Reset", ConfirmReset, ResetWidth, 30f, false);
            reset.tooltipText = "Every setting back to its default: KSP's as KSP's own Reset sets them, and the mods'.\n"
                                + "ReDefinition is off until a profile is chosen. Asks first, and says what it resets.";
            return reset;
        }

        private static void ConfirmReset()
        {
            List<string> names = new List<string>();
            List<string> atEveryStart = new List<string>();
            foreach (IBundledMod mod in BundledSettings.Installed())
            {
                // KSP's own reset is KSP's (KspReset), said apart below.
                if (mod.Id == "ksp") continue;
                names.Add(mod.ModName);
                if (mod.Saving == SettingsSaving.AtEveryStart) atEveryStart.Add(mod.ModName);
            }

            string message = "All of KSP's settings go back to what KSP ships, as KSP's own Reset sets them: graphics,"
                             + " gameplay, system, audio, input, and the key bindings of the keyboard layout KSP detects."
                             + " The screen resolution and full screen stay as they are.\n\n";
            if (names.Count > 0)
            {
                message += "Every setting of " + string.Join(", ", names.ToArray()) + " goes back to its default, the"
                           + " ones this window does not show included: what the mods' releases ship, and with"
                           + " Volumetric Clouds installed what its author ships. What an installed mod requires still"
                           + " counts."
                           + (atEveryStart.Count > 0
                               ? " " + string.Join(" and ", atEveryStart.ToArray()) + ", which ReDefinition sets at every"
                                 + " start, take what their own files hold from the next start on."
                               : "")
                           + (model.Bundled ? " Their settings stay in this window." : " Their settings are bundled in this window.")
                           + "\n\n";
            }
            message += "ReDefinition's own settings go back to theirs, with the upscaler and frame generation off. No"
                       + " graphics profile is chosen afterwards: the upscaler and frame generation stay off, and the"
                       + " other mods keep their own antialiasing, until one is.\n\n"
                       + "The rows are only filled in: Apply or Accept sets them, Cancel leaves everything as it was."
                       + " \"Restore settings from before ReDefinition\" under Mods and toolbar brings back what the mods"
                       + " had before ReDefinition first changed them.\n\nThe key bindings go back to their defaults"
                       + " too: ReDefinition's own and the mods'.";

            MultiOptionDialog confirm = new MultiOptionDialog("ReDefinitionReset", message,
                "Reset", HighLogic.UISkin, 460f,
                new DialogGUIButton("Reset", ChooseDefaults, true),
                new DialogGUIButton(Localizer.Format("#autoLOC_149514"), () => { }, true));
            UnityMouseEvents.Shield(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                confirm, false, HighLogic.UISkin));
        }

        // Fills every setting with its mod's default (SettingsEdit.ChooseDefaults),
        // and ReDefinition's own settings with theirs: the upscaler and frame
        // generation off, since no profile is chosen afterwards. With no defaults
        // for the other mods -- none installed, or the game still loading -- the
        // profile goes all the same.
        private static void ChooseDefaults()
        {
            List<string> problems = new List<string>();
            Dictionary<BundledSetting, string> values = ProfileApplier.DefaultValues(problems);
            ProfileApplier.Report("Reset", problems);
            if (values.Count > 0)
            {
                model.ChooseDefaults(values);
            }
            else
            {
                if (BundledSettings.Installed().Count > 0)
                    ScreenMessages.PostScreenMessage("ReDefinition has no defaults for the other mods yet; the log says why.",
                        5f);
                model.Profile = "";
            }
            if (edit != null) edit.After = new OwnSettings();
            // ReDefinition's own bindings went back with the settings above, the
            // mods' go with their other settings, and KSP's are filled in here.
            ResetKeyBindings();
            ResetAxes();
            ClearLayoutPending();
            status = null;
        }

        private static void ChooseProfile(GraphicsProfile profile)
        {
            // Built from values that have changed since, the lists would hand back
            // what the player changed meanwhile.
            if (model.ProfilesStale)
            {
                try
                {
                    LoadProfiles();
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Log.Tag + " The graphics profiles could not be read again: " + e);
                }
            }
            if (!model.ChooseProfile(profile.Name)) return;

            List<string> problems = new List<string>();
            if (edit != null) ModuleProfiles.Apply(profile, edit.After, problems);
            ProfileApplier.Report("Graphics profile '" + profile.Title + "'", problems);
        }

        // What the rows hold now against the profile they were filled from, and
        // whether any of it still waits for Apply (SettingsEdit.Status).
        private static string ProfileStatus()
        {
            string applied = BundledSettings.ProfileName;
            bool enabled = BundledSettings.Enabled;
            OwnSettings after = edit != null ? edit.After : null;
            bool upscalerPending = UpscalerPending();
            bool modulesSame = SameModules(after);

            if (status != null && statusVersion == model.Version && statusProfile == model.Profile
                && statusApplied == applied && statusBundled == model.Bundled && statusEnabled == enabled
                && statusUpscalerPending == upscalerPending && modulesSame)
                return status;

            statusVersion = model.Version;
            statusProfile = model.Profile;
            statusApplied = applied;
            statusBundled = model.Bundled;
            statusEnabled = enabled;
            statusUpscalerPending = upscalerPending;
            GraphicsProfile profile = ProfileApplier.Find(profiles, model.Profile);
            status = model.Status(applied, enabled, upscalerPending, profile != null ? profile.Title : null,
                values => ProfileApplier.Differences(values, PendingValue) + ModuleProfiles.Differences(profile, after));
            return status;
        }

        // Whether every quality setting of ReDefinition's modules still holds the value the
        // status line was worked out with; the values now are kept for the next
        // frame.
        private static bool SameModules(OwnSettings after)
        {
            int count = 0;
            for (int m = 0; m < OurModules.All.Count; m++)
            {
                IList<ModuleSetting> settings = OurModules.All[m].Settings;
                for (int s = 0; s < settings.Count; s++)
                    if (settings[s].Kind == SettingKind.Quality) count++;
            }
            bool same = statusModules != null && statusModules.Length == count;
            if (!same) statusModules = new string[count];

            int i = 0;
            for (int m = 0; m < OurModules.All.Count; m++)
            {
                IList<ModuleSetting> settings = OurModules.All[m].Settings;
                for (int s = 0; s < settings.Count; s++)
                {
                    if (settings[s].Kind != SettingKind.Quality) continue;
                    string value = after != null ? settings[s].Read(after) : null;
                    if (!string.Equals(statusModules[i], value, StringComparison.Ordinal))
                    {
                        statusModules[i] = value;
                        same = false;
                    }
                    i++;
                }
            }
            return same;
        }

        // Field by field, since the status line asks every frame.
        private static bool SameUpscaler(OwnSettings a, OwnSettings b)
        {
            return a.Enabled == b.Enabled && a.Quality == b.Quality && a.Sharpness == b.Sharpness
                   && a.MipmapBias == b.MipmapBias && a.CompensateLodBias == b.CompensateLodBias
                   && a.DisableMsaa == b.DisableMsaa && a.ForceAnisotropic == b.ForceAnisotropic && a.Jitter == b.Jitter
                   && a.SkinnedMotionVectors == b.SkinnedMotionVectors
                   && a.TufxAfterUpscaling == b.TufxAfterUpscaling && a.TransparencyMask == b.TransparencyMask
                   && a.ReactiveMask == b.ReactiveMask && a.AutoExposure == b.AutoExposure
                   && a.FrameGeneration == b.FrameGeneration && a.Backend == b.Backend && a.DlssPreset == b.DlssPreset
                   && a.UpscalerKey == b.UpscalerKey && a.SettingsWindowKey == b.SettingsWindowKey
                   && a.DiagnosticsKey == b.DiagnosticsKey && a.CameraListKey == b.CameraListKey;
        }

        private static string PendingValue(BundledSetting setting)
        {
            string value;
            return model.TryGetPending(setting.Key, out value) ? value : null;
        }

        private static DialogGUIBase[] InterfaceRows()
        {
            List<DialogGUIBase> notShown = NotShownRows();
            List<IBundledMod> installed = BundledSettings.Installed();
            if (installed.Count == 0) return notShown.ToArray();

            List<string> names = new List<string>();
            foreach (IBundledMod mod in installed) names.Add(mod.ModName);
            string list = string.Join(", ", names.ToArray());

            DialogGUIToggle bundle = new DialogGUIToggle(() => model.Bundled,
                () => KspSettingsSection.StateText(model.Bundled), b => model.Bundled = b, ControlWidth);
            bundle.tooltipText = "On: the settings of " + list + " are changed here, sorted by feature, and saved in\n"
                                 + "those mods as their own windows save them; their own toolbar buttons are hidden\n"
                                 + "where the Advanced buttons here open their windows.\n"
                                 + "Off: each keeps its own button and window, and ReDefinition leaves their settings to them.\n"
                                 + "What was saved in the mods stays. What only ReDefinition kept is dropped: the choices\n"
                                 + "for every save of TUFX and Distant Object, and Deferred's and Waterfall's settings.\n"
                                 + "Off also clears the graphics profile: the upscaler and frame generation go off until one is chosen.";

            DialogGUIButton restore = new DialogGUIButton("Restore settings from before ReDefinition", ConfirmRestore,
                300f, 30f, false);
            restore.tooltipText = "Every setting ReDefinition has changed goes back to what its mod had before, and is"
                                  + " saved there.\nApply or cancel the changes made here first.";
            restore.OptionInteractableCondition = () => BundledSettings.CanRestore && !Unapplied();

            List<DialogGUIBase> rows = new List<DialogGUIBase>
            {
                new DialogGUIHorizontalLayout(0f, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                    new DialogGUILabel("Bundle other mods here", NameWidth), bundle),
                new DialogGUILabel("<color=#9a9a9a>Bundled: " + list + "</color>", true),
            };
            rows.AddRange(ButtonRows());
            rows.Add(restore);
            rows.AddRange(notShown);
            return rows.ToArray();
        }

        // Whether the per-mod switches are folded out. Kept for the run, as the
        // Keys tab keeps its sections.
        private static bool buttonsOpen;

        // The toolbar block: one switch for every button ReDefinition can hide, and
        // a header that folds the mods out one by one, as the Keys tab's sections
        // do. Empty where no installed mod has such a button. The choices wait for
        // Apply, as everything else in this window does.
        private static List<DialogGUIBase> ButtonRows()
        {
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            List<IBundledMod> mods = ButtonMods();
            if (mods.Count == 0) return rows;

            DialogGUIToggle all = new DialogGUIToggle(() => AllButtonsHidden(mods),
                () => KspSettingsSection.StateText(AllButtonsHidden(mods)),
                hide => { foreach (IBundledMod mod in mods) model.SetButtonHidden(mod.Id, hide); }, ControlWidth);
            all.tooltipText = "On: every mod below is hidden from the toolbar while its settings are bundled here.\n"
                              + "Off: every one of them keeps its button.\n"
                              + "It shows On while all of them are hidden.";
            all.OptionInteractableCondition = () => model.Bundled;
            rows.Add(new DialogGUIHorizontalLayout(0f, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel("Hide all from toolbar", NameWidth), all));

            string folded = "+  Per mod (" + mods.Count + ")";
            string open = "-  Per mod (" + mods.Count + ")";
            DialogGUIButton header = new DialogGUIButton(() => buttonsOpen ? open : folded,
                () => { buttonsOpen = !buttonsOpen; }, PageWidth - 60f, RowHeight + 6f, false);
            header.tooltipText = "Opens or folds the switch for each mod's own toolbar button.";
            rows.Add(header);

            foreach (IBundledMod mod in mods)
            {
                IBundledMod shown = mod;
                DialogGUIToggle toggle = new DialogGUIToggle(() => model.ButtonHidden(shown.Id),
                    () => KspSettingsSection.StateText(model.ButtonHidden(shown.Id)),
                    hide => model.SetButtonHidden(shown.Id, hide), ControlWidth);
                toggle.tooltipText = "On: " + mod.ModName + "'s toolbar button is hidden, and the Advanced button here"
                                     + " opens its window.\n"
                                     + "Off: it keeps its button, and its settings stay in this window.";
                toggle.OptionInteractableCondition = () => model.Bundled;
                DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(0f, RowHeight, 0f, new RectOffset(),
                    TextAnchor.MiddleLeft, new DialogGUILabel("    " + mod.ModName, NameWidth), toggle);
                row.OptionEnabledCondition = () => buttonsOpen;
                rows.Add(row);
            }
            return rows;
        }

        private static bool AllButtonsHidden(List<IBundledMod> mods)
        {
            foreach (IBundledMod mod in mods)
                if (!model.ButtonHidden(mod.Id)) return false;
            return mods.Count > 0;
        }

        // What an installed mod has that this window cannot show: a mod this build of
        // which is not bundled at all, rows its registration places that the build
        // lacks, and settings the build saves that no registration names. Nothing
        // where a mod's version has changed without any of that. Each stays in the
        // mod's own window, which a button here opens where ReDefinition can.
        private static List<DialogGUIBase> NotShownRows()
        {
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            foreach (IBundledMod mod in BundledSettings.Mods)
            {
                RegisteredMod registered = mod as RegisteredMod;
                if (registered == null || !registered.AssemblyLoaded) continue;

                string text = NotShownText(registered);
                if (text == null) continue;
                if (rows.Count == 0)
                    rows.Add(new DialogGUILabel("<color=#ffdd55>Not shown in this window</color>", true));

                if (registered.IsInstalled && registered.OwnWindow != null)
                {
                    IBundledMod shown = mod;
                    DialogGUIButton open = new DialogGUIButton(mod.ModName + "'s window", () => OpenOwnWindow(shown),
                        160f, 24f, false);
                    rows.Add(new DialogGUIHorizontalLayout(0f, 24f, 8f, new RectOffset(), TextAnchor.MiddleLeft,
                        new DialogGUILabel(text, PageWidth - 220f), open));
                }
                else
                {
                    rows.Add(new DialogGUILabel(text, true));
                }
            }
            return rows;
        }

        private static bool AnyNotShown()
        {
            foreach (IBundledMod mod in BundledSettings.Mods)
            {
                RegisteredMod registered = mod as RegisteredMod;
                if (registered != null && registered.AssemblyLoaded && NotShownText(registered) != null) return true;
            }
            return false;
        }

        private static string NotShownText(RegisteredMod mod)
        {
            if (!mod.IsInstalled)
            {
                return mod.ModName + ": this build is not bundled, so none of its settings are here -- its own window"
                       + " and toolbar button have them all.";
            }
            List<string> parts = new List<string>();
            if (mod.RowsNotShown.Count > 0)
                parts.Add("this build has no " + Joined(mod.RowsNotShown) + " as ReDefinition knows "
                          + (mod.RowsNotShown.Count == 1 ? "it" : "them"));
            if (mod.SettingsNotKnown.Count > 0)
                parts.Add("it saves " + Joined(mod.SettingsNotKnown) + ", which ReDefinition does not know");
            if (parts.Count == 0) return null;
            return mod.ModName + ": " + string.Join("; ", parts.ToArray()) + ". Its own window has "
                   + (mod.RowsNotShown.Count + mod.SettingsNotKnown.Count == 1 ? "it." : "them.");
        }

        private static string Joined(IList<string> names)
        {
            List<string> quoted = new List<string>();
            foreach (string name in names) quoted.Add("'" + name + "'");
            return string.Join(", ", quoted.ToArray());
        }

        private static void ConfirmRestore()
        {
            MultiOptionDialog confirm = new MultiOptionDialog("ReDefinitionRestore",
                "Every setting ReDefinition has changed goes back to what its mod had before, and is saved there. "
                + "The graphics profile is cleared, so the upscaler and frame generation go off until one is chosen. "
                + "TUFX and Distant Object get theirs back in every save as it loads.",
                "Restore settings from before ReDefinition", HighLogic.UISkin, 420f,
                new DialogGUIButton("Restore", Restore, true),
                new DialogGUIButton(Localizer.Format("#autoLOC_149514"), () => { }, true));
            UnityMouseEvents.Shield(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                confirm, false, HighLogic.UISkin));
        }

        private static void Restore()
        {
            // The window stays usable behind the question: a row changed
            // meanwhile would be lost with the rows read again.
            if (Visible && Unapplied())
            {
                ScreenMessages.PostScreenMessage("Apply or cancel the changes in ReDefinition's window first, then restore.",
                    5f);
                return;
            }
            try
            {
                BundledSettings.RestoreBackup();
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The settings from before ReDefinition were restored only in part: " + e);
            }
            // Without the profile the upscaler goes off now, before anything reads it
            // back.
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon != null) addon.EnforceProfile();
            // The rows show what the mods hold now.
            if (!Visible) return;
            ReadBundled();
            LoadProfiles();
            if (addon != null && edit != null)
            {
                OwnSettings now = addon.Current();
                edit.Before = now;
                edit.After = now.Clone();
            }
        }

        private static DialogGUIBase BundledRow(BundledSetting setting)
        {
            // A setting checked by its type only has no control to draw: no row,
            // whatever the layout lists -- which the check outside the game
            // refuses.
            if (setting.Control == SettingControl.Value) return null;
            if (setting.Control == SettingControl.Binding) return BundledBindingRow(setting, false);

            string key = setting.Key;
            DialogGUIBase control;
            Func<string> value = () => "";

            if (setting.Control == SettingControl.Toggle)
            {
                control = new DialogGUIToggle(() => PendingBool(key),
                    () => KspSettingsSection.StateText(PendingBool(key)),
                    b => model.Change(key, b ? "True" : "False"), ControlWidth);
            }
            else if (setting.Control == SettingControl.Slider)
            {
                control = new DialogGUISlider(() => PendingFloat(key, setting.Min), setting.Min, setting.Max,
                    setting.WholeNumbers, ControlWidth, -1f, f =>
                    {
                        // The slider is set from the row in every frame
                        // (DialogGUISlider.Update), and a value it clamps or
                        // rounds on the way is not the player moving it.
                        if (Mirrors(f, PendingFloat(key, setting.Min), setting)) return;
                        model.Change(key, Format(f, setting.WholeNumbers));
                    });
                value = () => Display(setting, null, null);
            }
            else
            {
                // Taken as the window opens: TUFX's profiles are known only from
                // the running mod. Nothing to choose from, no row.
                string[] choices = SafeChoices(setting);
                if (choices == null || choices.Length == 0) return null;
                string[] labels = setting.ChoiceLabels;
                ChoiceRow choice = new ChoiceRow { AllChoices = choices, AllLabels = labels };
                // What an installed mod requires limits what can be chosen -- but for a
                // row it locks, which cannot be moved anyway: its whole list keeps the
                // handle where the value stands, where a list of one would put it at
                // the left end (Slider.normalizedValue is 0 where the minimum is the
                // maximum).
                if (!Requirements.Locked(setting)) Requirements.Limit(setting, ref choices, ref labels);
                choice.Choices = choices;
                choice.Labels = labels;

                // A value the list does not hold -- a profile pack removed since,
                // or one a requirement is about to put right -- stays in front,
                // rather than being shown as another.
                string now;
                if (model.TryGetPending(key, out now)) OfferChoice(choice, now);
                choice.Control = new DialogGUISlider(() => ChoiceIndex(key, choice.Choices), 0f,
                    choice.Choices.Length - 1, true, ControlWidth, -1f,
                    f =>
                    {
                        // The slider is set from the row in every frame: only a
                        // move away from what the row shows is the player
                        // choosing. A value from outside the list joins it first
                        // (OfferChoice).
                        int index = Mathf.Clamp(Mathf.RoundToInt(f), 0, choice.Choices.Length - 1);
                        if (index == Mathf.RoundToInt(ChoiceIndex(key, choice.Choices))) return;
                        model.Change(key, choice.Choices[index]);
                    });
                choiceRows[key] = choice;
                control = choice.Control;
                value = () => Display(setting, choice.Choices, choice.Labels);
            }

            // When a change arrives, where it is not at once, is said below the
            // tabs (WaitingNotice); the row's owner stands at its right.
            control.tooltipText = setting.Tooltip + Requirements.Note(setting);
            // The switch as it stands in the window, not as it was applied: with
            // bundling unticked the rows are left to their mods at once. A value
            // nobody could read cannot be edited sensibly.
            control.OptionInteractableCondition = () => (model.Bundled || SetDirectly(setting, false))
                                                        && model.HasPending(key) && !Requirements.Locked(setting);

            return new DialogGUIHorizontalLayout(0f, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel(() => MarkedTitle(setting), NameWidth), control, new DialogGUISpace(10f),
                new DialogGUILabel(value, ValueWidth),
                new DialogGUILabel("<color=#9a9a9a>" + setting.Owner.ModName + "</color>", SourceWidth));
        }

        private static string[] SafeChoices(BundledSetting setting)
        {
            try
            {
                return setting.CurrentChoices();
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("bundled-choices-" + setting.Key, setting.Owner.ModName + ", " + setting.Title
                                      + ": its choices could not be read (" + CompatibilityLog.Reason(e) + ").");
                return null;
            }
        }

        private static bool PendingBool(string key)
        {
            string text;
            bool value;
            return model.TryGetPending(key, out text) && bool.TryParse(text, out value) && value;
        }

        private static float PendingFloat(string key, float fallback)
        {
            string text;
            float value;
            return model.TryGetPending(key, out text)
                   && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
        }

        // Whether the slider's value is only what it was set to from the row:
        // Unity's slider clamps, then rounds whole numbers (Slider.ClampValue).
        private static bool Mirrors(float value, float shown, BundledSetting setting)
        {
            float expected = Mathf.Clamp(shown, setting.Min, setting.Max);
            if (setting.WholeNumbers) expected = Mathf.Round(expected);
            return Mathf.Approximately(value, expected);
        }

        private static float ChoiceIndex(string key, string[] choices)
        {
            string text;
            if (!model.TryGetPending(key, out text)) return 0f;
            for (int i = 0; i < choices.Length; i++)
                if (SettingValues.Same(choices[i], text)) return i;
            return 0f;
        }

        private static string Display(BundledSetting setting, string[] choices, string[] labels)
        {
            string text;
            if (!model.TryGetPending(setting.Key, out text)) return "--";
            if (choices != null)
            {
                for (int i = 0; i < choices.Length; i++)
                {
                    if (!SettingValues.Same(choices[i], text)) continue;
                    return labels != null && i < labels.Length ? labels[i] : choices[i];
                }
                return text;
            }

            float value;
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return text;
            if (setting.Percent)
                return Mathf.RoundToInt(value * 100f).ToString(CultureInfo.InvariantCulture) + " %";
            return setting.WholeNumbers
                ? Mathf.RoundToInt(value).ToString(CultureInfo.InvariantCulture)
                : value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string Format(float value, bool wholeNumbers)
        {
            return wholeNumbers
                ? Mathf.RoundToInt(value).ToString(CultureInfo.InvariantCulture)
                : (Mathf.Round(value * 100f) / 100f).ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static void ShowDiagnostics()
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon != null) addon.ShowDiagnostics();
        }

        private static IEnumerable<BundledSetting> InstalledSettings()
        {
            foreach (IBundledMod mod in BundledSettings.Installed())
            {
                foreach (BundledSetting setting in mod.Settings) yield return setting;
            }
        }

        // The settings that go through the bundling at Apply: all but those set
        // directly.
        private static IEnumerable<BundledSetting> BundledNow()
        {
            foreach (BundledSetting setting in InstalledSettings())
                if (!SetDirectly(setting, true)) yield return setting;
        }

        // Whether a row is set straight into its mod, as the mod's own screen
        // would: one that is never bundled, and one of a mod whose registration
        // says `direct` while the bundling is off -- as the window holds it, or
        // as it was applied.
        private static bool SetDirectly(BundledSetting setting, bool applied)
        {
            if (!setting.Bundled) return true;
            RegisteredMod mod = setting.Owner as RegisteredMod;
            if (mod == null || !mod.Registration.Direct) return false;
            return applied ? !BundledSettings.Enabled : !model.Bundled;
        }

        // The rows set directly whose value the player changed, or the reset
        // filled in: written into their mods, then each mod saves once. KSP's own
        // reset goes first where the window resets (KspReset), so that what
        // KSP's reset covers and no row shows -- its key layout, its terrain
        // presets -- is reset as well.
        private static void ApplyDirect(bool resetting)
        {
            if (resetting) KspReset.Run();

            HashSet<IBundledMod> written = new HashSet<IBundledMod>();
            foreach (BundledSetting setting in InstalledSettings())
            {
                // Only what the window changed: a value KSP's own screen set since
                // the window read it stays.
                if (!SetDirectly(setting, true) || !model.Changed(setting.Key)) continue;
                string value;
                if (!model.TryGetPending(setting.Key, out value) || value == null) continue;
                string now = SafeRead(setting);
                if (now != null && SettingValues.Same(now, value)) continue;
                try
                {
                    setting.Write(value);
                    written.Add(setting.Owner);
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn("direct-apply-" + setting.Key, setting.Owner.ModName + ", " + setting.Title
                                          + ": could not be set (" + CompatibilityLog.Reason(e) + ").");
                }
            }
            foreach (IBundledMod mod in written)
            {
                try
                {
                    mod.Save();
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn("direct-save-" + mod.Id, mod.ModName + "'s settings could not be saved ("
                                          + CompatibilityLog.Reason(e) + ").");
                }
            }
        }

        private static string SafeRead(BundledSetting setting)
        {
            try
            {
                return setting.Read();
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Apply and Accept, as in KSP's dialog: the bundling switch (on hands the
        // stored values over, off takes them back), the model's steps and the
        // profile, saved once -- then ReDefinition's own through the add-on's
        // setters, which switch nothing on without the profile in place, and read
        // back afterwards, as the section in KSP's settings dialog does.
        private static void Apply()
        {
            NoteWaiting();
            try
            {
                ApplyLayout();
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " KSP's keyboard layout from the window could not be applied: " + e);
            }

            try
            {
                ApplyKeyBindings();
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " KSP's key bindings from the window applied only in part: " + e);
            }

            try
            {
                ApplyAxes();
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " KSP's axes from the window applied only in part: " + e);
            }

            try
            {
                bool buttonsChanged = model.ButtonsPending();
                foreach (KeyValuePair<string, bool> pair in model.Buttons)
                    BundledSettings.SetHidesButton(pair.Key, pair.Value);

                if (model.Bundled != BundledSettings.Enabled)
                {
                    BundledSettings.SetEnabled(model.Bundled);
                    ToolbarTakeover.Refresh();
                }
                else if (buttonsChanged)
                {
                    ToolbarTakeover.Refresh();
                }

                bool resettingAll = model.Resetting;
                try
                {
                    ApplyDirect(resettingAll);
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Log.Tag + " Settings set directly from the window applied only in part: " + e);
                }

                if (BundledSettings.Enabled)
                {
                    bool changed = false;
                    bool resetting = resettingAll;
                    foreach (SettingsEdit.Step step in model.Steps(BundledNow(), BundledSettings.Holds))
                    {
                        BundledSetting setting = step.Setting;
                        // One setting that cannot be handed over must not take the
                        // other mods' changes with it -- nor the saving of the ones
                        // already made, which the outer catch would skip as well.
                        try
                        {
                            if (step.Kind == SettingsEdit.StepKind.Reset) BundledSettings.ResetTo(setting, step.Value);
                            else if (step.Kind == SettingsEdit.StepKind.Release) BundledSettings.Release(setting);
                            else BundledSettings.Set(setting, step.Value, false);
                            changed = true;
                        }
                        catch (Exception e)
                        {
                            CompatibilityLog.Warn("bundled-apply-" + setting.Key, setting.Owner.ModName + ", "
                                                  + setting.Title + ": could not be applied from the window ("
                                                  + CompatibilityLog.Reason(e) + ").");
                        }
                    }

                    // The profile the rows came from, once they are set -- in the
                    // same write as they are.
                    if (model.Profile != BundledSettings.ProfileName)
                    {
                        BundledSettings.ProfileName = model.Profile;
                        changed = true;
                    }
                    if (changed) BundledSettings.SaveNow();
                    if (resetting)
                        Debug.Log(Log.Tag + " Bundled settings: every setting set to its mod's default"
                                  + " (Reset).");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Other mods' settings from the window applied only in part: " + e);
            }

            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon != null && edit != null)
            {
                try
                {
                    addon.Apply(edit.Before, edit.After);
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Log.Tag + " The upscaler's settings from the window applied only in part: " + e);
                }
                finally
                {
                    // A throw in here would replace the one just caught.
                    try
                    {
                        // Both or neither: a half-written pair would take the
                        // player's earlier changes for new ones at the next
                        // Apply.
                        OwnSettings now = addon.Current();
                        OwnSettings copy = now.Clone();
                        edit.Before = now;
                        edit.After = copy;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning(Log.Tag + " The upscaler's settings could not be read back into the"
                                         + " window: " + e);
                    }
                }
            }

            // The new starting point, read back -- a failure there stays a warning,
            // not an exception the button handler sees.
            try
            {
                ReadBundled();
                LoadProfiles();
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The other mods' values could not be read back into the"
                                 + " window: " + e);
            }
        }
    }
}
