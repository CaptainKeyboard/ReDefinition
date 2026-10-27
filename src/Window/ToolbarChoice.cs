using System.Collections.Generic;
using System;
using ReDefinition.Settings;

namespace ReDefinition.Window
{
    // The toolbar block of the settings window and of the main menu's notice: one switch for every button
    // ReDefinition can hide, and a switch for each mod's own.
    // The two differ in where a choice is kept -- the window's waits for Apply, the notice's is bundled.cfg at once --
    // and in how the switches are drawn. What they share is here: which mods are listed, when all of them are hidden,
    // and what the switch for all does.
    // Better Time Warp's button, hidden for ReDefinition's own reason, joins the window's block as one more entry.
    internal sealed class ToolbarChoice
    {
        // The mods listed, where a choice is read and written, and the extra entry's own (null for none).
        public readonly List<IBundledMod> Mods;
        private readonly Func<IBundledMod, bool> hidden;
        private readonly Action<IBundledMod, bool> set;
        private readonly Func<bool> extraHidden;
        private readonly Action<bool> setExtra;

        // The block of the installed mods whose button can be hidden. `extraHidden` and `setExtra` are the extra
        // entry's, both null for none.
        public ToolbarChoice(Func<IBundledMod, bool> hidden, Action<IBundledMod, bool> set, Func<bool> extraHidden = null,
                             Action<bool> setExtra = null)
            : this(InstalledWithButton(), hidden, set, extraHidden, setExtra)
        {
        }

        // The same for the mods given, which a test names.
        internal ToolbarChoice(IEnumerable<IBundledMod> mods, Func<IBundledMod, bool> hidden, Action<IBundledMod, bool> set,
                               Func<bool> extraHidden = null, Action<bool> setExtra = null)
        {
            Mods = new List<IBundledMod>(mods);
            this.hidden = hidden;
            this.set = set;
            this.extraHidden = extraHidden;
            this.setExtra = setExtra;
        }

        // The installed mods whose toolbar button ReDefinition can hide.
        private static List<IBundledMod> InstalledWithButton()
        {
            List<IBundledMod> mods = new List<IBundledMod>();
            foreach (IBundledMod mod in BundledSettings.Installed())
                if (ToolbarTakeover.CanHide(mod)) mods.Add(mod);
            return mods;
        }

        // How many switches the block holds besides the one for all.
        public int Count
        {
            get { return Mods.Count + (extraHidden != null ? 1 : 0); }
        }

        // Whether the block has nothing to choose.
        public bool Empty
        {
            get { return Count == 0; }
        }

        // Whether every button in the block is to be hidden; a block of the extra entry alone counts the extra one.
        public bool AllHidden()
        {
            return (Mods.Count == 0 || AllModsHidden()) && (extraHidden == null || extraHidden());
        }

        // Hides or shows every button in the block. False, and nothing set, where they all are so already: the
        // switches below set the switch for all in every frame (DialogGUIToggle.Update), which fires its handler
        // too, and one of them switched off must not switch the others off.
        public bool SetAll(bool hide)
        {
            if (hide == AllHidden()) return false;
            foreach (IBundledMod mod in Mods) set(mod, hide);
            if (setExtra != null) setExtra(hide);
            return true;
        }

        // Whether the mod's button is to be hidden.
        public bool IsHidden(IBundledMod mod)
        {
            return hidden(mod);
        }

        // Hides or shows the mod's button.
        public void Set(IBundledMod mod, bool hide)
        {
            set(mod, hide);
        }

        // Whether every mod's button but the extra entry's is to be hidden; false for no mods.
        private bool AllModsHidden()
        {
            foreach (IBundledMod mod in Mods)
                if (!hidden(mod)) return false;
            return Mods.Count > 0;
        }
    }
}
