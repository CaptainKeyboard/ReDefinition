using System.Collections.Generic;

namespace ReDefinition.Settings
{
    // Where the bundled settings show in the settings window: tabs by what a setting
    // does, each listing the rows of every mod, as the registrations
    // place them (docs/modders/registering-a-mod.md): a setting's `row` and `order`,
    // `rowUnless`, and a mod's `tab` for its Advanced button.
    //
    // Shown of the graphics mods is what players set in a game's graphics menu:
    // one row per feature -- its quality, or whether it is on -- for the features
    // one sees and that cost frame time: shadows, reflections, clouds, the sea, the
    // ground's detail and scatter, re-entry, engine plumes, flares, distant
    // vessels; and the few switches players ask for beyond graphics -- waves moving
    // vessels, the clouds' sounds, names on flares, solid scatter. Not shown: a
    // feature's finer switches and technical parameters, tessellation among them;
    // the look a visual pack decides; switches for troubleshooting and
    // compatibility; the rest of a mod's interface. Those stay in the mod's own
    // window, through Advanced. A setting not shown stays bundled: a profile still
    // sets it for the frame time it costs, and a value from before ReDefinition can
    // still be put back (docs/reference/how-each-mod-keeps-its-settings.md).
    //
    // Of KSP, every control of its own settings screen is shown, so that the
    // window can stand in for that screen: its graphics in the graphics tabs, and
    // its audio, gameplay, system and input settings in tabs of their own.
    internal static class WindowLayout
    {
        private struct Row
        {
            public BundledSetting Setting;
            public int Order;
            public int Position;
        }

        // The rows of a tab, of the mods installed now.
        internal static List<BundledSetting> In(SettingCategory tab)
        {
            return In(tab, BundledSettings.Installed());
        }

        // The rows of a tab among the installed mods given: by their order -- one
        // without an order after the numbered ones -- then by mod, as ModRegistry
        // orders them, and as each registration lists its settings. The check
        // outside the game asks the same of the mods it builds.
        internal static List<BundledSetting> In(SettingCategory tab, IList<IBundledMod> installed)
        {
            List<Row> rows = new List<Row>();
            int position = 0;
            foreach (IBundledMod mod in installed)
            {
                RegisteredMod registered = mod as RegisteredMod;
                if (registered == null || !registered.IsInstalled) continue;
                foreach (SettingRegistration entry in registered.Registration.Settings)
                {
                    position++;
                    if (entry.Row != tab) continue;
                    // The same feature's row of that mod stands in for it.
                    if (entry.RowUnless != null && Contains(installed, entry.RowUnless)) continue;
                    BundledSetting setting = Of(mod, entry.Name);
                    if (setting == null) continue;
                    rows.Add(new Row { Setting = setting, Order = entry.Order ?? int.MaxValue, Position = position });
                }
            }
            rows.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Position.CompareTo(b.Position));

            List<BundledSetting> list = new List<BundledSetting>();
            foreach (Row row in rows) list.Add(row.Setting);
            return list;
        }

        // The mods whose Advanced button is in a tab: where their own window's
        // subject is.
        internal static List<IBundledMod> AdvancedIn(SettingCategory tab)
        {
            List<IBundledMod> list = new List<IBundledMod>();
            foreach (IBundledMod mod in BundledSettings.Installed())
            {
                RegisteredMod registered = mod as RegisteredMod;
                if (registered != null && registered.Registration.Tab == tab) list.Add(mod);
            }
            return list;
        }

        private static bool Contains(IList<IBundledMod> installed, string id)
        {
            foreach (IBundledMod mod in installed)
                if (mod.IsInstalled && mod.Id == id) return true;
            return false;
        }

        private static BundledSetting Of(IBundledMod mod, string name)
        {
            string key = mod.Id + "." + name;
            foreach (BundledSetting setting in mod.Settings)
                if (setting.Key == key) return setting;
            return null;
        }
    }
}
