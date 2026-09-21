# Registration reference

**For:** mod and visual pack authors writing a `MOD_SETTINGS` file.
**You need:** a registration you are writing, and
[registering-a-mod.md](registering-a-mod.md) for what each block is good for.
**You get:** every key of the format, what it takes, and what ReDefinition does without
it.

This lists every key ReDefinition's reader accepts. For a first registration, start
with [getting-started.md](getting-started.md).

## The file

A `.cfg` file anywhere in your mod's folder under `GameData`, holding one
`MOD_SETTINGS` node per mod. A registration of the same `name` from any other folder
takes precedence over ReDefinition's own, and the log names the file that counts. Two
registrations of the same name outside ReDefinition's folder are reported, and the
later one counts.

A value that is left empty counts as no value at all. The spaces around a value are
trimmed, and commas and backslashes are kept, so a comma inside a choice is written
`\,`. As in every KSP config file, a value holds no `//` and no brace, which is why a
note can stand beside one: KSP cuts the line at the `//`.

Titles, tooltips and labels may be localisation tags, KSP's own `#autoLOC_...` or your
mod's `#LOC_...`. The window shows them in the player's language, through KSP's
`Localizer`.

## MOD_SETTINGS

| Key | Needed | Left out | What it does |
|---|---|---|---|
| `name` | yes | -- | Your mod's id: lower-case letters, digits and `_`. Settings are kept, and profiles name them, as `<name>.<setting>`. Never change it once released. |
| `detect` | yes | -- | The full name of a type that is loaded only with your mod. Where that type is not loaded, the registration does nothing and says nothing. If an assembly from `GameData` named as `button`, or as the type's first namespace, is loaded without that type, the mod is left out as a whole with the reason in the log. |
| `title` | no | `name` | Your mod's name as players know it. |
| `needs` | no | -- | Member paths, separated by commas, that your settings cannot be trusted without. A build without one leaves the mod out as a whole. |
| `version` | no | -- | The version the defaults were read from. If the installed one differs, the log says so and the values are still used. |
| `save` | no | -- | A member path to the method without parameters that saves your settings, as your own window saves them. |
| `ready` | no | -- | A member path that holds nothing, or `False`, while your mod cannot take values yet. Until it holds something, ReDefinition reads nothing from your mod and keeps what is set for it. |
| `saving` | no | `InModFiles` with `save`, else `AtEveryStart` | `InModFiles`: your mod saves the value. `PerSave`: your mod keeps it per save, and ReDefinition sets its choice into every save as it loads. `AtEveryStart`: your mod keeps nothing, and ReDefinition sets the value at every start. |
| `behaviour` | no | -- | Code that answers for settings a member path cannot reach: a type of your own with its namespace, or one of ReDefinition's own names. See [Behaviours](#behaviours). |
| `window` | no | -- | `Namespace.Type.Method` that draws your IMGUI settings window. It gets a close button while your toolbar button is hidden. |
| `button` | no | -- | The name of the assembly your toolbar button's click handler lives in. ReDefinition hides that button while your settings are bundled, and opens your window through it. Only together with `window`. |
| `toolbarControl` | no | -- | The namespace your button registers with in ToolbarControl, where you make it that way. Like `button`, only together with `window`. |
| `tab` | no | no *Advanced* button | Where your *Advanced* button goes. Where your settings go is each setting's own `row`: `Gameplay`, `Audio`, `Display`, `General` (titled *Upscaling / Quality*), `ShadowsAndReflections`, `Planets`, `Effects`, `Keys`, `Axes` or `Devices`. `Keys` is taken too but draws no *Advanced* row, and any other name is reported and ignored. A tab without rows gets no *Advanced* row either. |
| `direct` | no | `False` | `True`: while the player has the bundling off, your rows are set straight into your mod, as your own window sets them, instead of being locked. KSP's registration says so, since ReDefinition's window can stand in for KSP's own screen. |

## SETTING

| Key | Needed | Left out | What it does |
|---|---|---|---|
| `name` | yes | -- | The second part of the setting's key. Never change it once released. |
| `member` | yes, unless `leftOut` or a `behaviour` answers for it | -- | Where the value lives: a [member path](#member-paths). |
| `default` | recommended, and needed for a setting with no `member` that your own behaviour answers for | the reset and the profiles leave the setting as it is; a setting your own behaviour answers for gets no row, and the log says so | The value your release ships, as a config file writes it: `True`, `0.5`, `High`. *Reset* sets it, and the profiles start from it. |
| `title` | no | `name` | The row's name. |
| `tooltip` | no | -- | What the setting does. `\n` starts a new line. |
| `kind` | no | `Other` | `Quality`: it costs frame time, and the profiles set it. `Taste`: how the game looks, which no profile touches. `Other`: interface, debugging, compatibility. |
| `row` | no | not shown | The tab its row is in: `Gameplay`, `Audio`, `Display`, `General` (titled *Upscaling / Quality*), `ShadowsAndReflections`, `Planets`, `Effects`, `Keys`, `Axes` or `Devices`. |
| `section` | no | -- | A heading the row stands under within its tab. A heading is drawn before the first row of each run of rows with the same `section`. |
| `order` | no | after the numbered rows | A number placing the row among its tab's rows, those of other mods included. Rows of the same order stand by their mods' titles, and within one mod in the order its registration lists them. |
| `takesEffect` | no | `NextScene` | `Live`, `NextScene` or `Restart`. The window tells the player. |
| `min`, `max` | no | -- | A number with both is a slider. |
| `whole` | no | `True` for an `int`, else `False` | Whether the slider snaps to whole numbers. |
| `percent` | no | `False` | `True`: the slider's value, from 0 to 1, is shown as a percentage. Only with `min` and `max`. |
| `choices` | no | an enum's names | A list to choose from, separated by commas. `\,` is a comma inside a choice. |
| `labels` | no | the choices | What the list shows for each choice, as many as there are choices. |
| `invert` | no | `False` | For a switch your mod stores the other way round, such as `disable_...`. The row shows it the right way. |
| `optional` | no | -- | A reason. If a build lacks the member, only this setting is left out, with the reason in the log. `True` leaves it out without a word. |
| `required` | no | `False` | `True`: where a build lacks the member, the mod is left out as a whole. Not together with `optional`. |
| `leftOut` | no | -- | A reason this setting is never bundled although your mod has it. The setting is named, so the inventory stays complete, and no `member` is needed. |
| `after` | no | -- | A member path to a method without parameters, called once at the end of the frame after a change. If a build lacks it, a `Live` setting takes effect from the next scene on. |
| `shaderGlobal` | no | -- | The name of a global shader float that takes the value too. For a number, and not with `invert`. |
| `perSave` | no | as the mod's `saving` | `False` for a setting your mod keeps for the game as a whole while the others are per save. |
| `leftOutWith` | no | -- | The `name` of another registered mod. While that one is loaded, this setting is left out: its own code holds the setting. |
| `rowUnless` | no | -- | The `name` of another registered mod. While that one is bundled, the row is not shown and the setting is still kept. |
| `behaviour` | no | the mod's | Code for this setting alone. See [Behaviours](#behaviours). |
| `bundled` | no | `True` | `False`: the setting is never bundled. It is set straight into your mod whether the bundling is on or off, no profile sets it, `bundled.cfg` never keeps it, and *Restore settings from before ReDefinition* leaves it. *Reset* still sets its `default`. For an interface or input setting that has nothing to do with the graphics. |

Without `choices`, `min` and `max`, the row follows the member's type: a switch for a
`bool`, a list of names for an `enum`. An `int`, `float`, `double`, `string` or
`Vector3`, written `x,y,z`, is kept, reset and set by profiles, but gets no row. A
member of any other type cannot be set from a config file's text and is left out, with
the reason in the log. So is a setting whose `default` is no value of its member's
type.

## KEY

A key binding. It is in the *Keys* tab beside ReDefinition's own bindings, the
other mods' and KSP's. The player sets one key and up to two modifiers, each left or
right.

| Key | Needed | Left out | What it does |
|---|---|---|---|
| `name` | yes | -- | As a setting's: the second part of the binding's key. |
| `member` | no | ReDefinition keeps the binding | Where your mod keeps the key: a `KeyCode`, or its name as text. Without it, ReDefinition keeps the binding and your mod asks `ReDefinition.Api.Keys` whether it is pressed. A mod with `save`, or with `saving` other than `AtEveryStart`, must give a member unless a `behaviour` is named for the mod or for the binding: otherwise the binding is left out, with the reason in the log. |
| `modifier1`, `modifier2` | no | -- | Where your mod keeps the modifiers, when it keeps them apart from the key. |
| `modifiers` | no | `any` | `any`: your mod takes either modifier member. `all`: it asks for both at once, and one modifier then goes into both. |
| `group` | no | `Mods` | The section of the *Keys* tab it is in: one of KSP's, or a name of your own. KSP's are `Flight`, `EVA`, `Editor`, `Camera`, `Map and vessels` and `General`. |
| `row` | no | `Keys` | Another tab, where a binding belongs beside a feature's rows. |
| `title`, `tooltip`, `default`, `order`, `takesEffect`, `optional`, `required`, `leftOut`, `behaviour`, `perSave`, `after`, `leftOutWith`, `rowUnless` | no | -- | As for a setting. `default` is written as the window shows it: `LeftAlt+F10`, `F11`, `None`. |

How many modifiers a row offers follows the members: none beside a `KeyCode` alone, one
beside `modifier1`, two beside both or where the whole combination is text.

A binding in a group of KSP's counts where that group counts when two bindings are
compared. A binding under *Mods*, or in a section of your own, counts everywhere.

A binding can hold any key of the keyboard, and the mouse from its third button on. The
left and right mouse buttons are the game's own. KSP's own bindings hold one key and no
modifier, and they fire on that key whatever modifiers are held.

Which default to give a binding: [registering-a-mod.md](registering-a-mod.md).

No graphics profile sets a binding. *Reset* puts it back to `default`, and
*Restore settings from before ReDefinition* to what your mod had.

## Member paths

A member path is written as in C#, from the full name of a type:

| Path | Means |
|---|---|
| `MyMod.Settings.enableLights` | a static field or property |
| `MyMod.Settings.Instance.lights.enabled` | through a static instance to its fields |
| `MyMod.SettingsAddon.settings.enabled` | `SettingsAddon` is a `MonoBehaviour`: the one in the scene is used |
| `MyMod.Loader.global.terrain.detail` | a struct on the way is written back whole |
| `MyMod.ModSettings.I[strength]` | through an indexer with a string key |
| `MyMod.Loader.global.SaveSettings` | a method without parameters, for `save` and `after` |

The type is the longest leading part of the path that names a loaded type. Names are
looked up only in assemblies from your mod's folder, the folder under `GameData` that
`detect` was loaded from. A `detect` type outside such a folder reaches its own
assembly only. The same holds for `window`, `needs`, a `BUILD`'s `has` and a
`behaviour` of your own. Public and non-public members both work.

## BUILD and DEFAULTS

Where builds of your mod differ, in members or in the values they ship, tell them apart
by a member one of them has:

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

The first `BUILD` whose member exists is the one installed. Without a match, the build
has no name.

A `DEFAULTS` block's values take precedence over the settings' own `default`. The
blocks without `build` come first, then the blocks for the installed build, each in
their order, a
later value over an earlier one. A block for a build no `BUILD` names is reported,
unless the registration has no `BUILD` at all and a behaviour tells the build.

## REQUIRES

What your mod needs of another setting, while your mod is loaded, whether or not
ReDefinition bundles its settings:

```
REQUIRES
{
    setting = ksp.terrainDetail
    highest = True
    lock = True
    reason = MyMod needs KSP's highest terrain detail.
}
```

While *Bundle other mods here* is on, ReDefinition keeps that setting as you require it
against a profile, the reset and the player. It corrects the value when something else
changes it, and shows your reason once per run.

| Key | Needed | Left out | What it does |
|---|---|---|---|
| `setting` | yes | -- | The key of the setting you require, as `<mod>.<setting>`. |
| one test | yes | -- | `equals`, `atMost`, `atLeast`, `highest` or `check`, from the table below. |
| `fix` | with `check` | -- | The value that satisfies the check. |
| `reason` | recommended | -- | The sentence the player reads on the row and in the log. |
| `lock` | no | `False` | `True` locks the row. Otherwise a row with a list offers only the values allowed, and any other row takes what the player sets and has it corrected as it is applied. |
| `whenInstalled` | no | -- | The `name` of another registered mod that must be loaded too. |

Exactly one test per block:

| Test | Passes |
|---|---|
| `equals` | this value |
| `atMost`, `atLeast` | a number no higher, or no lower. A value that is no number passes |
| `highest = True` | the last of the setting's choices by its place in the list. A name that also appears earlier never counts as the last |
| `check` | a check ReDefinition's behaviour for that setting's mod answers, with `fix` for the value that satisfies it |

The checks ReDefinition answers:

| Check | On | Passes |
|---|---|---|
| `TufxAmbientOcclusion` | a TUFX scene profile | a profile TUFX renders with ambient occlusion |
| `DlssFrameGenerationEveryRefresh` | `ksp.SYNC_VBL` | V-Sync off or every refresh, while DLSS frame generation runs |
| `DlssFrameGenerationVSync` | `ksp.SYNC_VBL` | V-Sync off, while DLSS frame generation runs in a build without V-Sync support |

A check no behaviour answers is reported once, and then passes every value. Where
several mods require the same setting, a value passes only what all of them allow.

## PROFILE and ALL_PROFILES

```
PROFILE
{
    name = low
    fancyEffects = False
}
ALL_PROFILES
{
    useTemporalAntiAliasing = False
}
```

A `PROFILE` block sets what that profile sets for your mod. It takes quality settings
only; any other setting in it is reported and left out. A setting no block of a profile
names keeps its default there.

An `ALL_PROFILES` block holds what every profile sets, of any kind: what your mod needs
changed to run with the upscaler. Its values take precedence over the `PROFILE`
blocks', and what installed mods require takes precedence over both.

`build` limits either block to one of your builds. The blocks are taken as the
`DEFAULTS` blocks are: those for every build first, then those for the installed build.

The profiles themselves are in `GameData/ReDefinition/Profiles`. A block for a
profile that is not there is reported and does nothing.

A player who installs your mod after choosing a profile gets these values at the next
start, without pressing *Apply*, and so does one whose installed build of your mod
changes ([player/graphics-profiles.md](../player/graphics-profiles.md)).

A visual pack changes these values with ModuleManager. `:HAS[#build[volumetric]]` picks
the block for one build, `:HAS[~build[]]` the one for every build:

```
@MOD_SETTINGS[parallax]:NEEDS[ReDefinition]
{
    @PROFILE[medium]:HAS[~build[]] { @densityMultiplier = 0.9 }
}
```

## Behaviours

`behaviour`, on the mod or on one setting, names the code that answers for settings a
member path cannot reach. It takes two kinds of name.

**A type of your own**, written with its namespace: `behaviour = MyMod.SettingsBridge`.
ReDefinition finds these members on it by name, so your mod needs no reference to
ReDefinition:

| Member | Needed | What it does |
|---|---|---|
| `string Read(string name)` | yes | The value as text. `null` says the value cannot be read now. |
| `void Write(string name, string value)` | yes | Takes the value. May return `bool`: `false` is a value your mod refuses, which ReDefinition reports and keeps. |
| `bool Ready` | no | Whether your mod can take values now, as `ready` says it for a path. Given for the mod, it gates every setting of your mod, member paths included; given for one setting, that setting alone. |
| `void Save()` | no | Saves as your own window saves. Called before a `save` the registration names. |
| `string[] Choices(string name)` | no | A list only the running mod knows. `null` leaves the setting the control its value's type gives. |
| `string Version` | no | Your mod's version, where the registration cannot tell it. |

`Ready` and `Version` may be a property, a field or a method without parameters. The
other four are methods, with the signatures above.

ReDefinition calls static members on the type. For members that are not static, it uses
a static `Instance` of that type where your type has one, and otherwise makes the type
once through its parameterless constructor. It does not reach a type derived from
`UnityEngine.Component` that has no static `Instance`, and the log says so.

Every setting without a `member` goes through it, by its `name`. A setting with a
`member` keeps its path, and a `KEY` block without a member stays ReDefinition's to
keep.

**One of ReDefinition's own names**, without a dot: `Tufx`, `Scatterer`, `Eve`, `Ksp`,
`DistantObject`, `Firefly`, `ParallaxScatter`. These are the behaviours ReDefinition
brings for the mods it ships registrations for. A name ReDefinition does not have leaves
the mod out as a whole where the mod names it, and leaves out that setting alone where a
setting names it.

## What is reported

Every problem goes into `KSP.log` with the prefix `[ReDefinition]`, naming your mod and
the setting. The same list is in the game, in the diagnostics window under *Debug*,
*Show mod registrations*.

A value ReDefinition cannot read is ignored, and that key's default stands.

ReDefinition leaves a setting out, and keeps the rest of your mod, where the setting has
no `name`, or no `member` while neither `leftOut` nor a `behaviour` answers for it, or
where its member is not there.

Your mod is left out as a whole where one of these is missing: a `required` setting's
member, one of `needs`, the `save` method, the `ready` member, `detect` while your mod's
assembly is loaded, or the type or name the mod's own `behaviour` gives. A registration
without `name` or `detect` is skipped.

A key given twice counts with its last value, and that is reported: it is usually a
patch that meant to change the first.

If a build of your mod lacks a setting that has a `row`, or saves a `[Persistent]`
field of a settings object your registration reaches that no registration names, the
player's window says so under *Mods / Toolbar*.
