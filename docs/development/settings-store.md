# The settings store

**For:** anyone changing how ReDefinition hands the other mods their values.
**You need:** `src/Settings` open beside this page, starting with `BundledStore.cs`.
**You get:** how a value reaches its mod, where it is kept, and how it goes back.

The store sits between the settings window and the bundled mods. It hands a value to its
mod, keeps what a mod has not saved yet, and remembers what each mod held before
ReDefinition first changed it. A value set here is saved through the mod's own save
routine, as if the player had set it in the mod's own window, so both windows show the
same.

## Where the code is

| File | Holds |
|---|---|
| `src/Settings/BundledStore.cs` | the operations below |
| `src/Settings/BundledLedger.cs` | what is kept: the values, the values from before ReDefinition, what waits for a mod's save |
| `src/Settings/BundledFile.cs` | `bundled.cfg` on disk |
| `src/Settings/BundledSettings.cs` | the facade the rest of ReDefinition calls, with the game as the store's host (`IStoreHost`) |
| `src/Settings/BundledSettingsAddon.cs` | when values are handed over |

The store reaches the game only through `IStoreHost`. The tests put a stand-in in that
place and run the store without KSP: `tests/ReDefinition.Tests/BundledStoreTests.cs` and
`tests/ReDefinition.Tests/BundledFileTests.cs`.

## How a mod keeps a value

A registration says it with `saving`. If it says nothing, a mod that names a `save`
method keeps its values in its own files, and a mod that names none keeps nothing.

| `SettingsSaving` | The mod | ReDefinition |
|---|---|---|
| `InModFiles` | saves it in its files through its own routine | keeps the value until the mod has saved it, or while the mod cannot take it yet |
| `PerSave` | keeps it per save | a choice is for the game: kept, and set into every save as it loads; a change in the mod's own window to a kept choice becomes the choice |
| `AtEveryStart` | keeps nothing | kept, and set at every start and scene change |

A mod saves once after a batch of changes, not once per setting. If a mod's save
throws, the store keeps what waited for that save, and saving is tried again at the next
scene change. Which mod keeps what, and where:
[how-each-mod-keeps-its-settings.md](../reference/how-each-mod-keeps-its-settings.md).

## The operations

Each of these is a method of `BundledStore`, reached through `BundledSettings`. `Set`,
`Release`, `ResetTo`, `Correct` and `TakeFromMod` do nothing while the bundling is off.
Putting a value back works with the bundling on or off.

* **`Set`.** The player's change, from the window or from a profile. What the installed
  mods require is applied to the value first (`Requirements.Adjust`). The store then
  keeps the value, writes it to the mod where the mod can take it now, and leaves the
  saving through `SaveNow` where the caller asks, once for a whole batch of changes.
* **`Release`.** A setting a newly chosen profile leaves alone while it still holds the
  applied profile's value. The store keeps no value for it any more, and marks its value
  from before ReDefinition to be put back.
* **`ResetTo`.** The value *Reset* gives a setting. For a mod that saves it
  works as `Set` does. For a mod without a save routine it sets the running value only,
  and keeps nothing, so the mod's own config is in force again at the next start. Where such a
  mod holds the default already, nothing is written. The value from before ReDefinition is
  kept as for any other change, so a restore undoes the reset.
* **`Correct`.** A value a requirement puts right (`Requirements.Enforce`). It becomes a
  new choice where a choice for every save is kept, or where the mod keeps the value for
  the game as a whole. A per-save value with no choice kept is corrected in the loaded
  save only.
* **`TakeFromMod`.** A change made in the own window of a per-save mod, to a value the
  store keeps as the choice. The choice follows the change, through the hooks on TUFX and
  Distant Object. A value an installed mod forbids does not become the choice, and the
  kept one goes straight back into the save.
* **`ReapplyStored`.** First what is marked to be put back, then what is kept, handed to
  the mods that do not hold it yet. It runs at every scene change, on the main menu's
  tick, at camera changes, and for one mod after that mod has read its own file.
* **`ReapplyWaiting`.** The mods a value could not reach, because they could not take it
  then, asked again on the add-on's two-second tick in every scene but the main menu, where
everything is handed over instead.
* **`RestoreBackup`.** Every setting back to what its mod held before ReDefinition. It
  keeps no value afterwards, and forgets the chosen profile.
* **`SetEnabled`.** The bundling switched on hands over again what is kept. Switched off
  it drops what only `bundled.cfg` kept, and forgets the chosen profile. What the mods
  saved stays as it is.

## Values from before ReDefinition

Before a setting is first changed, the store keeps the value its mod had, once and for
good. A per-save setting is kept once per save, keyed by the save's folder
(`BundledSetting.Context`). That value goes into `bundled.cfg` before the mod gets the new
one. A mod that saves on its own could otherwise keep the new value with nothing on disk
to put back. If the file cannot be written, the mod keeps the value it has. For an
`AtEveryStart` mod, which keeps nothing in files of its own, the value goes into
`bundled.cfg` with the next save.

A restore or a release marks each kept value to be put back once, with the bundling on or
off:

* a value for the game as a whole goes back at once;
* a per-save value goes back in its save, the next time that save loads.

Once the value is in its mod, and saved where the mod saves, the store keeps it no longer.
A new choice before then unmarks it. Each value is tried once per scene load, and again at
a restore or a scene change.

A per-save value put back is a change to its save like one made in the mod's own window.
TUFX keeps it in the game, which KSP writes when it saves, so a quicksave from before
brings back what that quicksave holds. Distant Object writes the save's own file at once.

## A mod the profile has not reached

`PROFILE_APPLIED_TO` holds the mods the chosen profile was applied to, each with the
build it had. At the first scene of a run, a mod that is not in it, or one whose build
has changed, is given that profile's values: the build's defaults, the profile's blocks,
what every profile sets, and what other mods require. Only that mod
(`ProfileApplier.CatchUpNewMods`). A mod installed after a profile was chosen is set up
without the player pressing *Accept*, and what the player changed in the other mods is
left alone.

## A mod that cannot take a value yet

`BundledSetting.Applicable` says whether a mod can take a value now. It is false for EVE
before its quality config is loaded, for TUFX before its profiles are loaded and, for its
per-save profiles, before a save is, and for a mod whose `ready` member holds nothing or
`False`. A mod that brings its own behaviour gates its settings the same way, through a
`Ready` member of its own type (`src/Settings/Behaviours/ProvidedSettings.cs`).

A value set while the mod cannot take it is kept, and reaches the mod at the next chance
through `ReapplyWaiting`. `Read` returns null while a mod's objects are not there to ask,
and nothing read then counts as a value from before ReDefinition. A kept choice the mod no
longer offers, such as a TUFX profile from a pack since removed, waits as well rather than
going into the mod. A value from before ReDefinition goes back as it was, offered or not.

## When the add-on hands values over

`BundledSettingsAddon` drives the timing:

| When | What runs |
|---|---|
| before a scene is requested | the values, then the follow-ups that must run before the scene loads, such as Parallax's scatter renormalisation |
| when a scene has loaded, and when it is ready | the values, after the mods' own per-scene applies; requirements are enforced at both |
| when KSP's settings are applied | requirements |
| at camera changes | the values, since TUFX applies its profile at every change of camera mode |
| every two seconds | everything in the main menu, where EVE sets up its managers late; elsewhere only the mods that are waiting |
| at the end of a frame | follow-ups that run once however many settings asked for them: EVE's cloud rebuild, Parallax's material updates, KSP's settings event |

KSP's `GameEvents` call their handlers from the last subscribed to the first. These
handlers subscribe at the start of the game, so they run after the mods' own handlers in
the same frame. Values already in place are left alone, and nothing is said about them.

## `bundled.cfg`

The store's file is `GameData/ReDefinition/PluginData/bundled.cfg`:

```
REDEFINITION_BUNDLED
{
    enabled = True                  // bundle the other mods here
    asked = scatterer,eve,...       // mods the main menu's question covered
    buttonsKept = tufx              // mods whose toolbar button the player keeps

    PROFILE_APPLIED_TO              // what the chosen profile was applied to,
    {                               // and the build each mod had at the time
        eve = volumetric
        scatterer = volumetric
        ksp =
    }
    profile = high                  // the profile last applied, or empty
    VALUES
    {
        waterfall.EnableLights = False
    }
    BEFORE_REDEFINITION
    {
        ksp.SYNC_VBL = 1
        restoring = ksp.SYNC_VBL    // marked to be put back
        SAVE
        {
            name = career           // a save's folder
            tufx.profileFlight = Default-Flight
        }
    }
}
```

A file that cannot be read is set aside as `bundled.cfg.unreadable`, and the store starts
afresh. The main menu's question then comes again. There is no format number:
ReDefinition keeps no migration for its own stored formats, which change in place.

## Following the mods' own windows

`SettingsWindow.SyncFromMods` reads four times a second while the settings window is open.
It reads the rows shown, every setting of a mod whose own window is open, and all of KSP's
settings once KSP's own settings screen has applied. The rest are read as the window opens
and after *Accept*. A value changed elsewhere shows in its row, and a row the player has
changed but not applied is left alone.
