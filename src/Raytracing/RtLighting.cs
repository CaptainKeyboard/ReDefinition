using ReDefinition.Core;
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
                ownShadows = null;
                ownReflections = null;
            }
            Shader currentShadows = GraphicsSettings.GetCustomShader(BuiltinShaderType.ScreenSpaceShadows);
            if (shadows != null && currentShadows != shadows)
            {
                // Another mod set its own since -- Scatterer's and Kopernicus's long-distance
                // shadows (Scatterer.cs, ShadowMan.cs), Kopernicus's on every scene --: that
                // one comes back when raytracing goes.
                if (currentShadows != null)
                {
                    shadowsBefore = currentShadows;
                    shadowsModeBefore = GraphicsSettings.GetShaderMode(BuiltinShaderType.ScreenSpaceShadows);
                }
                GraphicsSettings.SetShaderMode(BuiltinShaderType.ScreenSpaceShadows, BuiltinShaderMode.UseCustom);
                GraphicsSettings.SetCustomShader(BuiltinShaderType.ScreenSpaceShadows, shadows);
                // Remembered where it is set, not on the first call: raytracing started in
                // Shadows-off mode passes null here and sets the shader only later.
                ownShadows = shadows;
            }

            Shader current = GraphicsSettings.GetCustomShader(BuiltinShaderType.DeferredReflections);
            // The copy reads the globals Deferred sets, checked in Deferred's code
            // (ForeignAssumptions.DeferredReflections): renamed, it would light with
            // zeros where Deferred's own would not.
            if (reflections != null && current != reflections && current != null && current.name == DeferredReflectionsName
                && ForeignAssumptions.DeferredReflections.Holds)
            {
                reflectionsBefore = current;
                GraphicsSettings.SetCustomShader(BuiltinShaderType.DeferredReflections, reflections);
                // Same as above: Shadows mode passes null, Light/Full sets it later.
                ownReflections = reflections;
            }
            else if (reflections == null && current != null && current == ownReflections && reflectionsBefore != null)
            {
                // Light or Full to Shadows: Deferred's own reflections back while raytracing goes on.
                GraphicsSettings.SetCustomShader(BuiltinShaderType.DeferredReflections, reflectionsBefore);
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
