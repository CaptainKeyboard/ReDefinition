using System;

namespace ReDefinition.Atmospheres
{
    // Which part meshes get a cover shell, and which shaders the shell and the ray-traced scene leave alone. Pure (names in, answers
    // out), so the rules are tested outside the game. Package D34, review findings R1 and R3.
    //
    // The shell lights itself (Lambert, Unity's lights). RECON P8 measured no foreign opaque shader within 10 % of the stock material
    // in the frame, so a shell on a part with a foreign shader (Parallax, MechJeb, RPM, Shader Forge) would sit on it with another
    // brightness. The rule: a shell only over the shaders listed here; the other parts stay bare in the Shell method and are counted
    // and logged. The method ScreenSpace has no such limit. [VERIFY the stock and Deferred shader names in the game: the probe logs
    // the shader names of the bare parts]
    internal static class VehicleCoverGate
    {
        // The name prefixes of the shaders a shell is built over: KSP's own part shaders, Unity's standard and legacy shaders and the
        // Deferred mod's replacements of them.
        private static readonly string[] AcceptedPrefixes = { "KSP/", "Standard", "Legacy Shaders/", "Deferred/", "Diffuse", "Bumped", "Specular" };

        // The name of the shell's own shader: the ray-traced scene leaves it out (vehicles are bare in reflections).
        internal const string ShellShaderName = "ReDefinition/AtmospheresShell";

        // Whether a shell is built over a mesh drawn with this shader. A shader without a name is treated as foreign.
        internal static bool ShellAccepts(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return false;
            for (int i = 0; i < AcceptedPrefixes.Length; i++)
                if (shaderName.StartsWith(AcceptedPrefixes[i], StringComparison.Ordinal)) return true;
            return false;
        }

        // Whether the ray-traced scene leaves a mesh with this shader out: the cover shell is a thin skin over a part that is already in
        // the scene, and the rays must not see a second surface two centimetres above it.
        internal static bool RayTracingLeavesOut(string shaderName)
        {
            return shaderName == ShellShaderName;
        }
    }
}
