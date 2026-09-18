# Registering a mod with ReDefinition

ReDefinition gathers the graphics settings of many mods in one settings window,
sorted by feature, with graphics profiles and a *Reset to defaults* button. A mod
joins with a config file of its own -- no code, and no dependency: without
ReDefinition installed the game reads the file and does nothing with it.

This guide is for mod authors and visual pack authors. It is also the
specification ReDefinition's reader follows (`src/Framework/ModRegistration.cs`).

## The smallest registration

A `.cfg` file anywhere in your mod's folder under `GameData`:

```
MOD_SETTINGS
{
    name = mymod
    detect = MyMod.Settings

    SETTING
    {
        name = fancyEffects
        member = MyMod.Settings.fancyEffects
        default = True
    }
}
```

That is enough for ReDefinition to keep the setting, reset it to `True`, and put
back what it was before ReDefinition changed it. To show it in the window, give it
a `row` -- the tab it shows in -- and an `order` among that tab's rows; to let the
graphics profiles set it, give it `kind = Quality` and a `PROFILE` block for each
profile that should change it (below). Everything about your mod stands in this one
file: its settings, where they show, their defaults, what each profile sets and what
your mod requires of other settings.

ReDefinition ships registrations of its own for the mods it bundles, in
`GameData/ReDefinition/Mods`. A registration of the same `name` from any other folder
counts over ReDefinition's, whichever folder KSP reads first, and the log names the
file that counts: a mod can ship its own registration and replace ReDefinition's.
Two registrations of the same name outside ReDefinition's folder are reported, and
the later one counts; a pack that changes a registration edits it with
ModuleManager's `@` instead.

## The mod

| Key | Needed | Left out | What it does |
|---|---|---|---|
| `name` | yes | -- | Your mod's id: lower-case letters, digits and `_`. Settings are kept, and profiles name them, as `<name>.<setting>` -- **never change it** once released. |
| `title` | no | `name` | Your mod's name as players know it. |
| `detect` | yes | -- | The full name of a type that is loaded only with your mod. Where that type is not loaded, your mod is not installed and the registration does nothing, without a word -- unless an assembly from `GameData` named as `button` or as the type's first namespace is loaded (`MyMod.dll` for `MyMod.Settings`): then that build of your mod lacks the type, and your mod is left out as a whole, with the reason in the log. A registration without `detect` is skipped, with a problem in the log. |
| `needs` | no | -- | Member paths, separated by commas, that your settings cannot be trusted without -- the groups of a settings object, say. Where a build of your mod lacks one, your mod is left out as a whole, with the reason in the log. |
| `version` | no | -- | The version of your mod the defaults were read from. Where the installed one differs, the log says so. |
| `save` | no | -- | A member path to the method without parameters your mod saves its settings with, as its own window does. |
| `ready` | no | -- | A member path that holds nothing, or `False`, while your mod cannot take values yet -- settings your mod loads only in flight, say. Until it holds something, ReDefinition reads nothing from your mod, and what is set for it waits in ReDefinition. Without it, your settings count as there whenever their members are. |
| `saving` | no | `InModFiles` where `save` is given, else `AtEveryStart` | How a value set in ReDefinition lasts: `InModFiles` -- your mod saves it; `PerSave` -- your mod keeps it per save, and ReDefinition sets its choice into every save as it loads; `AtEveryStart` -- your mod keeps nothing, and ReDefinition sets it at every start. |
| `window` | no | -- | `Namespace.Type.Method` that draws your own IMGUI settings window (`GUILayout.Window` or `GUI.Window`): the window gets an *Advanced* button for it, and a close button while your toolbar button is hidden. |
| `button` | no | -- | The name of the assembly your toolbar button's click handler lives in: that button is hidden while your settings are in ReDefinition's window. Only together with `window`, whose method the player then reaches through the *Advanced* button; a `button` without `window` is reported, and the button stays. |
| `toolbarControl` | no | -- | The namespace your button registers with in ToolbarControl, where you make it that way. Like `button`, only together with `window`. |
| `tab` | no | -- | Where your *Advanced* button goes: `General`, `ShadowsAndReflections`, `Planets` or `Effects`. |

## A setting

| Key | Needed | Left out | What it does |
|---|---|---|---|
| `name` | yes | -- | The second part of the setting's key -- **never change it** either. |
| `member` | yes, but with `leftOut` | -- | Where the value lives: a member path (below). |
| `title` | no | `name` | The row's name. |
| `tooltip` | no | -- | What the setting does, for the row's tooltip; `\n` starts a new line. |
| `default` | recommended | -- | The value your release ships, as a config file writes it (`True`, `0.5`, `High`). *Reset to defaults* sets it, and the graphics profiles start from it. |
| `kind` | no | `Other` | `Quality`: it costs frame time, and the profiles set it. `Taste`: the look a visual pack chooses -- no profile touches it. `Other`: anything else -- interface, debugging, compatibility. |
| `row` | no | not shown | The tab its row is shown in: `General`, `ShadowsAndReflections`, `Planets` or `Effects`. Show only what a player looks for in a game's graphics menu; the rest stays reachable through your own window. |
| `order` | no | after the numbered rows | A number placing the row among the rows of its tab, those of other mods too. Rows of the same order stand by their mods' titles, then as your registration lists them. |
| `takesEffect` | no | `NextScene` | `Live`, `NextScene` or `Restart` -- the window tells the player. |
| `min`, `max` | no | -- | A number with both is a slider. |
| `whole` | no | `True` for an `int`, else `False` | Whether the slider snaps to whole numbers. |
| `choices` | no | an enum's names | A list to choose from, separated by commas; `\,` is a comma inside a choice. |
| `labels` | no | the choices | What the list shows for each choice, as many as there are choices. |
| `invert` | no | `False` | For a switch your mod stores the other way round (`disable_...`): the row shows it the right way. |
| `optional` | no | -- | A reason. Where a build of your mod lacks the member, only this setting is left out, with the reason in the log. `True` leaves it out without a word -- for a member only some builds have; `False` is the same as no `optional`. |
| `required` | no | `False` | `True`: where a build of your mod lacks the member, your mod is left out as a whole -- for the settings that tell a build of another shape. Not together with `optional`. |
| `leftOut` | no | -- | A reason your mod has this setting and it is still not to be bundled -- it would work against something. The reason is in the log, and the setting is named, so the inventory stays complete; no `member` is needed. |
| `after` | no | -- | A member path to a method without parameters, called once at the end of the frame after a change -- to apply it as your own window does. Where a build lacks the method, a `Live` setting takes effect from the next scene on instead; a call that fails is logged. |
| `shaderGlobal` | no | -- | The name of a global shader float that takes the value too -- for a number, and not with `invert`. |
| `perSave` | no | as the mod's `saving` | `False` for a setting your mod keeps for the game as a whole while the others are per save. |
| `leftOutWith` | no | -- | The `name` of another registered mod: while that one is loaded -- bundled or not -- this setting is left out: its own code holds the setting. |
| `rowUnless` | no | -- | The `name` of another registered mod: while that one is installed the row is not shown, the setting still is kept. |

The mod's `title` and a setting's `title`, `tooltip` and `labels` may be
localization tags -- KSP's own `#autoLOC_...` or your mod's `#LOC_...` from its
`Localization` files: the window shows them in the player's language.

A value is what stands after the first `=` of its line, commas and backslashes
included. Two things a config file never keeps in a value: a line ends at `//`,
which starts a comment, and `{` or `}` open or close a node wherever they stand.
Write a link without its `//`, and no braces in a tooltip.

Without `choices`, `min` and `max`, the row follows the member's type: a switch
for a `bool`, a list of names for an `enum`. An `int`, `float`, `double`,
`string` or `Vector3` (written `x,y,z`) is kept, reset and set by profiles, but
gets no row. A member of any other type cannot be set from a config file's text
and is left out, with the reason in the log -- as is a setting whose `default` is
no value of its member's type.

## A key binding

A `KEY` block beside the `SETTING` blocks is a key binding: it stands in the
settings window's *Keys* tab, beside ReDefinition's own bindings, the other mods'
and KSP's, and the player sets it there: one key, and up to two modifiers, each
left or right.

```
KEY
{
    name = window
    title = MyMod's window
    tooltip = Opens MyMod's own window.
    default = LeftAlt+M
    takesEffect = Live
}
```

| Key | Needed | Left out | What it does |
|---|---|---|---|
| `name` | yes | -- | As a setting's: the second part of the binding's key, `<mod>.<name>`. |
| `member` | no | ReDefinition keeps the binding | Where your mod keeps the key: a `KeyCode`, or its name as text. Without it, ReDefinition keeps the binding and your mod asks `ReDefinition.Api.Keys` whether it is pressed ([shared-foundation.md](shared-foundation.md)). |
| `modifier1`, `modifier2` | no | -- | Where your mod keeps the modifiers, when it keeps them apart from the key. |
| `modifiers` | no | `any` | `any`: your mod takes either modifier member; `all`: it asks for both at once, and one modifier then goes into both. |
| `title`, `tooltip`, `default`, `order`, `takesEffect`, `optional`, `required`, `leftOut`, `behaviour`, `perSave` | no | -- | As for a setting. `default` is written as the window shows it: `LeftAlt+F10`, `F11`, `None`. |
| `row` | no | `Keys` | Another tab, where a binding belongs beside a feature's rows. |

A binding is never set by a graphics profile: profiles set quality, and a key is
the player's. *Reset to defaults* puts it back to `default`, and *Restore settings
from before ReDefinition* to what your mod had.

The keys a binding can hold: the keyboard, and the mouse from its third button on
-- the left and right buttons are the game's own. KSP's own bindings hold one key
and no modifier, as KSP keeps them.

## Member paths

A member path is written as in C#, from the full name of a type:

| Path | Means |
|---|---|
| `MyMod.Settings.enableLights` | a static field or property |
| `MyMod.Settings.Instance.lights.enabled` | through a static instance to its fields |
| `MyMod.SettingsAddon.settings.enabled` | `SettingsAddon` is a `MonoBehaviour`: the one in the scene is used |
| `MyMod.Loader.global.terrain.detail` | a struct on the way is written back whole |
| `MyMod.ModSettings.I[strength]` | through an indexer with a string key |
| `MyMod.Loader.global.SaveSettings` | a method without parameters -- for `save` and `after` |

The type is the longest leading part of the path that names a loaded type. Names
are looked up only in assemblies from your mod's folder -- the folder under
`GameData` that `detect` was loaded from; a `detect` type outside such a folder
reaches its own assembly only. The same holds for `window`, `needs` and a
`BUILD`'s `has`. Public and non-public members both work; ReDefinition reaches
your mod through reflection only.

## Builds and their defaults

Where builds of your mod differ -- in members or in the values they ship -- tell
them apart by a member one of them has:

```
BUILD
{
    name = volumetric
    has = MyMod.Settings.volumetricGodrays
}
DEFAULTS
{
    build = volumetric
    version = 2.1.0.0
    waveDetail = 256
}
```

The first `BUILD` whose member exists is the one installed; without one matching,
the build has no name. A `DEFAULTS` block's values count over the settings'
`default`: the blocks without `build`, for every build, first, then the blocks for
the installed build, each in their order, a later value over an earlier one -- so a
block for a build counts over one for every build, wherever a patch adds it. A
block for a build no `BUILD` names is reported, unless the registration has no
`BUILD` and a behaviour tells the build, as TUFX's does. A block's `version`, or your mod's
own, names the version the values were read from: where the installed version
differs, the log says so, and the values are still used.

## What your mod needs of other settings

```
REQUIRES
{
    setting = ksp.terrainDetail
    highest = True
    lock = True
    reason = MyMod needs KSP's highest terrain detail.
}
```

While your mod is loaded -- bundled here or not, since it errors or warns either
way -- ReDefinition keeps the setting as you require, whatever a profile, the reset
or the player sets; puts it right when it is changed elsewhere; locks the row
(`lock = True`) or offers only the values allowed; and shows the reason once per
run. Exactly one test:

- `equals` -- this value;
- `atMost`, `atLeast` -- a number no higher, or no lower; a value that is no
  number passes;
- `highest = True` -- the last of the setting's choices, by its place in the
  list: a name that also stands earlier never counts as the last;
- `check` -- a check ReDefinition's behaviour for the setting's mod answers, with
  `fix` for the value that satisfies it. A check that mod's behaviour does not
  answer is reported once and passes every value. The checks:

  | Check | On | Passes |
  |---|---|---|
  | `TufxAmbientOcclusion` | a TUFX scene profile | a profile TUFX renders with ambient occlusion |
  | `DlssFrameGenerationEveryRefresh` | `ksp.SYNC_VBL` | V-Sync off or every refresh, while DLSS frame generation runs |
  | `DlssFrameGenerationVSync` | `ksp.SYNC_VBL` | V-Sync off, while DLSS frame generation runs in a build without V-Sync support |

`whenInstalled` names another registered mod that must be loaded too. Several
mods may require the same setting: a value passes only what all of them allow.
Require only what your mod errors or warns about otherwise.

## Graphics profiles

ReDefinition's profiles -- Low, Medium, High, Ultra and Max, and any a pack adds --
start from the defaults of the installed builds. What a profile sets for your mod
stands in your registration, a block per profile:

```
PROFILE
{
    name = low
    fancyEffects = False
}
PROFILE
{
    name = low
    build = volumetric
    waveDetail = 64
}
```

A `PROFILE` block sets quality settings only (`kind = Quality`): any other setting in
it is reported and left out. `build` limits a block to one of your builds, and the
blocks are taken as the `DEFAULTS` blocks are: those for every build first, then
those for the installed build. A setting no block of a profile names keeps its
default there. High is
the defaults: give it a block only where a default is too low for the cards High is
made for. The profiles themselves -- their names, titles and the hardware they are
for -- stand in ReDefinition's `GameData/ReDefinition/Profiles`; a block for a
profile that is not there is reported and does nothing.

What every profile sets stands in an `ALL_PROFILES` block: settings of any kind that
your mod needs changed to run with ReDefinition's upscaler -- its own temporal
antialiasing off, say:

```
ALL_PROFILES
{
    useTemporalAntiAliasing = False
}
```

`build` limits an `ALL_PROFILES` block as it does a `PROFILE` block. Its values count
over the `PROFILE` blocks' values, and what installed mods require counts over both.

A visual pack changes a mod's values for a profile with ModuleManager; a block for one
build is picked with `:HAS[#build[volumetric]]`, the one for every build with
`:HAS[~build[]]`:

```
@MOD_SETTINGS[parallax]:NEEDS[ReDefinition]
{
    @PROFILE[medium]:HAS[~build[]] { @densityMultiplier = 0.9 }
}
```

## Behaviours

Some mods keep their settings in ways a path cannot reach -- per scene, in a
config node besides the running object, behind hooks. ReDefinition handles those
of the mods it ships registrations for with behaviours, classes in ReDefinition's
own assembly, named with `behaviour =` on the mod or a setting. A registration can
name only a behaviour ReDefinition has; with any other name the mod is left out as
a whole, with the reason in the log. A mod whose settings need code of their own
to be read or set cannot be registered with a config file alone.

## When something is wrong

Every problem in a registration -- a key the reader does not know, a value it
cannot take, a member that is not there -- goes into `KSP.log` with the prefix
`[ReDefinition]`, naming your mod and the setting. A key with a problem is
ignored and its default stands; a setting without its `name` or `member`, or
whose member is not there, is left out, and the rest of your mod stays in.
Your mod is left out as a whole where a `required` setting's member, one of
`needs`, the `save` method or the `ready` member is missing, where `detect` is
missing while your mod's assembly is loaded, or where `behaviour` names none of
ReDefinition's. A registration without `name` or `detect` is skipped. A key given
twice counts with its last value, and that is reported too -- it is usually a
patch that meant to change the first.

The same shows in the game, in ReDefinition's diagnostics window (*Diagnostics* in
its settings window, then *Debug* and *Show mod registrations*): the mods bundled
with how many settings each, a mod that is loaded and not bundled with the reason,
and every problem the registrations had.

## A worked example

`docs/modders/examples/Trajectories.cfg` in ReDefinition's repository registers
Trajectories 2.4.5.4, which ReDefinition does not bundle itself. Put it into
`GameData/Trajectories` and start the game: its row shows under *Effects*, with
Trajectories' values from the first flight on. What it shows:

- Its settings are static properties of `Trajectories.Settings`, and `internal`: a
  member path reaches them all the same.
- `save` names the method its own window saves with, `Settings.Save`.
- `ready` names `Trajectories.Trajectories.Settings`, the object Trajectories makes
  only as its flight scenario loads: before that its properties read 0 and
  `Settings.Save` does nothing, so what is set in the main menu waits in
  ReDefinition until the first flight.
- The two limits of its prediction cost frame time, so they are `kind = Quality`,
  in the ranges of its own sliders; one of them has a row. Both are `required`: a
  build without them is not the one this was written for.
- Three switches of the tool are registered without a row, for *Reset to
  defaults*.
- It has no `button`, `window` or `tab`: Trajectories' window is the tool itself
  rather than a settings window, so its toolbar button stays -- and it is one of
  KSP's dialogs, not an IMGUI window that could get a close button.

## A template

`docs/modders/examples/registration-template.cfg` holds a registration with every kind of
key, each with a note beside it. Copy it into your mod's folder and delete what your
mod does not need.

## Checking a registration outside the game

ReDefinition's repository checks every registration against the mods installed on
the machine it runs on -- its own, the examples, and any it finds in `GameData`:

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_bundled_mods.ps1
```

A registration whose mod is installed must find every member it names, and each
setting's default must be a value its control takes.
