# ReDefinition's interface for mods

What a KSP game has only once -- the camera's jitter, the frame's motion vectors, camera
cuts, the floating origin's shifts, the image after upscaling, Direct3D 12 -- ReDefinition
offers every mod, in `ReDefinition.Api`. The design behind it:
[development/shared-foundation.md](../development/shared-foundation.md).

## Reaching it

| Way | For | How |
|---|---|---|
| The shader include | a shader that reads the frame's state | copy [`unity/Assets/ReDefinition/Include/ReDefinition.cginc`](../../unity/Assets/ReDefinition/Include/ReDefinition.cginc) into the shader project and `#include "ReDefinition.cginc"`; without ReDefinition every function returns what the game has without it. MIT |
| The wrapper | a mod that runs with or without ReDefinition | copy [examples/ReDefinitionApi.cs](examples/ReDefinitionApi.cs) into the mod and put it in the mod's namespace; `ReDefinitionApi.Installed` says whether ReDefinition is there. MIT |
| A reference | a mod that requires ReDefinition | reference `GameData/ReDefinition/Plugins/ReDefinition.dll`, declare `[assembly: KSPAssemblyDependency("ReDefinition", 0, 1)]`, and use `ReDefinition.Api`; `ReDefinition.xml` beside the DLL documents every member in the IDE |

Every member is called from the main thread. `ApiInfo.Version` counts changes of the
interface; the wrapper returns what the game has without ReDefinition while the installed
interface is older than the one it was written for. It binds each member once as a typed
delegate: a call costs what a direct call costs, without allocating.

**An example mod** uses each part once, through the wrapper:
[examples/ReDefinitionExample](examples/ReDefinitionExample) -- a line from the vessel as an
overlay, a compute pass that darkens the upscaled image, a reported camera jump, and two
settings of its own in ReDefinition's window, which it reads and saves itself
(`SettingsBridge.cs`, [registering-a-mod.md](registering-a-mod.md)). It is built with
ReDefinition's solution;
copy `ReDefinitionExample.dll` and `ExampleEffect.hlsl` from
`build/examples/ReDefinitionExample` into `GameData/ReDefinitionExample` to try it.

## The frame

Decided once per frame, before the first camera of the 3D stack culls; read it from
rendering code -- camera callbacks, command buffers, `OnRenderImage`.

| `Frame.` | Shader global | Holds |
|---|---|---|
| `UpscalerActive`, `FrameGenerationActive` | `_ReDefinition_Frame.x`, `.y` | whether ReDefinition's upscaler reconstructs this frame; whether frame generation receives it |
| `Technique` | | `"FSR 3"`, `"DLSS"` or `"AMD FSR (DLL)"` while the upscaler is active |
| `HistoryReset`, `HistoryResetReason` | `_ReDefinition_Frame.z` | whether temporal history is to be dropped this frame, and why |
| | `_ReDefinition_Frame.w` | the interface version |
| `RenderSize`, `DisplaySize` | `_ReDefinition_RenderSize`, `_ReDefinition_DisplaySize` | width, height, 1/width, 1/height of the 3D cameras' target and of the image shown; the screen without the upscaler |
| `Jitter`, `JitterNdc` | `_ReDefinition_Jitter` | the jitter the 3D cameras render with, in pixels at render size (`.xy`) and in normalized device coordinates (`.zw`); zero without it |
| `OriginShift`, `BodyShift`, `OriginShifted` | `_ReDefinition_OriginShift` (`OriginShift` and 1 if it shifted) | the floating origin's shifts since the frame before, summed. KSP moves the active vessel, the camera and nearby objects by `-OriginShift`, bodies and landed or packed vessels by `-BodyShift` |

**The jitter.** ReDefinition adds it to every camera of the 3D stack in `OnPreRender`, and
keeps the projection without it in `Camera.nonJitteredProjectionMatrix`. A mod that
raymarches or reprojects with its own matrices takes that one for history and motion, and
the camera's `projectionMatrix` for the rays.

**A cut of the mod's own.** A camera mod that switches or jumps the view:

```csharp
ReDefinitionApi.RequestHistoryReset("MyCameraMod switched to " + camera.name);
```

Before the frame's first 3D camera culls it reaches every mod in that frame; later, the
upscaler and frame generation in that frame and the mods in the next. A mod with temporal
history of its own drops it when told:

```csharp
ReDefinitionApi.RegisterHistoryReset(reason => myHistoryValid = false);
```

## In shaders

`ReDefinition.cginc` declares the globals above and includes `UnityCG.cginc`. Without
ReDefinition the globals are zero, and the functions fall back: the sizes to
`_ScreenParams`, the rest to no upscaler, no jitter, no reset, no shift.

| Function | Returns |
|---|---|
| `ReDefinitionInstalled()` | whether ReDefinition sets the globals |
| `ReDefinitionUpscalerActive()`, `ReDefinitionFrameGenerationActive()`, `ReDefinitionHistoryReset()` | the frame's state |
| `ReDefinitionRenderSize()`, `ReDefinitionDisplaySize()` | width, height, 1/width, 1/height -- never zero |
| `ReDefinitionJitterPixels()`, `ReDefinitionJitterNdc()` | the jitter |
| `ReDefinitionRemoveJitter(clip)` | a clip-space position from the camera's own matrices (`UnityObjectToClipPos`) without the jitter, `y` flipped as `_ProjectionParams.x` says |
| `ReDefinitionOriginShift()`, `ReDefinitionOriginShifted()` | the floating origin's shift |
| `ReDefinitionMotionVector(clipCurrent, clipPrevious)` | a motion vector in Unity's encoding, from this frame's and the frame before's clip-space position, both without jitter and with `y` up |

For a vertex shader that moves geometry -- waves, wind -- the motion vector pass computes
the position twice, with this frame's and the frame before's parameters:

```hlsl
#include "ReDefinition.cginc"

float4x4 _MyNonJitteredVP;       // GL.GetGPUProjectionMatrix(camera.nonJitteredProjectionMatrix, false) * camera.worldToCameraMatrix
float4x4 _MyPreviousVP;          // the same, kept from the frame before
float _MyTime, _MyPreviousTime;
// Wave(vertex, time): the mod's own displacement.

struct v2f { float4 pos : SV_POSITION; float4 current : TEXCOORD0; float4 previous : TEXCOORD1; };

v2f vert(appdata_base v)
{
    float4 worldNow = mul(unity_ObjectToWorld, Wave(v.vertex, _MyTime));
    float4 worldBefore = mul(unity_ObjectToWorld, Wave(v.vertex, _MyPreviousTime));
    v2f o;
    o.pos = UnityWorldToClipPos(worldNow.xyz);
    o.current = mul(_MyNonJitteredVP, worldNow);
    o.previous = mul(_MyPreviousVP, worldBefore);
    return o;
}

half4 frag(v2f i) : SV_Target
{
    return ReDefinitionMotionVector(i.current, i.previous);
}
```

The include is compiled with every function called by Unity 2019.4.18f1, the version KSP
1.12.5 is built with (`IncludeCheck` in ReDefinition's Unity project).

## Hooks

While ReDefinition's upscaler or frame generation runs. A handler that throws is removed
and logged once; the others run on.

### Motion vectors

For geometry a vertex shader moves and content that writes no depth, which Unity's motion
vectors miss. The handler is called as the scene camera culls, with the frame's state
decided, and adds commands to that camera's capture, which runs at its
`BeforeImageEffects` after Unity's motion vectors are written:

```csharp
ReDefinitionApi.RegisterMotionVectors((buffer, motionVectors, depth, scene) =>
{
    buffer.SetRenderTarget(motionVectors);
    buffer.SetGlobalTexture("_MyModSceneDepth", depth);
    foreach (Renderer renderer in myAnimatedRenderers)
        buffer.DrawRenderer(renderer, myMotionVectorMaterial);
});
```

* `motionVectors`: `RGHalf` at render size, in Unity's encoding -- the current minus the
  previous viewport position, viewport coordinates running from 0 to 1, with `y` flipped
  where `UNITY_UV_STARTS_AT_TOP` (Unity's `Internal-MotionVectors.shader`).
  `ReDefinitionMotionVector` in the include writes it.
* Both positions without jitter and with `y` up, as Unity's `_NonJitteredVP` and
  `_PreviousVP` are: through `GL.GetGPUProjectionMatrix(scene.nonJitteredProjectionMatrix, false)`
  and `scene.worldToCameraMatrix`, this frame's and the one the mod kept from the frame
  before. The motion vector pass flips `y` itself.
* `depth`: the scene's raw depth at render size (`RFloat`), reversed on Direct3D 11 -- 1
  near, 0 far -- to test against.
* Every upscaler and frame generation read the result.

### After upscaling

An effect on the upscaled image, at display size, before TUFX's effects after the upscaler
and before frame generation's copy of the scene; interpolated with the scene:

```csharp
ReDefinitionApi.RegisterAfterUpscaling((buffer, image, scene) =>
{
    buffer.GetTemporaryRT(tempId, image.width, image.height, 0, FilterMode.Bilinear, image.format);
    buffer.Blit(image, tempId, myEffectMaterial);
    buffer.Blit(tempId, image);
    buffer.ReleaseTemporaryRT(tempId);
});
```

The buffer is executed at once from `OnRenderImage`. `image` is HDR while the upscaler
runs. A Direct3D 12 compute pass recorded into this buffer with `DispatchInto` reads the
upscaled image -- the example mod does that.

### Overlays

Lines and markers in the world, drawn after the scene at display size: not upscaled, and
UI to frame generation, which does not interpolate them. The handler fills the buffer of a
camera behind every camera that draws the scene, as that camera culls; the buffer draws
onto the backbuffer at its `AfterEverything`, which holds no scene depth:

```csharp
ReDefinitionApi.RegisterOverlay((buffer, scene) =>
{
    buffer.SetViewProjectionMatrices(scene.worldToCameraMatrix, scene.nonJitteredProjectionMatrix);
    buffer.DrawMesh(myTrajectoryMesh, Matrix4x4.identity, myLineMaterial);
});
```

The camera exists only while a handler is registered.

## Profiles

`Profiles.Current` is the graphics profile chosen in ReDefinition's window -- `low`,
`medium`, `high`, `ultra`, `max` -- or null with none, when the upscaler and frame
generation are off. `Profiles.RegisterChanged(Action<string>)` is called with the new
name when it changes.

For a mod's settings in ReDefinition's window and profiles:
[registering-a-mod.md](registering-a-mod.md).

## Key bindings

A binding declared in your registration (`KEY`, [registering-a-mod.md](registering-a-mod.md))
is the player's to set in ReDefinition's *Keys* tab. Where your mod keeps no key of its
own, it asks here:

| Member | Answers |
|---|---|
| `Keys.Binding(key)` | the binding as text -- `LeftAlt+F10`, `F11`, `None` -- or null where no binding of that key is registered |
| `Keys.Pressed(key)` | the key went down this frame, with exactly the binding's modifiers |
| `Keys.Held(key)` | it is held |
| `Keys.Released(key)` | it went up this frame |

`key` is your mod's id in its registration, a dot, and the `KEY` block's name:
`mymod.window`. Another modifier held means another binding is meant, so `F10` does not
answer while `Alt+F10` is pressed. While the player is setting a binding in the window,
none of them answers.

## Direct3D 12

Compute passes on the Direct3D 12 device of ReDefinition's `dxgi.dll`, on Unity's
textures. Unity keeps rendering in Direct3D 11: each dispatch copies its textures into
textures both devices share, runs the pass on the Direct3D 12 queue, and copies the write
textures back.

### Available

`D3D12.Available`, and `D3D12.Problem` when not: without ReDefinition's `dxgi.dll`, with it
switched off or measuring only in `ReDefinitionProxy.ini`, or before its swapchain is made.
What the device supports, in Direct3D's own encoding:

| `D3D12.` | Encoding | Example |
|---|---|---|
| `FeatureLevel` | `D3D_FEATURE_LEVEL` | `0xC200` for 12.2 |
| `ShaderModel` | `D3D_SHADER_MODEL` | `0x67` for 6.7 |
| `RaytracingTier` | `D3D12_RAYTRACING_TIER` | 10 for 1.0, 11 for 1.1 |
| `MeshShaderTier` | `D3D12_MESH_SHADER_TIER` | 10 for 1.0 |
| `VariableShadingRateTier` | `D3D12_VARIABLE_SHADING_RATE_TIER` | 1, 2 |

### A compute pass

The shader, against the root signature every pass shares:

```hlsl
Texture2D<float4> Source : register(t0);          // t0-t7: read textures
RWTexture2D<float4> Result : register(u0);        // u0-u7: write textures
cbuffer Parameters : register(b0)                 // up to 256 bytes
{
    float Strength;
    float3 Padding;
};
SamplerState PointClamp : register(s0);
SamplerState LinearClamp : register(s1);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    Result[id.xy] = Source[id.xy] * Strength;
}
```

**From HLSL.** The mod ships the `.hlsl` file; ReDefinition compiles it with Windows'
`d3dcompiler_47.dll`, loaded from System32, to DXBC (`cs_5_0`). `#include` lines are
resolved relative to the file.

```csharp
int pass;
readonly RenderTexture[] read = new RenderTexture[1];
readonly RenderTexture[] write = new RenderTexture[1];
readonly byte[] constants = new byte[16];

void Start()
{
    string file = Path.Combine(Path.GetDirectoryName(typeof(MyAddon).Assembly.Location), "MyPass.hlsl");
    pass = ReDefinitionApi.CreateComputePassFromFile("MyMod pass", file, "main");
    if (pass == 0)
        Debug.LogWarning(ReDefinitionApi.D3D12LastRefusal);   // with the compiler's errors
}

void OnRenderImage(RenderTexture source, RenderTexture destination)
{
    read[0] = source;
    write[0] = myResult;   // a created RenderTexture of source's size
    Buffer.BlockCopy(new[] { 0.5f, 0f, 0f, 0f }, 0, constants, 0, 16);
    if (!ReDefinitionApi.Dispatch(pass, read, write, constants, (source.width + 7) / 8, (source.height + 7) / 8, 1, false))
        Debug.LogWarning(ReDefinitionApi.D3D12LastRefusal);
    Graphics.Blit(myResult, destination);
}

void OnDestroy()
{
    ReDefinitionApi.DestroyComputePass(pass);
    ReDefinitionApi.ReleaseTexture(myResult);
}
```

`CreateComputePassFromSource(name, hlsl, entryPoint)` takes the source as a string.
`D3D12.LastCompilerMessages` holds what the compiler said: its errors, or its warnings
after a success.

**From bytecode.** `CreateComputePass(name, bytecode)` takes a shader compiled beforehand
with the Windows SDK's compilers, from `Windows Kits\10\bin\<version>\x64`:

```
dxc -T cs_6_0 -E main -Fo MyPass.cso MyPass.hlsl
fxc /T cs_5_0 /E main /Fo MyPass.cso MyPass.hlsl
```

`dxc` makes DXIL (Shader Model 6) and signs it with the `dxil.dll` beside it, which
Direct3D 12 requires; `fxc` makes DXBC.

* **Building.** Each `CreateComputePass` method returns a handle above 0; the pipeline is
  built on the render thread. `ComputePassState(pass, out status)`: 1 built, 0 not yet, -1
  failed, with the reason.
* **Where to dispatch.** `Dispatch` executes at once. A render event that reads a texture
  Unity drew this frame sees this frame's content when it is issued through
  `Graphics.ExecuteCommandBuffer` from `OnRenderImage`, and the frame before's when issued
  from a camera's own command buffer (measured in a Unity 2019.4.18f1 player with KSP's
  graphics jobs). `DispatchInto(buffer, ...)` records the dispatch into a buffer the mod
  executes itself, once, in the frame it recorded it in: a later dispatch reuses the
  packet the recorded event points at.
* **Timing.** With `nextFrame` false the write textures hold the result when `Dispatch`
  returns, in Unity's command order: Direct3D 11 waits for the pass. With `nextFrame` true
  they get it at the pass's next dispatch, and the pass runs beside the rest of the frame.
* **Textures.** Up to 8 read and 8 write, none in both and none written twice; created
  `RenderTexture`s without mipmaps, arrays or multisampling, in a colour format a texture
  can be shared and bound for unordered access in -- no depth formats. An sRGB texture
  arrives as its bytes, not converted. Write textures are copied in before the pass too,
  so a pass may change part of one.
* **Limits.** 32 dispatches a frame. Each dispatch copies its textures twice and waits once;
  the shared copies take video memory, and one not used for ten seconds is freed.
  `ReleaseTexture` frees it earlier.
* **What Direct3D 12 sees.** Only the textures a pass is handed: not Unity's meshes, and not
  geometry Unity tessellates on the GPU.

## Later

Raytracing -- acceleration structures from meshes a mod hands over -- and contributions to
FSR 3's reactive and transparency masks are not in this build.
