using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Raytracing
{
    // ReDefinition's two lighting shaders in Unity's place while raytracing is on
    // (docs/development/raytracing.md, "Where the raytraced light enters Unity's
    // lighting"): the screen-space shadows, and, where Deferred has set its own,
    // the deferred reflections. What was set before is kept and set again when
    // raytracing goes, or when another shader takes the place meanwhile.
    internal static class RtLighting
    {
        internal const string DeferredReflectionsName = "Deferred/Internal-DeferredReflections";

        private static bool installed;
        private static BuiltinShaderMode shadowsModeBefore;
        private static Shader shadowsBefore;
        private static Shader reflectionsBefore;
        private static Shader ownShadows;
        private static Shader ownReflections;

        internal static bool ReflectionsReplaced { get; private set; }

        // Every frame while raytracing runs: the shaders in place, again where Deferred
        // or another mod has set its own since. The reflections only over Deferred's:
        // without it there is no ambient pass of Deferred's to take the place of.
        internal static void Keep(Shader shadows, Shader reflections)
        {
            if (!installed)
            {
                installed = true;
                shadowsModeBefore = GraphicsSettings.GetShaderMode(BuiltinShaderType.ScreenSpaceShadows);
                shadowsBefore = GraphicsSettings.GetCustomShader(BuiltinShaderType.ScreenSpaceShadows);
                ownShadows = shadows;
                ownReflections = reflections;
            }
            if (shadows != null && GraphicsSettings.GetCustomShader(BuiltinShaderType.ScreenSpaceShadows) != shadows)
            {
                GraphicsSettings.SetShaderMode(BuiltinShaderType.ScreenSpaceShadows, BuiltinShaderMode.UseCustom);
                GraphicsSettings.SetCustomShader(BuiltinShaderType.ScreenSpaceShadows, shadows);
            }

            Shader current = GraphicsSettings.GetCustomShader(BuiltinShaderType.DeferredReflections);
            if (reflections != null && current != reflections && current != null && current.name == DeferredReflectionsName)
            {
                reflectionsBefore = current;
                GraphicsSettings.SetCustomShader(BuiltinShaderType.DeferredReflections, reflections);
            }
            ReflectionsReplaced = reflections != null
                                  && GraphicsSettings.GetCustomShader(BuiltinShaderType.DeferredReflections) == reflections;
        }

        // What was there before, back.
        internal static void Restore()
        {
            if (!installed) return;
            installed = false;
            if (GraphicsSettings.GetCustomShader(BuiltinShaderType.ScreenSpaceShadows) == ownShadows)
            {
                GraphicsSettings.SetCustomShader(BuiltinShaderType.ScreenSpaceShadows, shadowsBefore);
                GraphicsSettings.SetShaderMode(BuiltinShaderType.ScreenSpaceShadows, shadowsModeBefore);
            }
            if (reflectionsBefore != null
                && GraphicsSettings.GetCustomShader(BuiltinShaderType.DeferredReflections) == ownReflections)
                GraphicsSettings.SetCustomShader(BuiltinShaderType.DeferredReflections, reflectionsBefore);
            reflectionsBefore = null;
            ReflectionsReplaced = false;
        }
    }
}
