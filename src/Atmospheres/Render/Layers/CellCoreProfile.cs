using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The single cloud's core profile (CloudCells.cginc CellBody, the global _CellK): the body's share rises from cloud.cellCoreFloor at its surface to 1 at the depth cloud.cellCoreDepth of its shorter
    // semi-axis, instead of being uniform right below the surface band. A uniform body with a crisp rim draws hard self-shadow zones in the sun's light (the contour bands of master plan 32 and
    // PRUEFPROTOKOLL 77); the values are a look choice judged against the reference photos (.local/work/real-clouds), so they are keys until the owner has picked them.
    internal static class CellCoreProfile
    {
        internal const string TuneDepth = "cloud.cellCoreDepth", TuneFloor = "cloud.cellCoreFloor";
        // 0: off, the body as before.
        internal const float DepthDefault = 0f, FloorDefault = 1f;

        private static readonly int IdK = Shader.PropertyToID("_CellK");

        internal static float Depth { get { return Mathf.Clamp(Tuning.Get(TuneDepth, DepthDefault), 0f, 1f); } }
        internal static float Floor { get { return Mathf.Clamp01(Tuning.Get(TuneFloor, FloorDefault)); } }

        // Writes the global for this frame (the layer pass and the cloud shadow read it).
        internal static void Apply()
        {
            Shader.SetGlobalVector(IdK, new Vector4(Depth, Floor, 0f, 0f));
        }
    }
}
