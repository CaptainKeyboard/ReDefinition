using ReDefinition.Raytracing;
using UnityEngine;

namespace ReDefinition.Probe
{
    // The light probe's switches (RtCamera.ResetProbeSwitches, MoonGlow.HeldOff, the water
    // debug view) off at every scene's start.
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    internal sealed class ProbeSwitchReset : MonoBehaviour
    {
        private void Awake()
        {
            RtCamera.ResetProbeSwitches();
            MoonGlow.HeldOff = false;
            Shader.SetGlobalFloat("_ReDefinitionWaterDebug", 0f);
        }
    }
}
