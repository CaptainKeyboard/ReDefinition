using System.Diagnostics;
using System.IO;
using System.Text;
using System;
using ReDefinition.Core;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ReDefinition.Window
{
    // KSP started anew, for settings that take effect only at a start. Where a
    // game is loaded it is saved first, as KSP saves it: "persistent", from the
    // game as it stands (GamePersistence.SaveGame with HighLogic.CurrentGame.
    // Updated(), as FlightDriver does after a launch). In flight only where KSP
    // allows a save now (FlightGlobals.ClearToSave); in the editors the vessel
    // being built goes into KSP's auto-saved ship first, as a launch saves it
    // (EditorLogic, decompiled).
    //
    // The new KSP is started by a hidden PowerShell that waits for this process
    // to end, so the two never run at once and the new one reads what this one
    // wrote on quitting: the same executable, folder and arguments. Windows only,
    // which is where the proxy runs.
    internal static class GameRestart
    {
        internal static bool Available
        {
            get { return Application.platform == RuntimePlatform.WindowsPlayer; }
        }

        // Whether a game is loaded that the restart saves first.
        internal static bool SavesGame
        {
            get { return HighLogic.CurrentGame != null && Loaded(HighLogic.LoadedScene); }
        }

        // Saves where a game is loaded, then restarts; says why on screen where it
        // cannot.
        internal static void SaveAndRestart()
        {
            string blocked;
            if (!Save(out blocked))
            {
                ScreenMessages.PostScreenMessage("ReDefinition: no restart -- " + blocked, 6f);
                Debug.LogWarning(Log.Tag + " Restart not done: " + blocked);
                return;
            }
            try
            {
                StartAfterExit();
            }
            catch (Exception e)
            {
                ScreenMessages.PostScreenMessage("ReDefinition: KSP could not be started again -- restart it yourself.",
                    6f);
                Debug.LogWarning(Log.Tag + " The new KSP could not be started: " + e);
                return;
            }
            Debug.Log(Log.Tag + " KSP quits and starts again for settings that take effect at a start.");
            Application.Quit();
        }

        private static bool Loaded(GameScenes scene)
        {
            return scene == GameScenes.FLIGHT || scene == GameScenes.EDITOR || scene == GameScenes.SPACECENTER
                   || scene == GameScenes.TRACKSTATION;
        }

        private static bool Save(out string blocked)
        {
            blocked = null;
            if (!SavesGame) return true;
            if (HighLogic.LoadedSceneIsFlight)
            {
                ClearToSaveStatus status = FlightGlobals.ClearToSave();
                if (status != ClearToSaveStatus.CLEAR)
                {
                    blocked = FlightGlobals.GetNotClearToSaveStatusReason(status, "save");
                    return false;
                }
            }
            if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null && EditorLogic.fetch.ship != null
                && EditorLogic.fetch.ship.parts.Count > 0)
            {
                string ship = ShipConstruction.SaveShip(KSPUtil.SanitizeString(EditorLogic.autoShipName, '_', true));
                Debug.Log(Log.Tag + " The vessel in the editor saved as " + ship + " before the restart.");
            }
            string saved = GamePersistence.SaveGame(HighLogic.CurrentGame.Updated(), "persistent", HighLogic.SaveFolder,
                SaveMode.OVERWRITE);
            if (string.IsNullOrEmpty(saved))
            {
                blocked = "the game could not be saved.";
                return false;
            }
            Debug.Log(Log.Tag + " Game saved (" + HighLogic.SaveFolder + "/persistent) before the restart.");
            return true;
        }

        private static void StartAfterExit()
        {
            Process self = Process.GetCurrentProcess();
            string exe = self.MainModule.FileName;
            string folder = Path.GetDirectoryName(exe);
            string[] args = Environment.GetCommandLineArgs();

            StringBuilder command = new StringBuilder();
            command.Append("Wait-Process -Id ").Append(self.Id).Append(" -ErrorAction SilentlyContinue; ");
            command.Append("Start-Process -FilePath ").Append(Quoted(exe)).Append(" -WorkingDirectory ").Append(Quoted(folder));
            if (args.Length > 1)
            {
                command.Append(" -ArgumentList ");
                for (int i = 1; i < args.Length; i++)
                {
                    if (i > 1) command.Append(',');
                    command.Append(Quoted(args[i]));
                }
            }

            ProcessStartInfo start = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + command.ToString().Replace("\"", "\\\"") + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = folder,
            };
            Process.Start(start);
        }

        // PowerShell's single quotes: nothing inside is expanded, a quote is doubled.
        private static string Quoted(string text)
        {
            return "'" + text.Replace("'", "''") + "'";
        }
    }
}
