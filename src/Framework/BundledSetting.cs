using System;
using System.Collections.Generic;

namespace ReDefinition.Framework
{
    // The tabs of the settings window, by feature, in this order. Which setting
    // shows in which, and in what order, its registration says (`row`, `order`;
    // WindowLayout).
    internal enum SettingCategory
    {
        Profiles,
        // ReDefinition's own upscaler and frame generation lead it, then KSP's
        // render quality, textures, V-Sync and frame limit.
        General,
        ShadowsAndReflections,
        // Clouds, sea and ground.
        Planets,
        // Re-entry, engines, flares and distant vessels.
        Effects,
        // Every key binding: ReDefinition's, the mods' and KSP's.
        Keys,
        Interface,
    }

    internal enum SettingControl
    {
        Toggle,
        Slider,
        Choice,
        // A key binding, held as the text of a KeyCombination.
        Binding,
        // A value of any other type -- a number without a range, a vector, a
        // name -- for a setting the window does not show: the reset and the
        // profiles set it, and it is checked by its type (ValueType).
        Value,
    }

    // How a mod keeps a value set in ReDefinition's window.
    internal enum SettingsSaving
    {
        // The mod saves it in its own files, as its own window does, and keeps it.
        InModFiles,
        // The mod saves it per save: kept here as well, and set into every save
        // as that save loads.
        PerSave,
        // The mod has no save routine of its own: kept here, and set at every
        // start and scene change.
        AtEveryStart,
    }

    // What a setting is to the profiles (docs/player/graphics-profiles.md): a profile
    // sets quality only -- never the look an author or a visual pack chose, nor
    // interface, debug, physics or antialiasing switches. The reset sets all of
    // them. Which setting is which its registration says (`kind`).
    internal enum SettingKind
    {
        Quality,
        Taste,
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

        // Slider: its range, and whether it snaps to whole numbers.
        public float Min;
        public float Max;
        public bool WholeNumbers;

        // Choice: the values, in the order the control steps through them, and
        // what the window calls them; null labels show the values themselves.
        public string[] Choices;
        public string[] ChoiceLabels;

        // Where the values come from when only the running mod knows them
        // (TUFX's profiles); null takes Choices.
        public Func<string[]> ChoicesSource;

        public string[] CurrentChoices()
        {
            return ChoicesSource != null ? ChoicesSource() : Choices;
        }

        // Value: the member's type, which a value must parse into.
        public Type ValueType;

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
        // draws it -- the type of the object it is drawn by, or the declaring
        // type of a static one: for the Advanced button, and for the close
        // button added to it (ModWindowClose). Null where it offers none worth
        // opening from here.
        string OwnWindow { get; }

        // The type OwnWindow names, as it was found in the mod's folder:
        // ModWindowClose hooks this one, not another copy of the mod's DLL loaded
        // first. Null where OwnWindow is.
        Type OwnWindowType { get; }

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
        void Save();

        // Once, as the mod is built in the game: hooks that let a change made
        // in the mod's own window reach ReDefinition's. Its failure costs only that.
        void InstallHooks();
    }
}
