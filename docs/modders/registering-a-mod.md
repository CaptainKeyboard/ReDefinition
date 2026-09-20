# Registering a mod

**For:** mod and visual pack authors whose first registration already works.
**You need:** a registration that shows one setting
([getting-started.md](getting-started.md)).
**You get:** the job you have in front of you, one section each.

Every key these sections use is in
[registration-reference.md](registration-reference.md).

## Put a setting in the right place

`row` names the tab, `order` the place among that tab's rows, and `kind` says whether
the profiles may set it:

```
SETTING
{
    name = cloudDetail
    member = MyMod.Settings.cloudDetail
    title = Cloud detail
    tooltip = How finely the clouds are drawn.
    row = Planets
    order = 30
    kind = Quality
    takesEffect = NextScene
    min = 1
    max = 4
    whole = True
    default = 2
}
```

Give a row only to what a player looks for in a graphics menu. Everything else needs
none. Give it a `SETTING` block without a `row`, and it is still kept, reset and set by
profiles.

`takesEffect` tells the player when the change arrives: `Live`, `NextScene` or
`Restart`. If a `Live` value needs a call to take hold, name that call with `after`.

## Save the value where your mod saves it

`save` names the method your own window saves with. ReDefinition calls it once after a
batch of changes, so your mod's file and ReDefinition's window always agree:

```
save = MyMod.Settings.Save
saving = InModFiles
```

`saving` says how a value lasts: `InModFiles`, `PerSave` or `AtEveryStart`. The
[glossary](../glossary.md) defines the three. With `save` given, `InModFiles` is the
default, and without it `AtEveryStart`.

With `PerSave`, the player's choice counts for the game rather than for one save.
ReDefinition sets it into every save as that save loads. A change made in your own
window becomes the new choice only where ReDefinition brings a hook for your mod, as it
does for TUFX and Distant Object.

## Wait until your mod can take values

A mod that builds its settings in flight has nothing to read in the main menu. `ready`
names a member that holds nothing, or `False`, until then:

```
ready = MyMod.Flight.Settings
```

Until it holds something, ReDefinition reads nothing from your mod and keeps what the
player sets. It hands the values over at the first chance, and asks again on its
two-second tick.

Name a member that is really there. If the build lacks it, or where `ready` names a
method, ReDefinition leaves your whole mod out and says so in the log.

## Offer a key binding

A `KEY` block is in the *Keys* tab, beside ReDefinition's own bindings, the other
mods' and KSP's. If your mod keeps the key itself, name the member:

```
KEY
{
    name = window
    title = MyMod's window
    member = MyMod.Settings.windowKey
    modifier1 = MyMod.Settings.windowModifier
    default = None
}
```

If your mod keeps no key of its own, leave `member` out. ReDefinition then keeps the
binding for you, and your mod asks whether it is pressed:

```csharp
if (ReDefinitionApi.KeyPressed("mymod.window")) ToggleWindow();
```

That call comes from the wrapper,
[examples/ReDefinitionApi.cs](examples/ReDefinitionApi.cs), which
[shared-foundation.md](shared-foundation.md) describes.

A binding without a member works only where ReDefinition keeps your settings anyway.
If your mod has `save`, or `saving = InModFiles` or `PerSave`, give the binding a
member, unless the mod or the binding names a `behaviour`. Without either it is left
out, with the reason in the log.

Give a binding `default = None` unless you know the key is free. KSP's own bindings
fire on their key whatever modifiers are held, and KSP binds nearly every letter, digit
and function key.

## Open your own window from ReDefinition's

Your window keeps everything the rows leave out. Name the method that draws it, and the
assembly your toolbar button's handler lives in:

```
window = MyMod.SettingsWindow.DrawWindow
button = MyMod
tab = Effects
```

If your button is made through ToolbarControl, write `toolbarControl = MyMod` in
place of `button`.

ReDefinition then hides your toolbar button while your settings are bundled, and puts
an *Advanced* button for your window into the tab `tab` names. Your window gets a close
button while its own button is hidden. With the bundling switched off, your button
comes back and nothing is hidden.

All of `window`, `tab` and one of the two button keys are needed. ReDefinition opens
your window through your own toolbar button, so it shows the *Advanced* button only in
the scenes where that button is on KSP's toolbar, and only in the tab `tab` names.

## Ship different defaults for different builds

If two builds of your mod differ, tell them apart by a member only one of them has,
and give each its values:

```
BUILD
{
    name = volumetric
    has = MyMod.Settings.volumetricGodrays
}
DEFAULTS
{
    build = volumetric
    waveDetail = 256
}
```

Use `optional` for a setting only some builds have, with the reason. Use `required` for
a setting that tells a build of another shape apart: without it, the whole mod is left
out rather than half bundled.

## Require something of another mod's setting

If your mod errors or warns unless another setting stands a certain way, say so.
While the mods are bundled, ReDefinition then keeps it that way against a profile, the
reset and the player, and shows your reason once per run:

```
REQUIRES
{
    setting = ksp.terrainDetail
    highest = True
    lock = True
    reason = MyMod needs KSP's highest terrain detail.
}
```

Require only what your mod really cannot run without. The player sees the row locked,
or its values narrowed, with your sentence beside it.

## Answer for settings a member path cannot reach

Some values live behind a call, in a config node beside the running object, or anywhere
a member path cannot say. Name a type of your own for those, and give their settings no
`member`:

```
behaviour = MyMod.SettingsBridge
```

```csharp
public static class SettingsBridge
{
    public static string Read(string name) { ... }
    public static void Write(string name, string value) { ... }
    public static void Save() { ... }
}
```

ReDefinition finds those members by name, so your mod still needs no reference to it.
Only `Read` and `Write` are needed. `Save`, `Ready`, `Choices` and `Version` are
optional, and the whole contract is in
[registration-reference.md](registration-reference.md). A working one is
`SettingsBridge.cs` in [examples/ReDefinitionExample](examples/ReDefinitionExample).

## Change another mod's values from a visual pack

A pack edits a registration with ModuleManager rather than shipping a second one:

```
@MOD_SETTINGS[parallax]:NEEDS[ReDefinition]
{
    @PROFILE[medium]:HAS[~build[]] { @densityMultiplier = 0.9 }
}
```

`:HAS[~build[]]` picks the block that counts for every build, and
`:HAS[#build[volumetric]]` the one for a named build. To replace a whole registration
instead, ship a `MOD_SETTINGS` with the same `name` from your own folder. It counts
over ReDefinition's, and the log names the file that won.

## Find out why something is missing

Open the settings window, *Diagnostics*, *Debug*, *Show mod registrations*. It lists
every bundled mod with how many settings it has, every loaded mod that is not bundled
with the reason, and every problem the registrations had. `KSP.log` holds the same
lines behind `[ReDefinition]`, and the reasons for single settings as well.

A new build of your mod can lose a setting the window shows, or save a `[Persistent]`
field no registration names. The player's *Mods / Toolbar* tab then says so and points
at your own window. Name a field you leave out on purpose with `leftOut` and its
reason, and it is not reported as new.

If you contribute a registration to ReDefinition itself, its own check script reads
it against the installed mods before the game runs:
[development/building-and-testing.md](../development/building-and-testing.md).
