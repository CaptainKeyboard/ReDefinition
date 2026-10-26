// The render events the mod raises on Unity's render thread for the proxy (ManagedBridge.cpp,
// DispatchRenderEvent): their ids, in one place. The managed side has them as ProxyEvents
// (src/Proxy/ProxyEvents.cs); tests/ReDefinition.Tests (ProxyContractTests) reads this file and holds
// ProxyEvents to it, and the harness raises the events by these names. The raytracing's own are in
// Raytracing/Shaders/RtContract.h. PX_EVENT_<NAME> is ProxyEvents' constant <Name>.

#ifndef REDEFINITION_PROXY_CONTRACT
#define REDEFINITION_PROXY_CONTRACT

// Frame generation's packet of this frame; DLSS's frame, its release, and its question for render sizes.
#define PX_EVENT_FRAME_PACKET 1
#define PX_EVENT_DLSS_FRAME 2
#define PX_EVENT_DLSS_RELEASE 3
#define PX_EVENT_DLSS_SIZE_QUERY 6

// AMD's upscaler: a frame, and the release of its context.
#define PX_EVENT_AMD_FRAME 4
#define PX_EVENT_AMD_RELEASE 5

// XeSS: a frame, and the release of its context.
#define PX_EVENT_XESS_FRAME 7
#define PX_EVENT_XESS_RELEASE 8

// Direct3D 12 for mods: a compute pass made, destroyed and dispatched, and a texture's shared copy released.
#define PX_EVENT_D3D12_CREATE_PASS 20
#define PX_EVENT_D3D12_DESTROY_PASS 21
#define PX_EVENT_D3D12_DISPATCH 22
#define PX_EVENT_D3D12_RELEASE_TEXTURE 23

// The game's frame index, issued before the frame's first Direct3D call, for Reflex.
#define PX_EVENT_REFLEX_FRAME 40

#endif
