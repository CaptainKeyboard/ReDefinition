using System.IO;
using UnityEngine;
using ReDefinition.Core;

namespace ReDefinition.Probe
{
    // A flight kept as a save file at a moment a probe names, and started from again. A test of a phase of a long flight then starts in that
    // phase and does not fly the hours before it. The save is written once, the first time the route says it is doing what
    // -redefinitionProbeSaveWhen names. An empty text is the first thing it says. The save is named by -redefinitionProbeSaveAs, in the
    // probe's save folder. -redefinitionProbeSaveQuit ends the run once it is written. -redefinitionProbeScenario <name> starts from it.
    internal static class Scenario
    {
        internal const string LoadArgument = "-redefinitionProbeScenario";
        private const string WhenArgument = "-redefinitionProbeSaveWhen";
        private const string AsArgument = "-redefinitionProbeSaveAs";
        private const string QuitArgument = "-redefinitionProbeSaveQuit";

        private static bool written;

        // Called with what the route says it is doing: the save is written the first time it begins with the text asked for.
        internal static void Watch(string status)
        {
            string when = CommandLine.After(WhenArgument);
            string name = CommandLine.After(AsArgument);
            if (written || when == null || name == null || status == null || !status.StartsWith(when)) return;
            written = true;
            GamePersistence.SaveGame(name, SceneProbe.SaveFolder, SaveMode.OVERWRITE);
            Debug.Log(Log.Tag + " Probe: scenario saved: " + name + " at " + status + ".");
            if (CommandLine.Has(QuitArgument)) Application.Quit();
        }

        // The game from the save the run is to start from, or null where it names none or the save is not there.
        internal static Game Load()
        {
            string name = CommandLine.After(LoadArgument);
            if (name == null) return null;
            string file = Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "saves"), Path.Combine(SceneProbe.SaveFolder, name + ".sfs"));
            if (!File.Exists(file))
            {
                Debug.LogWarning(Log.Tag + " Probe: no scenario " + name + ".");
                return null;
            }
            return GamePersistence.LoadGame(name, SceneProbe.SaveFolder, true, false);
        }
    }
}
