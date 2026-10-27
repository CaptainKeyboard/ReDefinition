using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using System;
using KSP.UI.Screens;
using ReDefinition.Core;
using ReDefinition.Settings;
using ReDefinition.Ui;
using UnityEngine;

namespace ReDefinition.Window
{
    // Hides the toolbar buttons of the mods whose settings are bundled here,
    // for the run, and brings them back when the bundling is switched off. Only
    // where ReDefinition's window opens the mod's own (IBundledMod.OwnWindow) or a
    // dialog of the mod's (DIALOG), so that what the rows leave out stays reachable.
    // A build without the method its registration names for that window keeps its
    // button.
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
        // The buttons hidden here with the scenes they had, whose each button is (null for none of the bundled mods), and
        // the mods whose hiding the log has told.
        private static readonly Dictionary<ApplicationLauncherButton, ApplicationLauncher.AppScenes> hidden =
            new Dictionary<ApplicationLauncherButton, ApplicationLauncher.AppScenes>();
        private static readonly Dictionary<ApplicationLauncherButton, IBundledMod> owners =
            new Dictionary<ApplicationLauncherButton, IBundledMod>();
        private static readonly HashSet<string> reported = new HashSet<string>();

        // The launcher's lists of mod buttons, shown and hidden, looked up once.
        private static FieldInfo modList;
        private static FieldInfo modListHidden;
        private static bool resolved;

        // ToolbarControl's static list of its instances and their nameSpace and stockButton fields, looked up once.
        private static FieldInfo tcList;
        private static FieldInfo tcNamespace;
        private static FieldInfo tcStock;
        private static bool tcResolved;

        // Buttons ReDefinition hides for its own reasons, whatever the bundling, by
        // the namespace they are registered with in ToolbarControl: shown while
        // their test holds (KeepOwn), with the scenes they had.
        private static readonly Dictionary<string, Func<bool>> ownButtons = new Dictionary<string, Func<bool>>();
        private static readonly Dictionary<ApplicationLauncherButton, ApplicationLauncher.AppScenes> ownHidden =
            new Dictionary<ApplicationLauncherButton, ApplicationLauncher.AppScenes>();

        // Hides the ToolbarControl button of that namespace while the test says false, shows it again once it says true.
        internal static void KeepOwn(string nameSpace, Func<bool> shown)
        {
            ownButtons[nameSpace] = shown;
        }

        // Every two seconds and at once after a change: ReDefinition's own buttons, then the bundled mods' buttons hidden
        // or shown again as the bundling and the player's choices say.
        public static void Refresh()
        {
            if (ApplicationLauncher.Instance == null) return;
            Guarded("toolbar-own", "A toolbar button ReDefinition hides could not be handled", OwnButtons);
            if (!BundledSettings.Enabled)
            {
                // Showing the buttons again must not throw out of here: this is
                // called from the main-menu notice's answer and from the tick
                // that would then try it again every two seconds.
                Guarded("toolbar-restore", "Other mods' toolbar buttons could not be shown again", RestoreAll);
                return;
            }

            Guarded("toolbar-takeover", "Other mods' toolbar buttons could not be handled", HideModButtons);
            Guarded("toolbar-takeover-tc", "ToolbarControl's buttons could not be handled", HideToolbarControlButtons);
            Forget();
        }

        // One step of Refresh: a failure is a warning, once per kind, and the other steps go on.
        private static void Guarded(string key, string failure, Action step)
        {
            try
            {
                step();
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn(key, failure + " (" + CompatibilityLog.Reason(e) + ").");
            }
        }

        // Hides the launcher's buttons of the bundled mods that are to be hidden, and shows those the player keeps.
        private static void HideModButtons()
        {
            // A button the player has asked to keep goes back where it was.
            foreach (ApplicationLauncherButton button in new List<ApplicationLauncherButton>(hidden.Keys))
            {
                IBundledMod owner;
                if (!owners.TryGetValue(button, out owner) || WouldHide(owner)) continue;
                Show(button);
            }

            foreach (ApplicationLauncherButton button in ModButtons())
            {
                if (button == null) continue;
                // Hidden already -- unless the mod has set its scenes again, or
                // the launcher has switched it on again in a new layout.
                if (button.VisibleInScenes == ApplicationLauncher.AppScenes.NEVER)
                {
                    KeepOff(button);
                    continue;
                }
                IBundledMod mod = CachedOwner(button);
                if (mod != null && WouldHide(mod)) Hide(button, mod);
            }
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
            // Mods that draw only while KSP's interface shows (KspUiState).
            KspUiState.ResyncShown();
            string before = button.toggleButton != null ? button.toggleButton.CurrentState.ToString() : "no toggle";
            if (button.toggleButton != null && button.toggleButton.CurrentState == KSP.UI.UIRadioButton.State.True)
                button.SetFalse(true);
            button.SetTrue(true);
            string after = button.toggleButton != null ? button.toggleButton.CurrentState.ToString() : "no toggle";
            // Whether the window then shows is told a second later (ModWindowClose.ReportOpening).
            Debug.Log(Log.Tag + " " + mod.ModName + "'s own window opened through its toolbar button ('"
                      + button.name + "', scenes " + button.VisibleInScenes + ", state " + before + " -> " + after
                      + ").");
            ModWindowClose.ExpectOpening(mod);
            return true;
        }

        // Whether ReDefinition could hide this mod's button at all: it names one,
        // and ReDefinition's window opens the mod's own window in its place, or a
        // dialog the mod gives it (DIALOG).
        internal static bool CanHide(IBundledMod mod)
        {
            RegisteredMod registered = mod as RegisteredMod;
            bool opensInWindow = mod.OwnWindow != null || (registered != null && registered.Dialogs.Count > 0);
            return opensInWindow && (mod.ButtonAssembly != null || mod.ToolbarControlNamespace != null);
        }

        // Whether it is hidden: it could be, and the player has not kept it
        // (BundledSettings.HidesButton).
        internal static bool WouldHide(IBundledMod mod)
        {
            return CanHide(mod) && BundledSettings.HidesButton(mod.Id);
        }

        // Whether the mod has a toolbar button in this scene; false where looking throws.
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

        // The mod's button: the one ToolbarControl made for its namespace, else the launcher's button its assembly owns.
        private static ApplicationLauncherButton ButtonOf(IBundledMod mod)
        {
            if (mod == null || ApplicationLauncher.Instance == null) return null;
            if (mod.ToolbarControlNamespace != null)
            {
                foreach (KeyValuePair<string, ApplicationLauncherButton> control in ControlButtons())
                    if (control.Key == mod.ToolbarControlNamespace && control.Value != null) return control.Value;
            }
            if (mod.ButtonAssembly == null) return null;
            foreach (ApplicationLauncherButton button in ModButtons())
                if (button != null && CachedOwner(button) == mod) return button;
            return null;
        }

        // Hides the button, its scenes kept to give back, and logs it once per mod.
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

        // A button hidden here that stands on again.
        // When a scene starts, KSP's
        // launcher moves every button into its new layout and switches each on
        // there (SpawnSimpleLayout, UIList.AddItem).
        // DetermineVisibility then moves
        // a button only between its shown and hidden lists, so one in the hidden
        // list already stays on screen (ApplicationLauncher, decompiled).
        // It is switched off as the launcher's SetHidden does it.
        private static void KeepOff(ApplicationLauncherButton button)
        {
            if (!hidden.ContainsKey(button)) return;
            SwitchOff(button);
        }

        // Switches the button's object off and has the launcher lay out its list again.
        private static void SwitchOff(ApplicationLauncherButton button)
        {
            if (!button.gameObject.activeSelf) return;
            button.gameObject.SetActive(false);
            if (scaleModList == null)
                scaleModList = typeof(ApplicationLauncher).GetMethod("ScaleModList", TypeLookup.Any, null, Type.EmptyTypes,
                    null);
            if (scaleModList != null) scaleModList.Invoke(ApplicationLauncher.Instance, null);
        }

        // ApplicationLauncher.ScaleModList, looked up on first use.
        private static MethodInfo scaleModList;

        // The counterpart of KeepOff: the launcher switches a button on only as it
        // moves it from its hidden list, so one switched off here while it stood in
        // the shown list is switched on here.
        private static void SwitchOnWhereShown(ApplicationLauncherButton button)
        {
            if (button.gameObject.activeSelf || !ApplicationLauncher.Instance.ShouldBeVisible(button)) return;
            button.gameObject.SetActive(true);
        }

        // The bundled mod a button belongs to, looked up once per button.
        private static IBundledMod CachedOwner(ApplicationLauncherButton button)
        {
            IBundledMod mod;
            if (owners.TryGetValue(button, out mod)) return mod;
            mod = Owner(button);
            owners[button] = mod;
            return mod;
        }

        // Hides the ToolbarControl buttons of the bundled mods that are to be hidden.
        private static void HideToolbarControlButtons()
        {
            foreach (KeyValuePair<string, ApplicationLauncherButton> control in ControlButtons())
            {
                IBundledMod mod = OwnerByNamespace(control.Key);
                if (mod == null || !WouldHide(mod)) continue;

                ApplicationLauncherButton stock = control.Value;
                if (stock == null) continue;
                if (stock.VisibleInScenes == ApplicationLauncher.AppScenes.NEVER) KeepOff(stock);
                else Hide(stock, mod);
            }
        }

        // The buttons KeepOwn names, hidden or shown again as their test says, as
        // the bundled mods' are (Hide, KeepOff, Show).
        private static void OwnButtons()
        {
            foreach (ApplicationLauncherButton gone in new List<ApplicationLauncherButton>(ownHidden.Keys))
                if (gone == null) ownHidden.Remove(gone);
            if (ownButtons.Count == 0) return;
            foreach (KeyValuePair<string, ApplicationLauncherButton> control in ControlButtons())
            {
                string ns = control.Key;
                Func<bool> shown;
                if (ns == null || !ownButtons.TryGetValue(ns, out shown)) continue;
                ApplicationLauncherButton stock = control.Value;
                if (stock == null) continue;
                ApplicationLauncher.AppScenes scenes;
                if (!shown())
                {
                    if (ownHidden.ContainsKey(stock))
                    {
                        // Its scenes set again meanwhile: those are the ones to
                        // give back, and it is hidden anew.
                        if (stock.VisibleInScenes != ApplicationLauncher.AppScenes.NEVER)
                        {
                            ownHidden[stock] = stock.VisibleInScenes;
                            stock.VisibleInScenes = ApplicationLauncher.AppScenes.NEVER;
                        }
                        SwitchOff(stock);
                        continue;
                    }
                    if (stock.VisibleInScenes == ApplicationLauncher.AppScenes.NEVER) continue;
                    ownHidden[stock] = stock.VisibleInScenes;
                    stock.VisibleInScenes = ApplicationLauncher.AppScenes.NEVER;
                    Debug.Log(Log.Tag + " Toolbar button of " + ns + " hidden, as the settings say.");
                }
                else if (ownHidden.TryGetValue(stock, out scenes))
                {
                    ownHidden.Remove(stock);
                    stock.VisibleInScenes = scenes;
                    SwitchOnWhereShown(stock);
                    Debug.Log(Log.Tag + " Toolbar button of " + ns + " shown again, as the settings say.");
                }
            }
        }

        // The installed bundled mod registered with that namespace in ToolbarControl; null for none.
        private static IBundledMod OwnerByNamespace(string ns)
        {
            if (string.IsNullOrEmpty(ns)) return null;
            foreach (IBundledMod mod in BundledSettings.Mods)
                if (mod.IsInstalled && mod.ToolbarControlNamespace == ns) return mod;
            return null;
        }

        // Each instance ToolbarControl holds: the namespace it registered with and its stock button, either null where it
        // has none. Nothing without ToolbarControl.
        private static IEnumerable<KeyValuePair<string, ApplicationLauncherButton>> ControlButtons()
        {
            if (!ResolveToolbarControl()) yield break;
            IEnumerable controls = tcList.GetValue(null) as IEnumerable;
            if (controls == null) yield break;
            foreach (object control in controls)
            {
                if (control == null) continue;
                yield return new KeyValuePair<string, ApplicationLauncherButton>(
                    tcNamespace.GetValue(control) as string, tcStock.GetValue(control) as ApplicationLauncherButton);
            }
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
                    tcList = control.GetField("tcList", TypeLookup.Any);
                    tcNamespace = control.GetField("nameSpace", TypeLookup.Any);
                    tcStock = control.GetField("stockButton", TypeLookup.Any);
                    if (tcList != null && !tcList.IsStatic) tcList = null;
                }
            }
            return tcList != null && tcNamespace != null && tcStock != null;
        }

        // One button back where it was: the mod is still bundled, only its button
        // stays. Its scenes are kept where the launcher throws, so the next pass
        // tries again instead of leaving it hidden with nothing to restore to.
        private static void Show(ApplicationLauncherButton button)
        {
            ApplicationLauncher.AppScenes scenes;
            if (button == null || !hidden.TryGetValue(button, out scenes))
            {
                hidden.Remove(button);
                return;
            }

            try
            {
                if (button.VisibleInScenes == ApplicationLauncher.AppScenes.NEVER) button.VisibleInScenes = scenes;
                SwitchOnWhereShown(button);
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("toolbar-show-one", "A toolbar button of another mod could not be shown again ("
                                      + CompatibilityLog.Reason(e) + "); it is tried again.");
                return;
            }

            hidden.Remove(button);
            IBundledMod owner;
            if (owners.TryGetValue(button, out owner) && owner != null && !Hides(owner))
            {
                reported.Remove(owner.Id);
                Debug.Log(Log.Tag + " " + owner.ModName + "'s toolbar button is shown again, as chosen in the window.");
            }
        }

        // Every hidden button shown again with its scenes; one that throws is kept for the next pass.
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
                    SwitchOnWhereShown(pair.Key);
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

        // Drops the destroyed buttons from a table.
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

        // The bundled mod whose assembly holds the button's click handler; null for none.
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
            ResolveLauncher();
            List<ApplicationLauncherButton> buttons = new List<ApplicationLauncherButton>();
            Add(buttons, modList);
            Add(buttons, modListHidden);
            return buttons;
        }

        // Looks up the launcher's lists of mod buttons, once.
        private static void ResolveLauncher()
        {
            if (resolved) return;
            resolved = true;
            modList = typeof(ApplicationLauncher).GetField("appListMod", TypeLookup.Any);
            modListHidden = typeof(ApplicationLauncher).GetField("appListModHidden", TypeLookup.Any);
            if (modList == null || modListHidden == null)
                Debug.LogWarning(Log.Tag + " KSP's launcher is not the one this mod was written against:"
                                 + " other mods' toolbar buttons stay.");
        }

        // The launcher's list of shown mod buttons, in the order it lays them out; null where KSP's launcher has no
        // such list.
        internal static IList ModList(ApplicationLauncher launcher)
        {
            ResolveLauncher();
            return modList != null ? modList.GetValue(launcher) as IList : null;
        }

        // Adds the buttons of one of the launcher's lists.
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
