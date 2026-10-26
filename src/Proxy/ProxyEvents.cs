namespace ReDefinition.Proxy
{
    // The ids of the render events the mod raises on Unity's render thread for the proxy (the proxy's
    // ManagedBridge.cpp, DispatchRenderEvent): a frame for frame generation, a frame and a release for each
    // upscaler, the Direct3D 12 compute for mods, and the game's frame index for Reflex. The raytracing's own are
    // RtNative's. Each is PX_EVENT_<NAME> in src/DxgiProxy/Managed/ProxyContract.h, which ProxyContractTests holds
    // these to.
    internal static class ProxyEvents
    {
        // Frame generation's packet of this frame; DLSS's frame, its release, and its question for render sizes.
        internal const int FramePacket = 1;
        internal const int DlssFrame = 2;
        internal const int DlssRelease = 3;
        internal const int DlssSizeQuery = 6;

        // AMD's upscaler: a frame, and the release of its context.
        internal const int AmdFrame = 4;
        internal const int AmdRelease = 5;

        // XeSS: a frame, and the release of its context.
        internal const int XessFrame = 7;
        internal const int XessRelease = 8;

        // Direct3D 12 for mods: a compute pass made, destroyed and dispatched, and a texture's shared copy released.
        internal const int D3d12CreatePass = 20;
        internal const int D3d12DestroyPass = 21;
        internal const int D3d12Dispatch = 22;
        internal const int D3d12ReleaseTexture = 23;

        // The game's frame index, issued before the frame's first Direct3D call, for Reflex.
        internal const int ReflexFrame = 40;
    }
}
