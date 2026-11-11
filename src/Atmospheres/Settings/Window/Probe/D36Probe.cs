using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The part of series K-W3a that belongs to the settings window (rows in tests/series/rows/D36.json; the series owns the start, this has
    // none): opens the window in the Space Centre with every group unfolded, reads the DialogGUI checklist from the dialog that stands
    // (SettingsWindowChecks) with a screenshot per item, and counts the rows the window greys out with the master switch off. Active only
    // with -atmoSeries K-W3a on the command line.
    // Several packages put a probe on this series; each ends with its own verdict line, and the first to finish starts the quit timer of
    // the game [VERIFY with A02 how the packages of one series share the end of the start].
    [AtmospheresModule(96)]
    internal sealed class D36Probe : SeriesProbe
    {
        private int shots;

        protected override string SeriesName
        {
            get { return "K-W3a"; }
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            yield return ProbeWait.Until(() => HighLogic.LoadedScene == GameScenes.SPACECENTER, 300, "the space centre");
            yield return ProbeWait.Seconds(3);

            var window = new SettingsWindow(WindowModel.ForGame());
            foreach (string category in window.Model.Categories()) SettingsWindow.SetOpen(category, true);
            SettingsWindow.SetOpen("Bodies", true);
            string before = SettingsDump();
            window.Show();
            yield return ProbeWait.Until(() => window.IsOpen && window.Root != null, 10, "the settings window");
            yield return ProbeWait.Frames(45);

            List<WindowCheck> checks = SettingsWindowChecks.Run(window.Root, window.Model, Shoot, SettingsDump() == before);
            yield return ProbeWait.Frames(5);
            foreach (WindowCheck check in checks)
            {
                Check("D36.dialog." + check.Name, check.Ok, check.Detail);
                Emit("D36.dialog." + check.Name, check.Ok);
                Emit("D36.dialog." + check.Name + ".shot", check.Shot);
            }

            // The rows the window greys out, here with the master switch off as the game starts: every row outside General and none in it.
            int greyed = 0, general = 0, generalGreyed = 0, withoutReason = 0;
            foreach (string category in window.Model.Categories())
            {
                foreach (SettingDef def in window.Model.Rows(category))
                {
                    RowState state = window.Model.State(def);
                    bool isGeneral = category == AtmospheresSettings.Categories.General;
                    if (isGeneral) general++;
                    if (state.Enabled) continue;
                    if (isGeneral) generalGreyed++;
                    else greyed++;
                    if (state.Reason.Length == 0 || state.Reason == state.ReasonId) withoutReason++;
                }
            }
            bool masterOff = !AtmospheresSettings.WeatherEffects;
            int outside = 0;
            foreach (SettingDef def in AtmospheresSettings.Definitions)
            {
                if (def.Category != AtmospheresSettings.Categories.General) outside++;
            }
            // With the master switch off every row outside General is greyed: the number missing (zero when the switch is on).
            Emit("D36.grey_rows_missing_outside_general", masterOff ? outside - greyed : 0);
            Emit("D36.grey_rows_outside_general", greyed);
            Emit("D36.grey_rows_without_reason_text", withoutReason);
            Emit("D36.grey_rows_in_general", generalGreyed);
            Emit("D36.master_off_at_start", masterOff);
            Emit("D36.rows_total", general + greyed + generalGreyed);

            WindowEnvironment env = window.Model.Env;
            Emit("D36.env.eve", env.EveInstalled);
            Emit("D36.env.main_mod", env.MainModPresent);
            EmitText("D36.tier_line", window.Model.TierLine());
            EmitText("D36.wind_line", window.Model.WindLine());
            EmitText("D36.tier_effective", QualityProfile.Effective().ToString());

            window.Close();
            yield return ProbeWait.Frames(5);
            Check("D36.window_closed", !window.IsOpen);
        }

        // All settings as text, to see that the window changed none by itself.
        private static string SettingsDump()
        {
            return AtmospheresSettings.Snapshot().ToString();
        }

        // A screenshot of the screen, numbered in order.
        private int Shoot(string item)
        {
            shots++;
            string folder = Path.Combine(KSPUtil.ApplicationRootPath, "Screenshots");
            Directory.CreateDirectory(folder);
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "atmo-D36-" + shots.ToString("00") + "-" + item + ".png"));
            return shots;
        }
    }
}
