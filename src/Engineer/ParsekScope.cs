using System.Collections;
using ReDefinition.Autopilot.Flight;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // At the start of a scene where Parsek puts vessels back, the recordings made for routes are told to Parsek's replay scope again, since the
    // scope is kept in memory only (ParsekRecording.KeepInScope).
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    internal sealed class ParsekScope : MonoBehaviour
    {
        private IEnumerator Start()
        {
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER && HighLogic.LoadedScene != GameScenes.TRACKSTATION && HighLogic.LoadedScene != GameScenes.FLIGHT) yield break;
            yield return new WaitForSeconds(1f);
            ParsekRecording.KeepInScope();
        }
    }
}
