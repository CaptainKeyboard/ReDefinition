using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The debug overlay switch atmoDebug as the shaders see it: the global _AtmoDebug of unity/Assets/ReDefinitionAtmospheres/Include/
    // AtmoContribution.cginc (0 Off, 1 Contrib, 2 Level, 3 Maps). A host calls Apply once per frame, before the cameras render; the
    // value is the number of the setting's enum member. The setter is replaceable so that a test sees the value without Unity.
    internal static class AtmoDebug
    {
        internal const string GlobalName = "_AtmoDebug";

        internal static Action<string, float> SetGlobal = (name, value) => Shader.SetGlobalFloat(name, value);

        private static float last = -1f;

        // The shader value of a mode.
        internal static float ValueOf(AtmoDebugMode mode)
        {
            return (float)(int)mode;
        }

        // Writes the global when the setting changed since the last call (or on the first call).
        internal static void Apply(AtmoDebugMode mode)
        {
            float v = ValueOf(mode);
            if (v == last) return;
            last = v;
            SetGlobal(GlobalName, v);
        }

        // Forgets the last value, so that the next Apply writes again (a scene change, a test).
        internal static void Reset()
        {
            last = -1f;
        }
    }
}
