# Changelog

## Unreleased

### Fixed

- **Planets and their clouds seen from orbit no longer trail under time warp.** Their
  pixels carried only the camera's turn as motion, not the planet's rotation or the
  vessel's travel along its orbit, because the motion vectors came from the near scene's
  camera alone. The distant planets' camera now gives its own where the near scene draws
  nothing.
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

- **The pause button works in the space centre and the tracking station too**, where
  game time runs as well. There it stops Unity's time scale and gives it back through
  KSP's time warp, as KSP does when it unpauses a flight.
- **ReDefinition's toolbar button is not in the main menu any more**: the
  *ReDefinition* entry under *Settings* opens the window there.
- **The DLSS preset is gone**: DLSS runs the model its library chooses for each mode.
  **Sharpness** runs from 0 to 1.2. Each profile shows one sentence and the hardware
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
- **Every graphics profile switches KSP's own antialiasing off**, as it already did for
  Scatterer's, TUFX's and Deferred's. The upscaler does the antialiasing, and MSAA
  resolves before the capture, which takes the smoothed edges the upscaler reconstructs
  from. KSP's settings screen shows it off now, instead of a value that was overruled
  while the upscaler ran. *Restore settings from before ReDefinition* puts your choice
  back, and the upscaler still holds MSAA off while it runs.
- `tools/check_bundled_mods.ps1` reports a check it cannot ask, because the mod that
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
