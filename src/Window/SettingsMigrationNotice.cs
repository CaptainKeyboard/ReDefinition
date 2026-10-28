using System.Collections;
using System.Text;
using ReDefinition.Core;
using ReDefinition.Migration;
using ReDefinition.Modules;
using ReDefinition.Ui;
using UnityEngine;

namespace ReDefinition.Window
{
    // The main menu's word, once, about what an update changed in the player's settings (SettingsMigration): all changes in one
    // list, with the words of each step. The new values are in force already. One button keeps them, the other takes the old
    // values back, each setting as it was before the update. The file as it was before stays next to settings.cfg either way.
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class SettingsMigrationNotice : MonoBehaviour
    {
        // Three seconds after the main menu opens, after the other notices: the changes waiting, if any.
        private IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(3f);

            MigrationNotice.Pending pending = MigrationNotice.Read(OwnSettings.NoticePath);
            if (pending == null) yield break;

            var message = new StringBuilder();
            message.Append("ReDefinition was updated from ").Append(pending.From).Append(" to ").Append(SettingsMigration.Current)
                   .Append(". These settings of yours changed with it, and the new values are in force:\n");
            foreach (SettingsChange change in pending.Changes)
                message.Append("\n- ").Append(change.Text);
            message.Append("\n\nThe file as it was is kept as settings.cfg.bak-").Append(pending.From).Append('.');

            MultiOptionDialog dialog = new MultiOptionDialog("ReDefinitionSettingsMigration", message.ToString(),
                "ReDefinition's settings changed", HighLogic.UISkin, 520f,
                new DialogGUIHorizontalLayout(0f, 30f, 8f, new RectOffset(), TextAnchor.MiddleCenter,
                    new DialogGUIButton("Keep the new values", () => MigrationNotice.Clear(OwnSettings.NoticePath), 220f, 30f, true),
                    new DialogGUIButton("Take my old values back", () => TakeBack(pending), 220f, 30f, true)));
            UnityMouseEvents.Shield(ScreenFit.Add(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                dialog, false, HighLogic.UISkin)));
            Debug.Log(Log.Tag + " Main menu notice about " + pending.Changes.Count + " settings changed by the update from " + pending.From + ".");
        }

        // Each changed setting back to its value from before the update, through the add-on's own apply, and saved.
        private static void TakeBack(MigrationNotice.Pending pending)
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon != null)
            {
                OwnSettings before = addon.Live.Clone();
                OwnSettings after = addon.Live.Clone();
                foreach (SettingsChange change in pending.Changes)
                    if (change.Before != null) after.SetValue(change.Key, change.Before);
                addon.Apply(before, after);
                addon.Live.Save();
            }
            MigrationNotice.Clear(OwnSettings.NoticePath);
            Debug.Log(Log.Tag + " Settings: the values from before the update to " + SettingsMigration.Current + " are back.");
        }
    }
}
