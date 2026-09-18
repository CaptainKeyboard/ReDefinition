using ReDefinition.Shared;

namespace ReDefinition.Api
{
    /// <summary>
    /// The interface ReDefinition offers other mods: the frame's state and history resets
    /// (<see cref="Frame"/>), places in the frame (<see cref="Hooks"/>), the chosen graphics profile
    /// (<see cref="Profiles"/>) and Direct3D 12 (<see cref="D3D12"/>). Every member takes and returns
    /// types of .NET, Unity and KSP only, so a mod can reach it by reflection without referencing
    /// ReDefinition (docs/modders/examples/ReDefinitionApi.cs). Call it from the main thread.
    /// Reference: docs/modders/shared-foundation.md.
    /// </summary>
    public static class ApiInfo
    {
        /// <summary>
        /// Raised with every change of the interface. A published member keeps its name and signature.
        /// </summary>
        public const int Version = SharedFrame.InterfaceVersion;
    }
}
