using System.Collections.Generic;
using System.Text;
using ReDefinition.Settings;

namespace ReDefinition.Window
{
    // Changes that do not take effect at once, said between Reset and Apply in
    // yellow: rows changed and not applied yet, and rows applied that wait for
    // the next scene or a restart. The line is short; its tooltip names each
    // setting and when it arrives.
    internal static partial class SettingsWindow
    {
        private const int WaitingListed = 8;
        private const string Yellow = "#f5d800";

        // Applied, and waiting: by key, with when it takes effect. The next
        // scene's go with the next scene; a restart's stay for the run.
        private static readonly Dictionary<string, BundledSetting> applied = new Dictionary<string, BundledSetting>();

        // Before Apply sets the rows: what it sets that does not take effect at once.
        private static void NoteWaiting()
        {
            foreach (BundledSetting setting in InstalledSettings())
            {
                if (setting.Window == ApplyWindow.Live || !model.Changed(setting.Key)) continue;
                applied[setting.Key] = setting;
            }
        }

        // From ModWindowsAddon once a scene is ready.
        internal static void SceneChanged()
        {
            List<string> arrived = new List<string>();
            foreach (KeyValuePair<string, BundledSetting> pair in applied)
                if (pair.Value.Window != ApplyWindow.Restart) arrived.Add(pair.Key);
            foreach (string key in arrived) applied.Remove(key);
        }

        // Everything waiting now: the rows changed in the window, then those applied.
        private static List<BundledSetting> Waiting()
        {
            List<BundledSetting> list = new List<BundledSetting>();
            HashSet<string> seen = new HashSet<string>();
            foreach (BundledSetting setting in InstalledSettings())
            {
                if (setting.Window == ApplyWindow.Live || !model.Changed(setting.Key)) continue;
                list.Add(setting);
                seen.Add(setting.Key);
            }
            foreach (BundledSetting setting in applied.Values)
                if (seen.Add(setting.Key)) list.Add(setting);
            return list;
        }

        // Worked out again only when the rows have changed or a second has passed:
        // the line asks every frame.
        private static int waitingVersion = -1;
        private static float waitingAt = -10f;
        private static string waitingLine = "";
        private static string waitingTooltip = "";

        private static void RefreshWaiting()
        {
            float now = UnityEngine.Time.unscaledTime;
            if (waitingVersion == model.Version && now - waitingAt < 1f) return;
            waitingVersion = model.Version;
            waitingAt = now;

            List<BundledSetting> waiting = Waiting();
            if (waiting.Count == 0)
            {
                waitingLine = "";
                waitingTooltip = "";
                return;
            }

            bool restart = false;
            foreach (BundledSetting setting in waiting) restart |= setting.Window == ApplyWindow.Restart;
            string line = waiting.Count == 1
                ? waiting[0].Title + ": " + BundledStore.When(waiting[0].Window)
                : waiting.Count + " changes take effect " + (restart ? "after a restart" : "from the next scene on");
            waitingLine = "<color=" + Yellow + ">" + line + "</color>";

            StringBuilder tooltip = new StringBuilder("Not at once:");
            for (int i = 0; i < waiting.Count && i < WaitingListed; i++)
                tooltip.Append('\n').Append(waiting[i].Title).Append(" (").Append(waiting[i].Owner.ModName).Append("): ")
                       .Append(BundledStore.When(waiting[i].Window));
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
            // KSP's own ClearButtonImage is internal; the field it sets, before the
            // button is built, hides the background (DialogGUIButton.Create).
            System.Reflection.FieldInfo clear = typeof(DialogGUIButton).GetField("clearButtonImage",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (clear != null) clear.SetValue(notice, true);
            return notice;
        }
    }
}
