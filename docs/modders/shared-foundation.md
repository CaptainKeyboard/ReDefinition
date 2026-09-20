# Using ReDefinition from your mod

**For:** mod authors who draw, raymarch, reproject, move geometry, or want a compute
pass on Direct3D 12.
**You need:** your own mod, and for the compute passes ReDefinition's `dxgi.dll`
installed.
**You get:** the frame's state, the hooks into ReDefinition's rendering, the chosen
profile, your key bindings, and Direct3D 12 compute passes, each with the code to copy.

A KSP game has some things only once: the camera's jitter, the frame's motion vectors,
camera cuts, the floating origin's shifts, the image after upscaling, and a Direct3D 12
device. ReDefinition offers all of them to every mod, in `ReDefinition.Api`. Why it is
built this way: [development/shared-foundation.md](../development/shared-foundation.md).

## Reach the interface

| Way | For | How |
|---|---|---|
| The shader include | a shader that reads the frame's state | copy [`ReDefinition.cginc`](../../unity/Assets/ReDefinition/Include/ReDefinition.cginc) from ReDefinition's repository into your shader project and `#include "ReDefinition.cginc"`. Without ReDefinition every function returns what the game has without it. MIT |
| The wrapper | a mod that runs with or without ReDefinition | copy [examples/ReDefinitionApi.cs](examples/ReDefinitionApi.cs) into your mod and put it in your namespace. `ReDefinitionApi.Installed` says whether ReDefinition is there. MIT |
| A reference | a mod that requires ReDefinition | reference `GameData/ReDefinition/Plugins/ReDefinition.dll`, declare `[assembly: KSPAssemblyDependency("ReDefinition", 0, 1)]`, and use `ReDefinition.Api`. `ReDefinition.xml` beside the DLL documents every member in your IDE |

Call every member from the main thread. `ApiInfo.Version` counts changes of the
interface. The wrapper returns what the game has without ReDefinition while the
installed interface is older than the one you wrote against. It binds each member once
as a typed delegate, so a call costs what a direct call costs and allocates nothing.

The example mod uses each part once:
[examples/ReDefinitionExample](examples/ReDefinitionExample). It draws a line from the
vessel as an overlay, darkens the upscaled image with a compute pass, reports a camera
jump, and keeps two settings of its own in ReDefinition's window.

To try it, clone ReDefinition's repository, build its solution, and copy
`ReDefinitionExample.dll` and `ExampleEffect.hlsl` from
`build/examples/ReDefinitionExample` into `GameData/ReDefinitionExample`.

## Read the frame's state

The state is decided once per frame, before the first camera of the 3D stack culls.
Read it from rendering code: camera callbacks, command buffers, `OnRenderImage`.

| `Frame.` | Shader global | Holds |
|---|---|---|
| `UpscalerActive`, `FrameGenerationActive` | `_ReDefinition_Frame.x`, `.y` | whether the upscaler reconstructs this frame, and whether frame generation receives it |
| `Technique` | | `"FSR 3"`, `"DLSS"` or `"AMD FSR (DLL)"` while the upscaler is active |
| `HistoryReset`, `HistoryResetReason` | `_ReDefinition_Frame.z` | whether temporal history is to be dropped this frame, and why |
| | `_ReDefinition_Frame.w` | the interface version |
| `RenderSize`, `DisplaySize` | `_ReDefinition_RenderSize`, `_ReDefinition_DisplaySize` | width, height, 1/width and 1/height of the 3D cameras' target and of the image shown. Both are the screen without the upscaler |
| `Jitter`, `JitterNdc` | `_ReDefinition_Jitter` | the jitter the 3D cameras render with, in pixels at render size (`.xy`) and in normalized device coordinates (`.zw`). Zero without it |
| `OriginShift`, `BodyShift`, `OriginShifted` | `_ReDefinition_OriginShift` | the floating origin's shifts since the previous frame, summed. KSP moves the active vessel, the camera and nearby objects by `-OriginShift`, and bodies and landed or packed vessels by `-BodyShift`. The global holds `OriginShift` and 1 where it shifted |

ReDefinition adds the jitter to every camera of the 3D stack in `OnPreRender`, and keeps
the projection without it in `Camera.nonJitteredProjectionMatrix`. If you raymarch or
reproject with your own matrices, take that one for history and motion, and the camera's
`projectionMatrix` for the rays.

## Tell the upscaler about your own camera cut

A camera mod that switches or jumps the view says so:

```csharp
ReDefinitionApi.RequestHistoryReset("MyCameraMod switched to " + camera.name);
```

With a reference to ReDefinition, that is `Frame.RequestHistoryReset(reason)`.

Before the frame's first 3D camera culls, the request reaches every mod in that frame.
Later, it reaches the upscaler and frame generation in that frame, and the mods in the
next one.

If your mod keeps temporal history of its own, drop it when ReDefinition says so:

```csharp
ReDefinitionApi.RegisterHistoryReset(reason => myHistoryValid = false);
```

Directly, that is `Frame.RegisterHistoryReset(handler)`, and `Frame.UnregisterHistoryReset`
takes it off again.

## Read the state in a shader

`ReDefinition.cginc` declares the globals above and includes `UnityCG.cginc`. Without
ReDefinition the globals are zero and the functions fall back: the sizes to
`_ScreenParams`, and the rest to no upscaler, no jitter, no reset and no shift.

| Function | Returns |
|---|---|
| `ReDefinitionInstalled()` | whether ReDefinition sets the globals |
| `ReDefinitionUpscalerActive()`, `ReDefinitionFrameGenerationActive()`, `ReDefinitionHistoryReset()` | the frame's state |
| `ReDefinitionRenderSize()`, `ReDefinitionDisplaySize()` | width, height, 1/width, 1/height, never zero |
| `ReDefinitionJitterPixels()`, `ReDefinitionJitterNdc()` | the jitter |
| `ReDefinitionRemoveJitter(clip)` | a clip-space position from the camera's own matrices (`UnityObjectToClipPos`) without the jitter, with `y` flipped as `_ProjectionParams.x` says |
| `ReDefinitionOriginShift()`, `ReDefinitionOriginShifted()` | the floating origin's shift |
| `ReDefinitionMotionVector(clipCurrent, clipPrevious)` | a motion vector in Unity's encoding, from this frame's and the previous frame's clip-space position, both without jitter and with `y` up |

If a vertex shader moves geometry, waves or wind for example, the motion vector pass
computes the position twice, with this frame's and the previous frame's parameters:

```hlsl
#include "ReDefinition.cginc"

float4x4 _MyNonJitteredVP;       // GL.GetGPUProjectionMatrix(camera.nonJitteredProjectionMatrix, false) * camera.worldToCameraMatrix
float4x4 _MyPreviousVP;          // the same, kept from the previous frame
float _MyTime, _MyPreviousTime;
// Wave(vertex, time): your own displacement.

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

The include is compiled with every function called, in Unity 2019.4.18f1, the version
KSP 1.12.5 is built with.

## Hooks into ReDefinition's rendering

The hooks below run while ReDefinition's upscaler or frame generation runs, which is
while the player has a graphics profile chosen. Without one, a handler is not called,
so a mod that must draw either way keeps its own path as well. A handler that throws is
removed and logged once, and the others keep running. No mod uses these hooks in the
game yet, so expect rough edges and report what you find. *Hook probe, into the log*,
in the diagnostics window's *Debug* tab, writes what each hook is called with, which
tells you whether yours should have been called.

### Add motion vectors for what Unity misses

Unity writes no motion vectors for geometry a vertex shader moves, or for content that
writes no depth. Your handler is called as the scene camera culls, with the frame's
state decided. It adds commands to that camera's capture, which runs at its
`BeforeImageEffects`, after Unity's motion vectors are written:

```csharp
ReDefinitionApi.RegisterMotionVectors((buffer, motionVectors, depth, scene) =>
{
    buffer.SetRenderTarget(motionVectors);
    buffer.SetGlobalTexture("_MyModSceneDepth", depth);
    foreach (Renderer renderer in myAnimatedRenderers)
        buffer.DrawRenderer(renderer, myMotionVectorMaterial);
});
```

* `motionVectors` is `RGHalf` at render size, in Unity's encoding: the current minus the
  previous viewport position, viewport coordinates running from 0 to 1, with `y` flipped
  where `UNITY_UV_STARTS_AT_TOP`. Unity's `Internal-MotionVectors.shader` defines it, and
  `ReDefinitionMotionVector` in the include writes it.
* Take both positions without jitter and with `y` up, as Unity's `_NonJitteredVP` and
  `_PreviousVP` are. Build them from
  `GL.GetGPUProjectionMatrix(scene.nonJitteredProjectionMatrix, false)` and
  `scene.worldToCameraMatrix`, this frame's and the one you kept from the previous frame.
  The motion vector pass flips `y` itself.
* `depth` is the scene's raw depth at render size, `RFloat`, reversed on Direct3D 11,
  where 1 is near and 0 is far, to test against.
* Every upscaler and frame generation read the result.

### Draw after the upscaler

An effect on the upscaled image runs at display size. It runs before TUFX's effects
after the upscaler, and before frame generation's copy of the scene, so it is
interpolated with the scene:

```csharp
ReDefinitionApi.RegisterAfterUpscaling((buffer, image, scene) =>
{
    buffer.GetTemporaryRT(tempId, image.width, image.height, 0, FilterMode.Bilinear, image.format);
    buffer.Blit(image, tempId, myEffectMaterial);
    buffer.Blit(tempId, image);
    buffer.ReleaseTemporaryRT(tempId);
});
```

The buffer is executed at once from `OnRenderImage`, and `image` is HDR while the
upscaler runs. A Direct3D 12 compute pass recorded into this buffer with `DispatchInto`
reads the upscaled image. The example mod does exactly that.

### Draw an overlay

Lines and markers in the world are drawn after the scene, at display size. They are not
upscaled, and frame generation treats them as interface, so it does not interpolate
them:

```csharp
ReDefinitionApi.RegisterOverlay((buffer, scene) =>
{
    buffer.SetViewProjectionMatrices(scene.worldToCameraMatrix, scene.nonJitteredProjectionMatrix);
    buffer.DrawMesh(myTrajectoryMesh, Matrix4x4.identity, myLineMaterial);
});
```

Your handler fills the buffer of a camera behind every camera that draws the scene, as
that camera culls. The buffer draws onto the backbuffer at its `AfterEverything`, which
holds no scene depth. The camera exists only while a handler is registered.

## Follow the chosen profile

`Profiles.Current` is the graphics profile chosen in ReDefinition's window: `low`,
`medium`, `high`, `ultra` or `max`. It is null while none is chosen, which is when the
upscaler and frame generation are off.

`Profiles.RegisterChanged(Action<string>)` is called with the new name when it changes.

Through the wrapper these two are `ReDefinitionApi.Profile` and
`RegisterProfileChanged`.

To put your own settings into the window and into the profiles:
[registering-a-mod.md](registering-a-mod.md).

## Ask whether your key is pressed

A binding declared in your registration with a `KEY` block is the player's to set in the
*Keys* tab. If your mod keeps no key of its own, ask here:

| Member | Answers |
|---|---|
| `Keys.Binding(key)` | the binding as text, such as `LeftAlt+F10`, `F11` or `None`, or null where no binding of that key is registered |
| `Keys.Pressed(key)` | the key went down this frame, with exactly the binding's modifiers |
| `Keys.Held(key)` | it is held |
| `Keys.Released(key)` | it went up this frame |

`key` is your mod's id in its registration, a dot, and the `KEY` block's name, for
example `mymod.window`. Through the wrapper the four are `ReDefinitionApi.Binding`,
`KeyPressed`, `KeyHeld` and `KeyReleased`.

Another modifier held means another binding is meant, so `F10` does not answer while
`Alt+F10` is pressed. While the player is setting a binding in the window, none of them
answers.

## Run a compute pass on Direct3D 12

The passes run on the Direct3D 12 device of ReDefinition's `dxgi.dll`, on Unity's
textures. Unity keeps rendering in Direct3D 11, so each dispatch copies its textures
into textures both devices share, runs the pass on the Direct3D 12 queue, and copies the
write textures back.

### Check what is there

`D3D12.Available` says whether the device is there, and `D3D12.Problem` says why not.
The reason is one of these: no `dxgi.dll`, the proxy switched off, or set to measure
frame times without substituting anything (`measureOnly=1` in `ReDefinitionProxy.ini`),
or the swapchain not made yet. Through the wrapper the two are
`ReDefinitionApi.D3D12Available` and `D3D12Problem`. The properties below carry the
same `D3D12` prefix there; the methods keep their names.

What the device supports, in Direct3D's own encoding:

| `D3D12.` | Encoding | Example |
|---|---|---|
| `FeatureLevel` | `D3D_FEATURE_LEVEL` | `0xC200` for 12.2 |
| `ShaderModel` | `D3D_SHADER_MODEL` | `0x67` for 6.7 |
| `RaytracingTier` | `D3D12_RAYTRACING_TIER` | 10 for 1.0, 11 for 1.1 |
| `MeshShaderTier` | `D3D12_MESH_SHADER_TIER` | 10 for 1.0 |
| `VariableShadingRateTier` | `D3D12_VARIABLE_SHADING_RATE_TIER` | 1, 2 |

### Write the shader

Every pass shares one root signature:

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

### Build and dispatch it from HLSL

Ship the `.hlsl` file with your mod. ReDefinition compiles it with Windows'
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

### Or ship bytecode

`CreateComputePass(name, bytecode)` takes a shader you compiled beforehand with the
Windows SDK's compilers, from `Windows Kits\10\bin\<version>\x64`:

```
dxc -T cs_6_0 -E main -Fo MyPass.cso MyPass.hlsl
fxc /T cs_5_0 /E main /Fo MyPass.cso MyPass.hlsl
```

`dxc` makes DXIL, Shader Model 6, and signs it with the `dxil.dll` beside it, which
Direct3D 12 requires. `fxc` makes DXBC.

### The rules a dispatch follows

| Rule | What it means |
|---|---|
| Building | Each `CreateComputePass` method returns a handle above 0, and the pipeline is built on the render thread. `ComputePassState(pass, out status)` answers 1 for built, 0 for not yet, and -1 for failed, with the reason |
| Where to dispatch | `Dispatch` executes at once. A render event that reads a texture Unity drew this frame sees this frame's content when it is issued through `Graphics.ExecuteCommandBuffer` from `OnRenderImage`, and the previous frame's when issued from a camera's own command buffer |
| `DispatchInto` | It records the dispatch into a buffer you execute yourself, once, in the frame you recorded it in. A later dispatch reuses the packet the recorded event points at |
| Timing | With `nextFrame` false, the write textures hold the result when `Dispatch` returns, in Unity's command order: Direct3D 11 waits for the pass. With `nextFrame` true they get it at the pass's next dispatch, and the pass runs beside the rest of the frame |
| Textures | Up to 8 read and 8 write, none in both and none written twice. Use created `RenderTexture`s without mipmaps, arrays or multisampling, in a colour format a texture can be shared and bound for unordered access in. Depth formats do not work. An sRGB texture arrives as its bytes, not converted. Write textures are copied in before the pass as well, so a pass may change part of one |
| Limits | 32 dispatches a frame. Each dispatch copies its textures twice and waits once. The shared copies take video memory, and one not used for ten seconds is freed. `ReleaseTexture` frees it earlier |
| What Direct3D 12 sees | Only the textures a pass is handed. Not Unity's meshes, and not geometry Unity tessellates on the GPU |

## Not in this build

Raytracing, with acceleration structures built from meshes a mod hands over, and
contributions to FSR 3's reactive and transparency masks.
