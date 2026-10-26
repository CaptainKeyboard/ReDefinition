# Changelog

## Unreleased

### Added

- **Intel XeSS files.** A *Download ...* button under *Display* fetches Intel's
  `libxess.dll`, `libxess_fg.dll` and `libxell.dll` from Intel's release of the XeSS SDK
  3.0.2 on GitHub, after you accept Intel's licence. Every file is checked against the
  release before it is placed, and the row names the files in place with their versions.
- **Docking alignment on the navball.** With a docking port as the target, an orange ring
  on the navball shows where to point the nose so that the two ports face each other.
- **The flight panel.** KSP's maneuver panel shows profiles in place of its orbit tabs:
  Ascent, Orbit, Rendezvous, Landing, Docking and Air, each a set of figures in KSP's
  own fields, such as the suicide burn, the transfer window, the closest approach, the
  angle of attack or the angle between docking ports. It switches to the profile for
  what the vessel is doing, and grows to the right for more figures. The pencil after the
  tabs changes a profile's figures and their order, or adds a profile of your own.
- **Launch from the editor, and back.** In the VAB a button over the launch pad seen
  through the door launches the vessel, in the SPH one over the runway. On the ground near
  the space centre, a button on the VAB or SPH reverts to the editor where KSP's pause menu
  would, or else recovers the vessel and opens it in the editor; it asks first. They look
  like the space centre's own buttons.
- **Tooltips on the HUD.** The buttons of KSP's flight and editor screens that had none
  name themselves as KSP's others do: the stages' plus and minus buttons, a stage's delta-v
  bar, the total delta-v, the delta-v tools' toolbar button and their body list, the alarm
  clock's button, the altimeter's mode switch, the staging's reset button; and
  ReDefinition's own: the round gauge, the vertical and horizontal speed. The staging's
  reset button in the editors asks before it resets.
- **The round gauge.** KSP's vertical speed dial becomes a digital gauge with a lit arc.
  It shows the stage's burn time, the countdown and delta-v of the next burn, the time to
  impact, the closing speed on a target or the angle of attack. It picks the figure for
  the situation, or the one you click to.
- **Vertical and horizontal speed at the navball.** Under its SAS modes, *V* and *H* show
  the vessel's speed over the surface in its two parts.
- **The altimeter switches by itself.** Below 5 km over the ground and on it, it shows the
  height above the ground; above 6 km, the height above sea level. Its switch still
  changes it by hand. *Altimeter switches to the ground* under *Gameplay*, *Flight*.
- **Flights start in the maneuver mode.** KSP's orbit and maneuver panel is open as a
  flight starts; *Start flight in maneuver mode* under *Gameplay*, *Flight* switches it off.
- **Persistent Rotation in the settings window.** Under *Gameplay*, *Flight*: whether its
  window shows in flight, and the default reference body. Its toolbar button, which
  only showed and hid the window, is hidden.
- **Recommended KSP settings.** Taking control at the first start and *Reset* switch on
  *Advanced tweakables*, the *Extended burn indicator* and *Always show closest
  approach*, and set the *Conic patch limit* to 6.
- **The crew portraits' slot buttons** show a green plus and a red cross, as the staging
  stack's buttons for stages do, in place of KSP's *|<* and *>|*.
- **Better Time Warp's button in the time panel.** With Better Time Warp Continued
  installed, its arrow stands at the end of the time warp row, in the panel's look, and
  opens its window. Its toolbar button is hidden; *Hide from toolbar* under *Mods / Toolbar*
  brings it back.
- **RCS, turning and the TWR at burnout in the VAB and the SPH.** The planner's *Thrust*
  part gives for every step the RCS of what flies it as a stage row, thrust, Isp, TWR and
  burn time, its delta-v in the fuel plan, and how long it takes to turn by 90° on its reaction wheels, RCS and gimbals; the route
  warns where that is slow for a launch, a landing or a burn in orbit, and where a
  section has no RCS to dock with. The stage details give the TWR as a stage lights and as it burns
  out.
- **The delta-v tools in the staging stack.** KSP's *Δv* button opens every stage's
  details and, in the VAB and the SPH, a *Δv Tools* window under the lowest stage with
  the body, atmosphere, altitude and pressure; pressed again, it closes them. The details
  always show every value, in two columns side by side, the part icons as well.
- **Grip for landing legs.** Every landing leg has a *Traction* button in its part menu,
  in the VAB and the SPH, from Default to Extreme Grip, as the grip pads have, so that a
  lander stays put on a slope.
  Needs ModuleManager.
- **Readable on large screens.** What KSP leaves at a fixed number of pixels now takes
  its UI scale: the map's icons and labels, the orbit lines, the labels over vessels, the
  maneuver node's gizmo, the editor's move and rotate gizmos, and other mods' windows.
  The tech tree opens larger and zooms in further, its nodes twice their size and the
  parts it unlocks 1.2 times. A row in the settings window sets the
  size as a share of the UI scale. Where 4kSP or Maneuver Node Evolved is installed, its
  scaling of the same things is turned off, so nothing grows twice. The UI scale row goes
  up to 400 %, in steps of 10 %.
- **A mission planner in the VAB and the SPH.** A button with *Δv* on it, next to KSP's
  mode buttons, opens it in the parts list's place. It lays out a route from stop to
  stop, A → B → C, on the surface or in an orbit of any body the game has loaded, and
  lists the steps in between: up into an orbit, across to the next body or straight into
  its air, down onto the surface; a way across can be split into the burn that leaves
  and the one that arrives. You add the vessel's sections, capsules, landers and
  satellites, as many as it has, each from the parts its decouplers and docking ports
  separate; the planner adds a crewed vessel's capsule and an uncrewed vessel's one
  satellite by itself. Each step says who flies it, the whole vessel or one section,
  set by the planner or by you; a lander leaves the rest, docks with it again and can be
  refilled from it, or flies on alone, as the step after it says; a satellite stays at its
  destination; the capsule comes home alone. The planner shares the vessel's stages out
  over the steps, counting the mass each stage pushes at that moment. The fuel plan lists
  every step with what it needs and what is left after it, in red what it lacks; a step
  opens to its stages, each with what it gives there, what it keeps and its TWR, to use
  it there or keep it for later and to drop it after the step; a refill can come from a
  stage you pick. *Thrust* lists every step that burns: on a launch or a landing on the
  engines the TWR of the stage that decides, in the air where the body has any; on every
  other burn how long it takes against its orbit and the delta-v that costs. What blocks a step
  shows in red under it: too little fuel, too little thrust to lift off or land, no
  parachutes or heat shield, a lander not where it has to be, a satellite not carried to
  where it stays. The plan is kept per vessel.
- **The navball moves sideways.** Drag its show/hide button left or right in flight; the
  SAS switches, the autopilot modes and the gauges move with it, and it stays on the
  screen. A click still shows and hides it. The position is kept; with another screen resolution the
  navball goes back to the centre.
- **Raytracing.** A new group of rows under *Display*: *Raytracing* as *Off*, *Shadows*,
  *Shadows and light* or *Full*, with *Light bounces*, *Moonlight*, *Moon reflection*,
  *Moon glow*, *Sun reflection*, *Night light* and *Light colour*. Rays trace the sun's
  shadows as far as the scene reaches, the light bouncing off surfaces and the sky, the
  moons' light and shadows at night, and with *Full* reflections, the sea mirroring the
  sun and the moons. The rays see vessels, the ground within 30 km, the space centre with
  its flags and Parallax's rocks, trees and grass, and EVE's clouds. It works with
  Deferred and without it, needs a GPU with DirectX Raytracing 1.1 and is off in every
  profile.
- **Ray Reconstruction.** With DLSS as the upscaler, NVIDIA's Ray Reconstruction removes
  the noise of the traced light and upscales in one step, as a setting, on at first. It
  runs beside DLSS frame generation and, through NVIDIA's NGX (`rrPath`), beside FSR 3's
  and XeSS's frame generation. On a Radeon RX 9000, AMD FSR Ray Regeneration does the
  denoising; it is untested on that GPU. Elsewhere ReDefinition's own denoiser does.
- **Raytracing, off at first: radiance cache, opacity micromaps, RTX Mega Geometry.**
  `rtRadianceCache` keeps the indirect light in a hash grid over the world, so that fewer
  rays are traced; `rtOpacityMicromaps` lets the GPU decide most hits on grass and ferns;
  `rtMegaGeometry` builds new meshes' structures from RTX Mega Geometry's clusters. The
  last two are for NVIDIA GPUs. On an RTX A4500 micromaps cost 4 to 10 % of the frame
  rate and the cache gained 1 %.
- **A vessel's meshes appear in the rays at once.** Their structures are built in
  batches, from pooled buffers, and compacted in later frames (`rtBlasBatch`,
  `rtBlasPool`, `rtBlasBudget`, `rtBlasCompaction`). The frame that builds a vessel of 150
  parts took 0.63 ms of GPU time where building mesh by mesh took 21.6 ms (the raytracing
  harness, RTX A4500).
- **FSR 4 or FSR 3 named.** For AMD's DLL the *Technique* row names the FSR it runs, such
  as *FSR 4.1.1*, or *FSR 3.1.5 fallback* on a GPU without FSR 4; the log gives the
  reason.
- **XeSS super resolution.** *XeSS (DLL)* as a technique, from Intel's `libxess.dll`, on
  Intel Arc and on any GPU with Shader Model 6.4 and DP4a. The row names its version.
- **XeSS frame generation, with XeLL.** From `libxess_fg.dll` and `libxell.dll` of Intel's
  XeSS SDK 2.1 or newer, 2x on any GPU with Shader Model 6.4, more on an Intel GPU
  (`xessFgFrames`, untested on an Intel Arc). `frameGenerationTechnique` in
  `ReDefinitionProxy.ini` chooses between DLSS, XeSS and FSR 3; `auto` takes DLSS where it
  runs, XeSS on an Intel GPU, FSR 3 otherwise.
- **Low latency.** NVIDIA Reflex now runs without DLSS frame generation. A latency
  brake, on with a target of 4 ms (`latencyBrakeTargetMs`), waits at the start of a frame
  for as long as frames queued in the proxy, on any GPU and under DLSS frame generation
  too. On an RTX A4500 with raytracing on *Full* it took 9 to 14.5 ms off a latency of 58
  to 72 ms at the same frame rate. Intel's XeLL runs on an Intel GPU and with XeSS frame
  generation. AMD Anti-Lag 2 (`antiLag2`, off at first) is for a Radeon RX 5000 or newer
  and untested on a Radeon. The *Low latency* row under *Display* says what runs, and the
  log measures the latency on every GPU, split into stages.
- **NVIDIA Streamline 2.14.16.** *NVIDIA DLSS files* downloads the files of this release,
  and the proxy loads no other version of Streamline.
- **DLSS frame generation on RTX 30 and RTX 20.** The *DLSS-G on RTX 30/20* row installs
  the community package dlssg_for_sm86 from the zip the player downloaded: *Install from
  zip* places its files beside `KSP_x64.exe`, keeping what was there as `.old`. Rows set
  its preset, its multiplier, DLSS super resolution through its INT8 kernels and its frame
  timing log. A first install sets the multiplier to 6X and the preset to C, which gave 4
  to 8 % more rendered frames a second than Auto on an RTX A4500; a reinstall keeps your
  choices.
- **How many frames to generate.** The *Frame generation* row is *Off*, *2x* to *6x* or
  *Dynamic*, and a change applies at once. *Dynamic* lets DLSS frame generation reach the
  display's refresh rate (NVIDIA driver 595.41 or newer, a GPU with multi frame
  generation) and is the default where it is supported, otherwise the default is *2x*. A
  note under the row says what runs and why a step is not offered.
- **The diagnostics window as pages.** A list of pages on the left, a strip on top with
  what runs, the rendered and shown frame rate, a frame time graph, the latency and the
  load of the main thread, the render thread and the GPU. A search field finds options on
  every page, *Changed* lists those away from their default, *Reset debug switches* puts
  them back, and a star pins an option to the foot of the window. The window is taller and
  keeps its last page and place.
- **The main menu and the space centre.** Fireworks behind the main menu, on Kerbin's night
  side too; a camera flight from the main menu down to the space centre, under the sun of
  the save; Parallax's grass on the lawns, where it is installed; the runway lights dimmed.
- **Tools for measuring.** `tools/benchmark_matrix.py` runs a standing comparison of
  upscalers, frame generation, raytracing and latency models and writes the results into
  `docs/development/benchmarks.md`. The scene probe's `-redefinitionGpuProfile` names what
  each camera and pass costs on the GPU, and `-redefinitionProbeGrass` flies over a field of
  Parallax's grass.

### Fixed

- **Other mods' buttons on the loading screen.** The Toolbar's buttons and MechJeb's menu
  no longer show on KSP's black screen between scenes.
- **The settings window after a question.** Opened from the pause menu, the window took no
  more clicks once *Reset*, *Restore* or another question over it was answered. Rows that
  turned live or grey while their tab was not shown stayed as they were until the window
  was opened again. Unticking one mod under *Hide all from toolbar* unticked them all, and
  *Cancel* could choose a graphics profile again. The pause button in the title row dropped
  what waited for *Accept*.
- **Escape only cancels.** Escape while a row waits for a key or an axis, or in the search
  field, no longer closes the window and opens the pause menu as well. A row stops waiting
  for a key when you leave its tab or type in the search.
- **Hotkeys while typing.** ReDefinition's hotkeys, and those of mods that read them through
  it, no longer fire while a text field of KSP's has the keyboard.
- **Save and restart asks first**, and stands beside *Reset*: it no longer moved under the
  pointer as *Accept* was clicked. The tabs show their mark while something in them waits.
- **The mouse wheel over a button** in the planner scrolls its list, as over a label.
- **The planner's headings and altitude fields** no longer take the keyboard: Space, Return
  and the arrow keys stay the editor's after a click on them. The headings show their
  tooltips.
- **The planner with jets, the RAPIER, other propellants and solid boosters.** Jets and a
  RAPIER in its air-breathing mode counted for nothing, and a jet stage above rockets left
  them no fuel. The planner now works out the stages itself where an engine breathes air,
  and lets the jets fly the first part of a launch from Kerbin or Laythe; a spaceplane
  lifts off along the runway. RCS that burns liquid fuel and oxidizer counts what it can
  burn in its mix, at its thrust limit. A stage of solid boosters burns out on the step it
  starts on. A tank's separation motor no longer counts as an engine, and an ion engine
  shows the electric charge it needs against what the vessel holds.
- **Far ground no longer shimmers under the upscaler.** Zoomed out far, over the Mun
  for example, the far side of the ground, or the whole body, trembled and kept
  trembling. Unity's motion vectors there are imprecise so far from the origin;
  ReDefinition now computes them itself for the sky, and for the ground more than
  500 m away while the camera is more than 20 km out.
- **No more flicker on the ground and Parallax's scatter.** Unity at times stops writing
  the flight camera's motion vectors, and the upscaler, frame generation, Scatterer's TAA
  and TUFX's antialiasing then take the picture from far off; Parallax's scatter showed it
  most. In flight, ReDefinition now writes the camera's own movement where Unity left
  them, whether its upscaler runs or not, and says so in `KSP.log`.
- **Bundled mods' toolbar buttons stay hidden across scenes.** At every scene start KSP's
  launcher moves its buttons into a new layout and shows each there, a hidden one too,
  and nothing hid it again: the buttons of the bundled mods came back in the main menu,
  the space centre and in flight until *Hide all from toolbar* was switched off and on.
  ReDefinition now switches such a button off again as soon as the launcher is ready.
- **DLSS frame generation generates nothing while KSP's window is not in front.** The
  rendered frames are shown alone and the log says that the window is not in the
  foreground. Keep the window in front when testing or recording.
- **No more hitch once a second.** Work that waited for the render thread, or searched the
  whole scene, once a second is gone from the upscaler's per-frame code, and the round gauge
  reads its picture without waiting for the GPU.
- **KSP's reflection probe stays on with Deferred.** With the reflection refresh set to
  Off in KSP's settings, the probe stayed off in flight after Deferred raised the refresh.
- **Parallax's scatter textures get the upscaler's mipmap bias.**
- **The settings window and its questions stay within the screen** at any UI scale, and
  mod windows placed by code stand where they were placed.
- **The tooltip of the physics delta-time limit** said the opposite of what it does.

### Changed

- **The DLSS model is chosen by NVIDIA per mode by default.** *Default* in the *DLSS
  model* row. Low, Medium and High take it, Ultra and Max take *Preset L*.
- **Frame generation holds off** while KSP's pause menu is open, while the rendered frame
  rate is below the technique's minimum (60 or 30 for FSR 3 by its version, 30 for DLSS,
  40 for XeSS; `fgMinimumFps`), for a single slow frame, and steadily where V-Sync caps
  the rendered rate below the minimum.
- **`ReDefinitionProxy.ini` holds every key.** The proxy completes the file at every start
  with the keys added since, each with its comment. A default the proxy had written
  follows a newer version's; a value you changed stays. When Windows refuses to load the
  proxy as `dxgi.dll`, the game says so.
- **Every other mod is optional.** ReDefinition's own features work whole without Deferred,
  Scatterer, Parallax and the rest. The *Other mods* page of the diagnostics window lists
  the places in other mods' code that ReDefinition relies on, those that changed in the
  installed version first.

### For mod authors

- `Frame.Technique` can read `"XeSS (DLL)"`. A key read through `Keys` while its setting is
  gone reads as unbound instead of throwing, and hotkeys do not fire while the diagnostics
  window's search field has the keyboard.

## 0.1.3 -- 2026-09-26

### Added

- **DLSS model in the settings window.** *High* is NVIDIA's model L, the sharper and more
  stable image with less ghosting; *Fast* is model M, closer in speed to DLSS's standard
  models. High by default; the profile Low takes Fast. It replaces the DLSS preset switch
  in the Debug tab.
- **A log line comparing the motion vectors with the true motion** about every ten
  seconds in flight, on the active vessel and on the ground around it, in pixels.
- **A log line checking the vessel's motion vectors on every pixel** about every ten
  seconds in flight, per part, with the frames where they were off.
- **Vessel motion vectors in the Debug tab**, not saved: written by ReDefinition, or
  Unity's own for a comparison.
- **Record the screen in the Debug tab.** Half a second of what the monitor shows, frame
  generation's frames included, into `ReDefinitionCaptures` beside `KSP_x64.exe`, with
  eight consecutive frames of what frame generation receives: colour, depth, motion
  vectors and the camera, and one frame of the vessel shadow's inputs and results. The
  game stalls briefly while these are written.

### Fixed

- **Frame generation leaves the vessel's shadow where it is.** Under a vessel the camera
  follows, the shadow stands on the screen while the ground runs, and every generated
  frame dragged it along with the ground or lost it. ReDefinition now takes the active
  vessel's shadow out of the image frame generation moves, pixel by pixel where that is
  the smaller error, so it is kept in place like the interface. Vessel shadow in frame
  generation in the Debug tab switches it off.
- **The active vessel moves smoothly between physics steps.** KSP moves parts only on a
  physics step, 50 a second, so at about 59 frames a second one frame in six showed the
  vessel and the camera standing still, and frame generation made its worst frames next
  to those. ReDefinition now draws the vessel where it is between its last two steps;
  the physics keep their own positions. Render interpolation in the Debug tab switches it
  off.
- **Parts drawn in the forward pass have depth and motion vectors.** Unity's depth
  texture holds only what the deferred pass draws; ReDefinition writes the vessel's
  depth for the others.
- **Firefly's and TUFX's own windows open from the Advanced buttons.** A mod that hides
  KSP's interface and shows it again without saying so, QuickIVA at the start of a
  flight, left KSP's interface marked hidden, and these two draw only while it is
  marked shown. ReDefinition marks it shown again where it is on screen.
- **The vessel's own motion vectors.** On a fast run up to a percent of the vessel's
  pixels carried the ground's motion instead of its own; ReDefinition now writes the
  vessel's motion vectors itself, 0.15 % of its pixels more than a pixel off in flight.
- **`ReDefinitionMotionVector` in the shader include writes `y` the right way up.** It
  flipped `y` where Unity does not for a camera that renders into a render texture, as
  the scene camera does while ReDefinition runs, so motion drawn with it pointed the
  wrong way vertically.
- **Planets and their clouds seen from orbit no longer trail under time warp.** Their
  pixels carried only the camera's turn as motion, not the planet's rotation or the
  vessel's travel along its orbit. The distant planets' motion is now computed from the
  planets' own movement where the near scene draws nothing.
- **The mouse wheel over a button scrolls the list under it.** KSP's dialog button
  prefab carries an `EventTrigger`, which takes the wheel; the settings window now
  passes it on, as KSP's own forwarder does.
- **The picture never goes away while the proxy runs.** Every way out of the frame's
  copy used to leave the frame unpresented and tell the game the Present had failed: the
  screen stayed black or frozen with nothing to bring it back. The frame is now
  presented as it stands, so the picture holds the previous frame for as long as the
  copy fails, and the log counts it.
- **A black screen where the upscaler's buffers could not be made.** Unity's
  `RenderTexture.Create()` answers true even where Direct3D refused the texture, so the
  rig was built on buffers that were not there and the cameras rendered into nothing.
  Reported from the game after a scene change under memory pressure, where switching the
  upscaler off and on brought the picture back. The rig now asks for the native handle,
  its set-up fails where a texture is missing, and the upscaler comes back by itself
  once the memory does. A texture lost while the rig runs is caught the same way, about
  once a second, and the rig is rebuilt.

### Changed

- **Close, or Accept and Cancel.** The settings window shows *Close* while nothing is
  changed. A change brings *Accept* and *Cancel* in its place: *Accept* sets what the
  window holds, *Cancel* puts every row back, and neither closes the window. *Apply* is
  gone.
- **Save and restart.** Where a change takes effect only at a start, a button left of
  *Accept* applies it and starts KSP again: *Save and restart* where a game is loaded,
  which saves it first as KSP does, *Apply and restart* in the main menu. In flight it
  saves only where KSP allows saving at that moment; in the editors the vessel goes into
  KSP's auto-saved ship.
- **Let ReDefinition take control.** The first start's button, formerly *Use High*,
  also makes KSP's *Settings* buttons open ReDefinition's window and loads the main menu
  again, so that its *Settings* leads there at once.
- **KSP's UI scale follows the screen.** Its default is 150 % from 1440 lines and 200 %
  from 2160 (4K), and 100 % below: *Reset* sets it, and taking control at the first
  start sets it where the scale is still KSP's own 100 %.
- **Dialogs over the settings window stand apart from it**: the screen behind them is
  darkened, and they are drawn opaque with a light edge. In NVIDIA's download the
  licences are links in one row rather than buttons.
- **Reset chooses High.** It sets every setting to its default as before, KSP's except
  the screen resolution, and then chooses the profile High over them, so choosing High
  afterwards changes nothing. ReDefinition is active afterwards, with the upscaler at
  *AA only*.
- **The other mods' antialiasing is switched off only while the upscaler runs.** No
  profile changes KSP's MSAA, Scatterer's TAA and SMAA or Deferred's editor SMAA in their
  settings any more; with the upscaler off, frame generation alone included, they
  antialias the image as they are set.
- **The pause button works in the space centre and the tracking station too**, where
  game time runs as well. There it stops Unity's time scale and gives it back through
  KSP's time warp, as KSP does when it unpauses a flight.
- **ReDefinition's toolbar button is not in the main menu any more**: the
  *ReDefinition* entry under *Settings* opens the window there.
- **Sharpness** runs from 0 to 1.2. Each profile shows one sentence and the hardware
  beside its button; what it sets is in its tooltip. **The profiles name no graphics
  card any more**: which tier a card holds has not been measured. The groups show no count. The window
  is thinner than KSP's own dialogs while a flight runs behind it, with a shadow under
  the rows' text; where nothing moves behind it, it is nearly opaque.
- **Every setting of KSP's own settings screen is in the window**, and the tabs are a
  tree: *Gameplay*, *Audio*, *Display*, *Graphics* with *Detail* under it, *Controls*
  with *Axes* and *Devices* under it, and *Mods / Toolbar* set apart. *Display* holds the
  screen and the upscaler, *Detail* KSP's quality settings and the shadows, planets and
  effects as groups. The tabs under a parent show while it or one of them is open. They hold KSP's gameplay and system
  switches, the map view's lines, its volumes, the screen, the keyboard layout, the
  joystick and gamepad axes, the mouse wheel, Track IR and the 6-DOF device. In
  *Gameplay*, *Detail*, *Axes* and *Devices* the rows are in groups that open and fold.
- **A search above the tabs** finds every setting by its name or its mod, each word on
  its own and in any order. The window's background is less see-through. A blue dot
  marks a row, and a tab, where a change waits for *Apply*.
  KSP's graphics settings that were kept without a row have one now, and the screen
  resolution and full screen are there too. KSP's settings can be changed without a
  graphics profile, as in KSP's own screen; no profile sets anything outside the
  graphics.
- **Axes are bound by moving them**, as in KSP's own input screen: each with a primary
  and a secondary binding, invert, sensitivity and deadzone. KSP's keys take a
  controller's buttons as well, and the four custom axes' keys are in *Keys*.
- **Replace original settings**, at the top of *Mods / Toolbar*: every *Settings* button of KSP
  opens this window instead of KSP's screens.
- ***Reset to defaults* is now *Reset*, and resets all of KSP's settings** as KSP's own
  *Reset* does, its keys and axes included, besides the mods' settings. The screen
  resolution and full screen stay as they are.
- **A change that does not take effect at once is said in yellow** between *Reset* and
  *Apply*, from the moment it is changed until it arrives: at the next scene or after a
  restart. Its tooltip names each setting. The rows' tooltips no longer end in "From
  ...; takes effect ...; saved in ...", and no tooltip line is longer than the
  Upscaler's first.
- The window is titled *Settings -- ReDefinition*.
- **The window opens from the menu Escape opens**, in flight and in the space centre. A
  *ReDefinition* entry stands there under KSP's own *Settings*, and *All settings* in ReDefinition's
  section of KSP's settings dialog opens the same window. The toolbar is hidden while
  that menu is up, so this is the way in mid-flight.
- **A *ReDefinition* entry in KSP's main menu**, under *Settings*, in the menu's own
  font and colours, fading in with the others. The menu keeps its height: every entry is
  a little smaller and the rows a little closer.
- **A pause button in the settings window's title row**, in symbols rather than words:
  two bars while the flight runs, a triangle while the window holds it. It holds the
  flight for as long as the window is open; closing the window lets the flight run again,
  and only KSP's own pause menu keeps it paused. In flight only, and the choice is kept,
  so the next opening holds the flight again.
- **ReDefinition's toolbar button goes first.** Only its own entry is moved, in what the
  launcher shows and in the list the launcher keeps; no other button is read or moved,
  and a failure leaves the toolbar as it was.
- The proxy writes a line about memory with every frame time report: what the game
  holds, what Windows has left, how much the game may still commit against its own
  limit, and its video memory against the driver's budget. Once at the start it also
  says whether a job object caps what the game may commit, which is below Windows' own
  limit where something sets one.
- The toolbar button has a tooltip, *ReDefinition - Settings*, the kind KSP's own
  buttons show.
- **A toolbar icon of its own.** A bank of sliders in the stock toolbar's colours, which
  is what the button opens: every graphics mod's settings on one panel. It ships as
  `GameData/ReDefinition/Icons/ReDefinitionIcon.png`; where that file is missing, the
  icon ReDefinition draws at run time stands in. `tools/make_icon.py` draws both it and
  the picture for a forum post or a mod site.
- A switch *Hook probe, into the log* in the diagnostics window's *Debug* tab registers a
  handler on each of the three places a mod can add to the frame, and writes two lines per
  scene for each: which camera, which textures, and the frame's state. It draws nothing.
  Nothing in ReDefinition uses those hooks for its own work, so this is what walks their
  path in a normal game. All three were seen running in flight, with the state a handler
  reads matching the frame it runs in.
- **A mod installed after you chose a profile is set up by that profile**, at the next
  start, without pressing *Apply*. The same where a mod's build changes, as it does when
  Volumetric Clouds brings its own EVE and Scatterer. Only those mods are set, so what
  you changed in the others stays.
- **Which mods keep their toolbar button is now yours to pick.** *Mods / Toolbar* has
  *Hide all from toolbar* and, under *Per mod*, one switch per mod, folded out like the
  *Keys* tab's sections. A mod switched off keeps its button, and its settings stay in
  ReDefinition's window. The main menu's panels offer the same switches. The choice is
  kept in `bundled.cfg` as `buttonsKept`, so a mod installed later follows the rule
  rather than an old file.
- `tools/checks/check_bundled_mods.ps1` reports a check it cannot ask, because the mod that
  answers it is not installed, as a note rather than a failure. The same for
  ToolbarControl.

## 0.1.2 -- 2026-09-20

### Fixed

- A setting a mod's own behaviour answers for was reported as missing its `default`
  even where it had one: the default was judged before the registration had read it.
  Every such setting said so in `KSP.log`, the example mod's two among them.
- A member path with an indexer, `MyMod.ModSettings.I[strength]`, counts for `needs`
  and for a `BUILD`'s `has` as it does for a `member`. It was read as a member named
  `I[strength]`, which is nowhere, so `needs` dropped the whole mod and `has` never
  told the build.
- A list of choices a requirement narrows keeps its labels beside their values, also
  where the values come from the running mod and the labels from the registration.

### Changed

- **KSP 1.12.0 to 1.12.99** in the version file, instead of 1.12.5 alone, so CKAN and
  KSP-AVC offer ReDefinition on every 1.12 release. The interfaces it uses are the same
  in all of them.
- Two texts in the window: the mode tooltip said 1.3x where the upscaler renders at
  1.2x, and the diagnostics button is *Show other mods' state* instead of *Show host
  stack*.
- `ReDefinitionProxy.ini` describes `fgVSync` as it works: V-Sync for as long as frame
  generation is switched on, not only on the frames it interpolates, and a sync interval
  of 0 becomes 1 while a higher one stays.

### For mod authors

- **A mod can answer for its own settings.** `behaviour = MyMod.SettingsBridge` in a
  registration names a type in your own mod. ReDefinition finds `Read` and `Write` on it
  by name, and `Ready`, `Save`, `Choices` and `Version` where you offer them, so a
  setting no member path reaches needs no code in ReDefinition and no reference to it.
  It answers for settings, not for key bindings: a `KEY` block without a member stays
  ReDefinition's to keep.
- A registration that says `saving = InModFiles` with no way to save is reported instead
  of silently losing the value, and a setting such a behaviour answers for is reported
  where it has no `default`.

### Documentation

- The pages are rebuilt around who reads them: a glossary, a first setting in fifteen
  minutes, the format key by key, and the pages for players, mod authors and development
  apart. Every claim was held against the code.
- GitHub issue forms for a bug report, an idea, and a mod whose settings should join the
  window.

## 0.1.1 -- 2026-09-18

### Fixed

- **A crash while a scene loads with frame generation on.** The frame generation
  proxy's motion vector check could end the game with `0xc0000409` when the motion
  between two frames was large, as when a flight loads. No line the proxy writes to
  its log can end the game any more.
- The report of which skinned renderers draw their own motion vectors could stop the
  upscaler's work for a frame where a renderer's material went with the scene, and
  was written again at every rebuild of the upscaler instead of once per scene.

### New

- **Key bindings in the settings window.** A *Keys* tab holds every binding in one
  place: ReDefinition's own hotkeys, the bundled mods' and KSP's own.
  - A row takes a key when clicked and pressed; *Ctrl*, *Alt* and *Shift* beside it
    switch a modifier through left, right and none. Escape cancels, *x* clears.
  - Sections: *Mods* for ReDefinition's and the mods' keys, and KSP's groups
    (*Flight*, *EVA*, *Editor*, *Camera*, *Map and vessels*, *General*), each folded
    until opened. A search field filters every section.
  - Two bindings on the same key are shown in yellow only where they count in the same
    situation -- B for the brakes and B for boarding on EVA are no conflict.
  - *Reset to defaults* puts the bindings back as well, KSP's to what KSP ships.
  - ReDefinition's hotkeys are set there, one for the settings window itself among
    them. None is bound at first: KSP's own bindings fire whatever modifiers are
    held, so the former `RightCtrl`+`RightShift`+`U`, `K` and `N` also switched the
    lights and moved the vessel on RCS.
  - Scatterer's and Deferred's window keys are bundled.
  - While the search field has the keyboard, no key reaches the game.
- **A notice for what a mod's update leaves out.** Where a new version of a bundled mod
  lacks a setting the window shows, saves one that no registration names, or cannot be
  bundled at all, the tab *Mods and toolbar* is marked *(!)* and names it, with a button
  to the mod's own window. A new version that changes none of this is not mentioned.

### For mod authors

- **Renamed config nodes:** a registration is `MOD_SETTINGS` (was `REDEFINITION_MOD`),
  a graphics profile `GRAPHICS_PROFILE` (was `REDEFINITION_PROFILE`). The old names are
  not read any more.
- **Key bindings in a registration:** a `KEY` block beside the `SETTING` blocks, with
  `member`, `modifier1`, `modifier2`, `modifiers = any|all` and `group` for the section
  it stands in. Without a member ReDefinition keeps the binding.
- **`ReDefinition.Api.Keys`:** `Binding`, `Pressed`, `Held` and `Released` for a binding
  by its key. The interface's version is 2; the wrapper `ReDefinitionApi.cs` and the
  example mod carry it.
- `docs/modders/registering-a-mod.md` explains what a mod's update shows the player.

## 0.1.0 -- 2026-09-17

First release.

- One settings window for KSP's and the supported mods' settings, with graphics profiles
  from Low to Max, compatible settings kept in every profile, *Reset to defaults* and
  *Restore settings from before ReDefinition*.
- Supported mods: Scatterer, EVE Redux with its volumetric clouds, Parallax Continued,
  Deferred, Firefly, Waterfall, Distant Object Enhancement, TUFX. Any mod can join with a
  config file.
- Upscaling and antialiasing with FSR 3, NVIDIA DLSS and AMD's FSR DLL.
- Frame generation with FSR 3 and NVIDIA DLSS, through Direct3D 12 on top of the game.
- An interface for other mods: the frame's state, history resets, hooks for motion
  vectors and overlays, and compute passes on the Direct3D 12 device.
