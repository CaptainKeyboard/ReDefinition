using System.Collections.Generic;
using System;

namespace ReDefinition.Settings
{
    // The tabs of the settings window, by feature, in this order: the game, its
    // sound, the display with the upscaler, the graphics, the controls, and last ReDefinition's own dealings
    // with menus, mods and the toolbar.
    // Graphics has the profiles and Detail under it, and the controls have their axes and devices
    // under them.
    // Shadows and reflections, planets and effects are groups of
    // Detail, not tabs of their own. Which setting shows where, and in what
    // order, its registration says (`row`, `order`; WindowLayout).
    internal enum SettingCategory
    {
        // KSP's gameplay switches, the map view's lines, and its system section:
        // one tab, as KSP's own General tab holds both.
        Gameplay,
        // KSP's volumes and its sound normalizer.
        Audio,
        // Titled Graphics: the profiles, and Display and Detail under it.
        Profiles,
        // The screen: resolution, full screen, V-Sync, frame limit, then
        // ReDefinition's upscaler, frame generation, low latency and raytracing, the UI's size
        // and the brightness boosts. The first tab under Graphics.
        Display,
        // Shown in Display, as its upscaling section, not as a tab: named
        // General in the registration format, which is released.
        General,
        // KSP's render quality, textures, lights and antialiasing, then the
        // three below as groups.
        Detail,
        ShadowsAndReflections,
        // Clouds, sea and ground.
        Planets,
        // Re-entry, engines, flares and distant vessels.
        Effects,
        // Titled Controls: every key binding -- ReDefinition's, the mods' and
        // KSP's -- with KSP's keyboard layout above them, and the axes and
        // devices under it.
        Keys,
        // KSP's joystick and gamepad axes.
        Axes,
        // Mouse wheel, Track IR and 6-DOF.
        Devices,
        Interface,
    }

    // The control the window draws a setting's row with.
    internal enum SettingControl
    {
        // An on/off switch, for a bool.
        Toggle,
        // A slider over Min to Max.
        Slider,
        // Buttons stepping through Choices.
        Choice,
        // A key binding, held as the text of a KeyCombination.
        Binding,
        // A value of any other type -- a number without a range, a vector, a
        // name -- for a setting the window does not show: the reset and the
        // profiles set it, and it is checked by its type (ValueType).
        Value,
        // No value: a button that opens a dialog of the mod's (DIALOG). Never
        // among a mod's Settings, so nothing that sets values meets it.
        Dialog,
    }

    // How a mod keeps a value set in ReDefinition's window.
    internal enum SettingsSaving
    {
        // The mod saves it in its own files, as its own window does, and keeps it.
        // That is so for KSP, Scatterer, EVE, Parallax and Firefly.
        // The store keeps it as well until the mod has saved it, or while the mod cannot
        // take it yet, and hands it over at the next chance.
        InModFiles,
        // The mod saves it per save: kept here as well, and set into every save
        // as that save loads. That is so for TUFX and Distant Object.
        // A choice here is for the game, not one save.
        // A change made in the mod's own window to a choice kept here becomes that choice (BundledStore.TakeFromMod).
        // A per-save value put back is a change to its save like one made in the mod's own window.
        // TUFX keeps it in the game, which KSP writes when it saves, so a quicksave made before brings back what
        // it holds. Distant Object writes the save's own file at once.
        PerSave,
        // The mod has no save routine of its own: kept here, and set at every
        // start and scene change. That is so for Deferred and Waterfall.
        AtEveryStart,
    }

    // What a setting is to the profiles (docs/player/graphics-profiles.md): a profile
    // sets quality only -- never the look an author or a visual pack chose, nor
    // interface, debug, physics or antialiasing switches. The reset sets all of
    // them. Which setting is which its registration says (`kind`).
    internal enum SettingKind
    {
        // What makes the image better and costs frame time: what a profile sets.
        Quality,
        // The look an author or a visual pack chose: never set by a profile.
        Taste,
        // Anything else -- interface, debug, physics, antialiasing: set by the reset only.
        Other,
    }

    // One setting of another mod, as the settings window shows it. Its value
    // travels as text in the invariant culture -- as a ConfigNode holds it, and
    // as bundled.cfg stores it.
    internal sealed class BundledSetting
    {
        // Stable, "<mod id>.<name>": the key in bundled.cfg; a changed key loses a
        // stored choice.
        public string Key;
        public string Title;
        public string Tooltip = "";
        public SettingControl Control;
        public SettingKind Kind = SettingKind.Other;

        // Slider: its range, whether it snaps to whole numbers, and its step from
        // Min; 0 for none.
        public float Min;
        public float Max;
        public bool WholeNumbers;
        public float Step;

        // Choice: the values, in the order the control steps through them, and
        // what the window calls them; null labels show the values themselves.
        public string[] Choices;
        public string[] ChoiceLabels;

        // Where the values come from when only the running mod knows them
        // (TUFX's profiles); null takes Choices.
        public Func<string[]> ChoicesSource;

        // The values to choose from now: ChoicesSource's where there is one, else Choices.
        public string[] CurrentChoices()
        {
            return ChoicesSource != null ? ChoicesSource() : Choices;
        }

        // Binding: how many modifiers the mod can keep for it. Scatterer holds one
        // modifier beside each of its keys.
        public int MaxModifiers = 2;

        // Binding: the key of another binding of the same mod whose modifier works
        // with this one's key as well, and the other way round -- Scatterer opens
        // its window on either modifier with either key. The Keys tab counts the
        // crossed combinations among the conflicts. Null for most.
        public string CrossedWith;

        // Binding: the Keys tab's section it stands in (SettingRegistration.KeyGroup).
        public const string ModsGroup = "Mods";
        public string Group = ModsGroup;

        // Binding: kept by ReDefinition, not by the mod (KeptBindings) -- there to
        // set whether or not the mod is ready, and nothing of the mod's to save.
        public bool KeptHere;

        // Value: the member's type, which a value must parse into.
        public Type ValueType;

        // When a change takes effect, as the row's tooltip and the log say.
        public ApplyWindow Window = ApplyWindow.Unspecified;

        // The mod's value now, and a change to it. Read returns null while the
        // mod's objects are not there to ask.
        public Func<string> Read;
        public Action<string> Write;

        // Whether the mod can take a value now; null is always.
        public Func<bool> Applicable;

        // For a mod that keeps its values per save: the save it keeps them for
        // now, which the value from before ReDefinition is kept for as well; null
        // for a value kept for the game as a whole.
        public Func<string> Context;

        // The heading of the group it stands in within its tab; null for none.
        public string Section;

        // Slider: shown as a percentage of its value, which runs from 0 to 1.
        public bool Percent;

        // False: set directly, as the mod's own screen sets it -- editable with
        // the bundling off, never kept in bundled.cfg, never set by a profile nor
        // put back by the restore. KSP's audio, gameplay, system and input.
        public bool Bundled = true;

        // Dialog: the button's text, the dialog's title and width, and its content
        // from the mod, asked for each time it opens.
        public string DialogButton;
        public string DialogHeading;
        public float DialogWidth;
        public Func<DialogGUIBase[]> DialogContent;

        // Set by the mod that declares it.
        public IBundledMod Owner;
    }

    // A mod whose settings the window shows, reached through reflection
    // (docs/reference/how-each-mod-keeps-its-settings.md).
    internal interface IBundledMod
    {
        // Stable, lower case: the prefix of its settings' keys, and what the
        // main-menu notice remembers having asked about.
        string Id { get; }

        // As players know it.
        string ModName { get; }

        // The mod is loaded, and its settings object is the shape this was
        // written against.
        bool IsInstalled { get; }

        // Settings this build of the mod does not offer, or that another mod
        // holds for itself: named here, so the log and the check outside the game
        // show them.
        IList<string> DroppedMembers { get; }

        // The assembly its toolbar button's click handler lives in; null when
        // it has no button to take over.
        string ButtonAssembly { get; }

        // The namespace its button is registered with in ToolbarControl, for
        // mods that make their button through it; null otherwise.
        string ToolbarControlNamespace { get; }

        // Its own settings window, as "<type>.<method>" of the function that
        // draws it. The type is that of the object it is drawn by, or the declaring
        // type of a static one.
        // It serves the Advanced button, and the close button added to it (ModWindowClose).
        // Null where it offers none worth opening from here.
        string OwnWindow { get; }

        // The type OwnWindow names, as it was found in the mod's folder:
        // ModWindowClose hooks this one, not another copy of the mod's DLL loaded
        // first. Null where OwnWindow is.
        Type OwnWindowType { get; }

        // Its settings, as the window shows them in its order.
        IList<BundledSetting> Settings { get; }

        // Which of its defaults apply (docs/reference/mod-defaults.md): the build installed
        // where builds differ -- Scatterer's public one or Volumetric Clouds' --
        // or, for TUFX, whether Volumetric Clouds is installed; null where one
        // set of defaults fits every build.
        string Build { get; }

        // The installed version its defaults are checked against; null where it
        // cannot be read.
        string Version { get; }

        // How a value set here lasts, and the mod's own save routine for it --
        // called once after a batch of changes, not per setting.
        SettingsSaving Saving { get; }
        // The mod's own save routine, called once after a batch of changes.
        void Save();

        // Once, as the mod is built in the game: hooks that let a change made
        // in the mod's own window reach ReDefinition's. Its failure costs only that.
        void InstallHooks();
    }
}
