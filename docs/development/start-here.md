# Start here

**For:** a developer who has just opened the repository.
**You need:** the repository, and time for two pages.
**You get:** a map of the developer documentation, and the page for the job in front of you.

ReDefinition brings the graphics mods of Kerbal Space Program 1 together in one concept. It has
one settings window and profiles for them, and it keeps their settings compatible. It adds
upscaling with FSR, DLSS and XeSS, frame generation and raytracing. These work on the whole
scene, other mods' effects included. A native proxy in place of `dxgi.dll` carries the work that
needs Direct3D 12.

## Read these two first

1. [Architecture](architecture.md) shows the parts, the areas of the plugin and which area may
   use which.
2. [Startup and scenes](flow-startup.md) shows what the game starts, in which scene, and what
   survives a scene change.

## The job in front of you

| You want to | Open |
|---|---|
| Change what happens to a frame in the mod | [A frame in the mod](flow-frame.md) |
| Change the proxy | [The proxy's present path](flow-proxy-present.md) and [add a feature to the proxy](howto-add-a-proxy-feature.md) |
| Change raytracing | [Raytracing from scene to screen](flow-raytracing.md) |
| Add or change a setting | [Add a setting](howto-add-a-setting.md) and [a setting's path](flow-setting.md) |
| Add a shader | [How shaders reach the game](flow-shaders.md) |
| Change a feature on screen | [The features on screen](flow-game-ui.md) |
| Change the dashboard | [The dashboard](flow-dashboard.md) |
| Change what other mods can call | [The interface for other mods](flow-mod-interface.md) |
| Understand how the plugin and the proxy exchange data | [The four channels to native code](flow-native-channels.md) |
| Build and check everything before a commit | [Build and check everything](howto-build-and-check.md) |
| Find a script | [The scripts in tools](tools-index.md) |
| Write or fix a comment | [Writing code comments](writing-code-comments.md) |

## The flows

Each flow page follows one thing through the code, from where it starts to where it ends:

* [Startup and scenes](flow-startup.md): every add-on, in the scene that starts it.
* [A setting's path](flow-setting.md): from a registration to the other mod's own save.
* [The features on screen](flow-game-ui.md): the HUD, the planner, the scaling and the menu.
* [A frame in the mod](flow-frame.md): the cameras, the upscaler and the presenter.
* [The four channels to native code](flow-native-channels.md): imports, render events, shared
  textures and the ini file.
* [The interface for other mods](flow-mod-interface.md): what `src/Api` forwards to.
* [The proxy's present path](flow-proxy-present.md): from `DllMain` to the screen.
* [Raytracing from scene to screen](flow-raytracing.md): the scene, the trace and the light
  back.
* [How shaders reach the game](flow-shaders.md): the bundle, and the proxy's HLSL.
* [The dashboard](flow-dashboard.md): the server, the telemetry and the browser.

## Look things up in the wiki

The wiki generates these from the code, so they are always current. Every area and every
file also has a wiki page with all its comments.

| Index | It lists |
|---|---|
| [Entry points](https://github.com/CaptainKeyboard/ReDefinition/wiki/Index-entry-points) | Every add-on, by scene |
| [Harmony hooks](https://github.com/CaptainKeyboard/ReDefinition/wiki/Index-harmony-hooks) | Every patch of the game's or another mod's code |
| [Assumptions about other mods](https://github.com/CaptainKeyboard/ReDefinition/wiki/Index-foreign-assumptions) | What the code takes for granted |
| [Command-line switches](https://github.com/CaptainKeyboard/ReDefinition/wiki/Index-command-line-switches) | The probes and profilers |
| [Proxy ini keys](https://github.com/CaptainKeyboard/ReDefinition/wiki/Index-proxy-ini-keys) | The proxy's settings and their defaults |
| [Registrations](https://github.com/CaptainKeyboard/ReDefinition/wiki/Index-registrations) | The mods that register their settings |
| [Proxy exports](https://github.com/CaptainKeyboard/ReDefinition/wiki/Index-proxy-exports) | The functions the plugin calls in the proxy |
| [Types](https://github.com/CaptainKeyboard/ReDefinition/wiki/Index-types) | Every class, struct, interface and enum |
| [Dependencies](https://github.com/CaptainKeyboard/ReDefinition/wiki/Index-dependencies) | Which area uses which |

## The rules of the repository

* The comments in the code are the developer documentation. Change the code, and the comment
  with it. `doclock check` fails on a comment that fell behind.
* Run [the checks](howto-build-and-check.md) before you commit. The unit tests include the
  documentation check and the check of which area uses which.
* The glossary names the things; use its words in code, comments and pages.
