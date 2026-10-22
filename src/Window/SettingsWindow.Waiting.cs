using System.Collections.Generic;
using System.Text;
using ReDefinition.Settings;

namespace ReDefinition.Window
{
    // Changes that do not take effect at once, said between Reset and Apply in
    // yellow: rows changed and not applied yet, and rows applied that wait for
    // the next scene or a restart -- the bundled mods' and ReDefinition's own.
    // The line is short; its tooltip names each setting and when it arrives.
    internal static partial class SettingsWindow
    {
        private const int WaitingListed = 8;
        private const string Yellow = "#f5d800";

        // One change that waits: its row's name, whose it is, and when it
        // arrives.
        private sealed class WaitingChange
        {
            public string Title;
            public string Owner;
            public ApplyWindow Window;
        }

        // Applied, and waiting: by key, with when it takes effect. The next
        // scene's go with the next scene; a restart's stay for the run.
        private static readonly Dictionary<string, WaitingChange> applied = new Dictionary<string, WaitingChange>();

        // Before Apply sets the rows: what it sets that does not take effect at once.
        private static void NoteWaiting()
        {
            foreach (KeyValuePair<string, WaitingChange> pair in Waiting(false)) applied[pair.Key] = pair.Value;
        }

        // What waits for a restart without a row: files a download or an install placed, which KSP takes at
        // its next start (Streamline's, XeSS frame generation's, the RTX 30 package and its preset).
        internal static void NoteRestart(string key, string title, string owner)
        {
            applied[key] = new WaitingChange { Title = title, Owner = owner, Window = ApplyWindow.Restart };
            waitingVersion = -1;
        }

        // The changes waiting for a restart the player has been asked about: Accept asks once for each.
        private static readonly HashSet<string> restartAsked = new HashSet<string>();

        // Accept, and where it sets something that takes effect only at a start -- or a download placed such
        // files -- the question whether to restart KSP now or later.
        private static void ApplyAndAskRestart()
        {
            List<string> titles = new List<string>();
            foreach (KeyValuePair<string, WaitingChange> pair in Waiting(true))
                if (pair.Value.Window == ApplyWindow.Restart && restartAsked.Add(pair.Key)) titles.Add(pair.Value.Title);
            Apply();
            if (titles.Count == 0 || !GameRestart.Available) return;
            ConfirmDialog.OverWindow("ReDefinitionRestartNow", "Restart KSP?",
                string.Join(", ", titles.ToArray()) + (titles.Count == 1 ? " takes" : " take")
                + " effect after a restart of KSP." + (GameRestart.SavesGame ? " The game is saved first." : ""), 420f,
                "Restart now", ApplyAndRestart, "Later");
        }

        // From ModWindowsAddon once a scene is ready.
        internal static void SceneChanged()
        {
            List<string> arrived = new List<string>();
            foreach (KeyValuePair<string, WaitingChange> pair in applied)
                if (pair.Value.Window != ApplyWindow.Restart) arrived.Add(pair.Key);
            foreach (string key in arrived) applied.Remove(key);
        }

        // The rows changed in the window that do not take effect at once, by key;
        // with `withApplied`, then those applied.
        private static List<KeyValuePair<string, WaitingChange>> Waiting(bool withApplied)
        {
            List<KeyValuePair<string, WaitingChange>> list = new List<KeyValuePair<string, WaitingChange>>();
            HashSet<string> seen = new HashSet<string>();
            foreach (BundledSetting setting in InstalledSettings())
            {
                if (setting.Window == ApplyWindow.Live || !model.Changed(setting.Key)) continue;
                list.Add(new KeyValuePair<string, WaitingChange>(setting.Key, new WaitingChange
                {
                    Title = setting.Title,
                    Owner = setting.Owner.ModName,
                    Window = setting.Window,
                }));
                seen.Add(setting.Key);
            }
            if (edit != null && edit.Before != null && edit.After != null)
            {
                foreach (IGraphicsModule module in OurModules.All)
                {
                    foreach (ModuleSetting setting in module.Settings)
                    {
                        string before = setting.Read(edit.Before);
                        string after = setting.Read(edit.After);
                        if (before == after) continue;
                        ApplyWindow window = setting.EffectOf != null ? setting.EffectOf(before, after) : setting.TakesEffect;
                        if (window == ApplyWindow.Live) continue;
                        string key = "redefinition." + module.Name + "." + setting.Key;
                        list.Add(new KeyValuePair<string, WaitingChange>(key, new WaitingChange
                        {
                            Title = setting.Title,
                            Owner = "ReDefinition",
                            Window = window,
                        }));
                        seen.Add(key);
                    }
                }
                // Read as a menu is built.
                if (edit.Before.ReplaceKspSettings != edit.After.ReplaceKspSettings)
                {
                    list.Add(new KeyValuePair<string, WaitingChange>(ReplaceKey, new WaitingChange
                    {
                        Title = "Replace original settings",
                        Owner = "ReDefinition",
                        Window = ApplyWindow.NextScene,
                    }));
                    seen.Add(ReplaceKey);
                }
            }
            if (!withApplied) return list;
            foreach (KeyValuePair<string, WaitingChange> pair in applied)
                if (seen.Add(pair.Key)) list.Add(pair);
            return list;
        }

        private const string ReplaceKey = "redefinition.replaceKspSettings";

        // Worked out again only when the rows have changed or a second has passed:
        // the line asks every frame.
        private static int waitingVersion = -1;
        private static float waitingAt = -10f;
        private static string waitingLine = "";
        private static string waitingTooltip = "";
        private static bool waitingRestart;

        // Whether a change, applied or not, waits for a restart.
        private static bool RestartWaiting()
        {
            RefreshWaiting();
            return waitingRestart;
        }

        private static void RefreshWaiting()
        {
            float now = UnityEngine.Time.unscaledTime;
            if (waitingVersion == model.Version && now - waitingAt < 1f) return;
            waitingVersion = model.Version;
            waitingAt = now;

            List<KeyValuePair<string, WaitingChange>> waiting = Waiting(true);
            waitingRestart = false;
            if (waiting.Count == 0)
            {
                waitingLine = "";
                waitingTooltip = "";
                return;
            }

            bool restart = false;
            foreach (KeyValuePair<string, WaitingChange> pair in waiting) restart |= pair.Value.Window == ApplyWindow.Restart;
            waitingRestart = restart;
            string line = waiting.Count == 1
                ? waiting[0].Value.Title + ": " + BundledStore.When(waiting[0].Value.Window)
                : waiting.Count + " changes take effect " + (restart ? "after a restart" : "from the next scene on");
            waitingLine = "<color=" + Yellow + ">" + line + "</color>";

            StringBuilder tooltip = new StringBuilder("Not at once:");
            for (int i = 0; i < waiting.Count && i < WaitingListed; i++)
            {
                WaitingChange change = waiting[i].Value;
                tooltip.Append('\n').Append(change.Title).Append(" (").Append(change.Owner).Append("): ")
                       .Append(BundledStore.When(change.Window));
            }
            if (waiting.Count > WaitingListed) tooltip.Append("\nand ").Append(waiting.Count - WaitingListed).Append(" more");
            waitingTooltip = TooltipText.Wrap(tooltip.ToString());
        }

        // A button without a background: KSP's labels carry no tooltip, its buttons do.
        private static DialogGUIBase WaitingNotice(float width)
        {
            DialogGUIButton notice = new DialogGUIButton(() =>
            {
                RefreshWaiting();
                return waitingLine;
            }, () => { }, width, 30f, false);
            notice.tooltipText = " ";
            notice.OnUpdate = () =>
            {
                RefreshWaiting();
                notice.tooltipText = waitingTooltip.Length > 0 ? waitingTooltip : " ";
            };
            DialogOverWindow.ClearBackground(notice);
            return notice;
        }
    }
}
