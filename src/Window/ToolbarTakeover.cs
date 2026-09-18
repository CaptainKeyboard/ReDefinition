using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using System;
using KSP.UI.Screens;
using ReDefinition.Core;
using ReDefinition.Settings;
using ReDefinition.Upscaler;
using UnityEngine;

namespace ReDefinition.Window
{
    // Hides the toolbar buttons of the mods whose settings are bundled here,
    // for the run, and brings them back when the bundling is switched off. Only
    // where ReDefinition's window opens the mod's own (IBundledMod.OwnWindow):
    // what the rows leave out stays reachable -- a build without the method its
    // registration names for that window keeps its button.
    //
    // KSP's launcher, decompiled: ApplicationLauncherButton.VisibleInScenes is
    // public, and its setter calls ApplicationLauncher.DetermineVisibility,
    // which moves the button to the launcher's visible or hidden list. So
    // NEVER hides a button and the value it had brings it back; nothing is
    // removed, and the mod keeps its button. Whose a button is: the assembly
    // of its click handler (onTrue), looked up once per button. The mods make
    // their buttons anew in every scene, some a little after the launcher is
    // ready, so this looks every two seconds.
    //
    // A button made through ToolbarControl (TUFX's) has ToolbarControl's
    // handler, so it is found by the namespace it registered with -- the
    // instances' tcList, nameSpace and stockButton, decompiled. Only KSP's own
    // launcher is touched: Blizzy's toolbar saves its layout to its own file
    // whenever a button's visibility changes (toolbar-settings.dat), so a
    // button there stays.
    internal static class ToolbarTakeover
    {
        private static readonly Dictionary<ApplicationLauncherButton, ApplicationLauncher.AppScenes> hidden =
            new Dictionary<ApplicationLauncherButton, ApplicationLauncher.AppScenes>();
        private static readonly Dictionary<ApplicationLauncherButton, IBundledMod> owners =
            new Dictionary<ApplicationLauncherButton, IBundledMod>();
        private static readonly HashSet<string> reported = new HashSet<string>();

        private static FieldInfo modList;
        private static FieldInfo modListHidden;
        private static bool resolved;

        private static FieldInfo tcList;
        private static FieldInfo tcNamespace;
        private static FieldInfo tcStock;
        private static bool tcResolved;

        public static void Refresh()
        {
            if (ApplicationLauncher.Instance == null) return;
            if (!BundledSettings.Enabled)
            {
                // Showing the buttons again must not throw out of here: this is
                // called from the main-menu notice's answer and from the tick
                // that would then try it again every two seconds.
                try
                {
                    RestoreAll();
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn("toolbar-restore", "Other mods' toolbar buttons could not be shown again ("
                                                             + CompatibilityLog.Reason(e) + ").");
                }
                return;
            }

            try
            {
                foreach (ApplicationLauncherButton button in ModButtons())
                {
                    // Hidden already -- unless the mod has set its scenes again.
                    if (button == null || button.VisibleInScenes == ApplicationLauncher.AppScenes.NEVER) continue;
                    IBundledMod mod = CachedOwner(button);
                    if (mod != null && WouldHide(mod)) Hide(button, mod);
                }
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("toolbar-takeover", "Other mods' toolbar buttons could not be handled ("
                                                          + CompatibilityLog.Reason(e) + ").");
            }

            try
            {
                HideToolbarControlButtons();
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("toolbar-takeover-tc", "ToolbarControl's buttons could not be handled ("
                                                             + CompatibilityLog.Reason(e) + ").");
            }

            Forget();
        }

        // The mod's own settings window, through its own toolbar button, hidden
        // or not: the click handler behind it opens the window
        // (ApplicationLauncherButton.SetTrue, decompiled, sets the toggle's
        // state with the call made). Off first where the button still stands
        // on -- its window closed by its own close button, which not every mod
        // reports back to its button. False where it has no button here: none
        // in this scene, or one on Blizzy's toolbar only.
        internal static bool OpenOwnWindow(IBundledMod mod)
        {
            ApplicationLauncherButton button = ButtonOf(mod);
            if (button == null) return false;
            if (button.toggleButton != null && button.toggleButton.CurrentState == KSP.UI.UIRadioButton.State.True)
                button.SetFalse(true);
            button.SetTrue(true);
            return true;
        }

        // Whether a bundled mod's toolbar button is hidden: it names one, and
        // ReDefinition's window opens the mod's own window in its place.
        internal static bool WouldHide(IBundledMod mod)
        {
            return mod.OwnWindow != null && (mod.ButtonAssembly != null || mod.ToolbarControlNamespace != null);
        }

        internal static bool HasButton(IBundledMod mod)
        {
            try
            {
                return ButtonOf(mod) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Whether the mod's button is one hidden here: then its own window has
        // no way to be closed but the one this mod adds (ModWindowClose).
        internal static bool Hides(IBundledMod mod)
        {
            foreach (ApplicationLauncherButton button in hidden.Keys)
            {
                IBundledMod owner;
                if (button != null && owners.TryGetValue(button, out owner) && owner == mod) return true;
            }
            return false;
        }

        // The mod's own window closed the way its button closes it. A window
        // opened by its hotkey can leave the button off, and turning off what is
        // off calls nothing: it is turned on first without a call
        // (UIRadioButton.SetState, CallType.APPLICATIONSILENT, decompiled).
        internal static bool CloseOwnWindow(IBundledMod mod)
        {
            ApplicationLauncherButton button = ButtonOf(mod);
            if (button == null) return false;
            if (button.toggleButton != null && button.toggleButton.CurrentState != KSP.UI.UIRadioButton.State.True)
                button.SetTrue(false);
            button.SetFalse(true);
            return true;
        }

        private static ApplicationLauncherButton ButtonOf(IBundledMod mod)
        {
            if (mod == null || ApplicationLauncher.Instance == null) return null;
            if (mod.ToolbarControlNamespace != null && ResolveToolbarControl())
            {
                IEnumerable controls = tcList.GetValue(null) as IEnumerable;
                if (controls != null)
                {
                    foreach (object control in controls)
                    {
                        if (control == null || tcNamespace.GetValue(control) as string != mod.ToolbarControlNamespace) continue;
                        ApplicationLauncherButton stock = tcStock.GetValue(control) as ApplicationLauncherButton;
                        if (stock != null) return stock;
                    }
                }
            }
            if (mod.ButtonAssembly == null) return null;
            foreach (ApplicationLauncherButton button in ModButtons())
                if (button != null && CachedOwner(button) == mod) return button;
            return null;
        }

        private static void Hide(ApplicationLauncherButton button, IBundledMod mod)
        {
            // A button already hidden would be remembered as belonging in NEVER,
            // and would stay hidden when the bundling is switched off. The
            // callers check as well, so that they can skip the lookup of whose a
            // button is.
            if (button.VisibleInScenes == ApplicationLauncher.AppScenes.NEVER) return;

            hidden[button] = button.VisibleInScenes;
            // Whose it is, recorded here too: a button made through
            // ToolbarControl never passes the owner cache, so without this a
            // TUFX button kept back would still clear the notice's record and
            // be announced again.
            owners[button] = mod;
            button.VisibleInScenes = ApplicationLauncher.AppScenes.NEVER;
            if (reported.Add(mod.Id))
                Debug.Log(Log.Tag + " " + mod.ModName + "'s toolbar button hidden: its settings are in"
                          + " ReDefinition's window.");
        }

        private static IBundledMod CachedOwner(ApplicationLauncherButton button)
        {
            IBundledMod mod;
            if (owners.TryGetValue(button, out mod)) return mod;
            mod = Owner(button);
            owners[button] = mod;
            return mod;
        }

        private static void HideToolbarControlButtons()
        {
            if (!ResolveToolbarControl()) return;
            IEnumerable controls = tcList.GetValue(null) as IEnumerable;
            if (controls == null) return;

            foreach (object control in controls)
            {
                if (control == null) continue;
                IBundledMod mod = OwnerByNamespace(tcNamespace.GetValue(control) as string);
                if (mod == null || !WouldHide(mod)) continue;

                ApplicationLauncherButton stock = tcStock.GetValue(control) as ApplicationLauncherButton;
                if (stock != null && stock.VisibleInScenes != ApplicationLauncher.AppScenes.NEVER) Hide(stock, mod);
            }
        }

        private static IBundledMod OwnerByNamespace(string ns)
        {
            if (string.IsNullOrEmpty(ns)) return null;
            foreach (IBundledMod mod in BundledSettings.Mods)
                if (mod.IsInstalled && mod.ToolbarControlNamespace == ns) return mod;
            return null;
        }

        // ToolbarControl is optional; without it, or in another shape, only its
        // buttons stay.
        private static bool ResolveToolbarControl()
        {
            if (!tcResolved)
            {
                tcResolved = true;
                Type control = TypeLookup.Find("ToolbarControl_NS.ToolbarControl");
                if (control != null)
                {
                    tcList = control.GetField("tcList", HostStack.Any);
                    tcNamespace = control.GetField("nameSpace", HostStack.Any);
                    tcStock = control.GetField("stockButton", HostStack.Any);
                    if (tcList != null && !tcList.IsStatic) tcList = null;
                }
            }
            return tcList != null && tcNamespace != null && tcStock != null;
        }

        private static void RestoreAll()
        {
            int shown = 0;
            Dictionary<ApplicationLauncherButton, ApplicationLauncher.AppScenes> keep =
                new Dictionary<ApplicationLauncherButton, ApplicationLauncher.AppScenes>();
            foreach (KeyValuePair<ApplicationLauncherButton, ApplicationLauncher.AppScenes> pair in hidden)
            {
                if (pair.Key == null) continue;
                try
                {
                    // The read is inside the guard as well: a button whose
                    // launcher is being torn down can throw from the getter,
                    // and that would leave every button after it hidden.
                    if (pair.Key.VisibleInScenes != ApplicationLauncher.AppScenes.NEVER) continue;
                    pair.Key.VisibleInScenes = pair.Value;
                    shown++;
                }
                catch (Exception e)
                {
                    // One button's launcher trouble must not keep the others
                    // hidden -- and the scenes it belongs in are kept, so the
                    // next pass can try again instead of leaving it hidden with
                    // nothing to bring it back to.
                    keep[pair.Key] = pair.Value;
                    CompatibilityLog.Warn("toolbar-restore-one", "A toolbar button of another mod could not be shown"
                                          + " again (" + CompatibilityLog.Reason(e) + "); it is tried again.");
                }
            }
            if (shown > 0)
                Debug.Log(Log.Tag + " " + shown + " toolbar button(s) of other mods shown again.");
            hidden.Clear();
            HashSet<string> stillHidden = new HashSet<string>();
            foreach (KeyValuePair<ApplicationLauncherButton, ApplicationLauncher.AppScenes> pair in keep)
            {
                hidden[pair.Key] = pair.Value;
                IBundledMod mod;
                if (owners.TryGetValue(pair.Key, out mod) && mod != null) stillHidden.Add(mod.Id);
            }

            // Per mod, not all or nothing: a mod still hidden must not be
            // announced as newly hidden later, and one button that could not
            // be shown again must not silence the notice for every other mod.
            reported.RemoveWhere(id => !stillHidden.Contains(id));
        }

        // Buttons destroyed with their scene.
        private static void Forget()
        {
            Prune(hidden);
            Prune(owners);
        }

        private static void Prune<T>(Dictionary<ApplicationLauncherButton, T> map)
        {
            List<ApplicationLauncherButton> gone = null;
            foreach (ApplicationLauncherButton button in map.Keys)
            {
                if (button != null) continue;
                if (gone == null) gone = new List<ApplicationLauncherButton>();
                gone.Add(button);
            }
            if (gone == null) return;
            foreach (ApplicationLauncherButton button in gone) map.Remove(button);
        }

        private static IBundledMod Owner(ApplicationLauncherButton button)
        {
            Callback onTrue = button.onTrue;
            if (onTrue == null) return null;

            foreach (Delegate handler in onTrue.GetInvocationList())
            {
                if (handler.Method == null || handler.Method.DeclaringType == null) continue;
                string assembly = handler.Method.DeclaringType.Assembly.GetName().Name;
                foreach (IBundledMod mod in BundledSettings.Mods)
                {
                    if (mod.IsInstalled && mod.ButtonAssembly != null
                        && string.Equals(mod.ButtonAssembly, assembly, StringComparison.OrdinalIgnoreCase))
                        return mod;
                }
            }
            return null;
        }

        // The launcher's mod buttons, shown and hidden -- a copy, since hiding
        // one moves it from one list to the other.
        private static List<ApplicationLauncherButton> ModButtons()
        {
            if (!resolved)
            {
                resolved = true;
                modList = typeof(ApplicationLauncher).GetField("appListMod", HostStack.Any);
                modListHidden = typeof(ApplicationLauncher).GetField("appListModHidden", HostStack.Any);
                if (modList == null || modListHidden == null)
                    Debug.LogWarning(Log.Tag + " KSP's launcher is not the one this mod was written against:"
                                     + " other mods' toolbar buttons stay.");
            }

            List<ApplicationLauncherButton> buttons = new List<ApplicationLauncherButton>();
            Add(buttons, modList);
            Add(buttons, modListHidden);
            return buttons;
        }

        private static void Add(List<ApplicationLauncherButton> buttons, FieldInfo field)
        {
            IEnumerable list = field != null ? field.GetValue(ApplicationLauncher.Instance) as IEnumerable : null;
            if (list == null) return;
            foreach (object item in list)
            {
                ApplicationLauncherButton button = item as ApplicationLauncherButton;
                if (button != null) buttons.Add(button);
            }
        }
    }
}
