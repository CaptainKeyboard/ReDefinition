# Changelog

## 0.1.1

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
