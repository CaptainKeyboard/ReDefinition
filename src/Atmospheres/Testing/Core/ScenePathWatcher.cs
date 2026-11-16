using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Which way a series start reached its flight, from the scenes the game was asked to load: through the space centre (as a player's flight is reached) or straight from the main
    // menu. The KSC ground renders differently on the two ways (green through the space centre, grey-blue straight in: tools/vanilla_ground.py, 2026-11-16), so every series log
    // says which one a start took ('scene path: space centre' or 'scene path: direct') and the ground probe judges it. Only with -atmoSeries on the command line.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    internal sealed class ScenePathWatcher : MonoBehaviour
    {
        internal const string SpaceCentre = "space centre", Direct = "direct", Unknown = "unknown";

        private static bool spaceCentreSeen;
        private static string path;

        private void Awake()
        {
            if (!CommandLine.Has("-atmoSeries", true))
            {
                Destroy(this);
                return;
            }
            DontDestroyOnLoad(gameObject);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
        }

        private void OnDestroy()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
        }

        private static void OnSceneLoadRequested(GameScenes scene)
        {
            if (scene == GameScenes.SPACECENTER) spaceCentreSeen = true;
            if (scene == GameScenes.FLIGHT && path == null) Record();
        }

        private static void Record()
        {
            path = spaceCentreSeen ? SpaceCentre : Direct;
            AtmospheresLog.Say("scene path: " + path + " (the first flight of this run was reached " + (spaceCentreSeen ? "through the space centre" : "straight from the main menu") + ")");
        }

        // The way the run's first flight was reached; Unknown before any flight was asked for.
        internal static string Current
        {
            get { return path ?? Unknown; }
        }
    }
}
