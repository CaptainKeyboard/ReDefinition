# The settings store

How a value set in the settings window, by a profile or by the reset reaches the other
mods, is saved, and can be put back. The code: `BundledStore`, `BundledLedger`,
`BundledFile`, behind the facade `BundledSettings`, with the game as the store's host
(`IStoreHost`); the timing in `BundledSettingsAddon`. Tested without the game in
`tests/ReDefinition.Tests/BundledStoreTests.cs` and `BundledFileTests.cs`.

## How a mod keeps a value

A registration says it (`saving`, or `save`):

| `SettingsSaving` | The mod | ReDefinition |
|---|---|---|
| `InModFiles` | saves it in its files through its own routine | keeps the value until the mod has saved it, or while the mod cannot take it yet |
| `PerSave` | keeps it per save | a choice is for the game: kept, and set into every save as it loads; a change in the mod's own window to a kept choice becomes the choice |
| `AtEveryStart` | keeps nothing | kept, and set at every start and scene change |

Mods save once after a batch of changes, not per setting. A mod whose save throws keeps
what waited for it, and saving is tried again at the next scene change.

## The operations

* **Set** -- the player's change, from the window or a profile. What installed mods require
  is applied first (`Requirements.Adjust`). The value is kept, written to the mod where it
  can take it now, and saved by the caller's `SaveNow`.
* **Release** -- a setting a newly chosen profile leaves alone while it still holds the
  applied profile's value: ReDefinition keeps no value for it any more, and its value from
  before ReDefinition is marked to be put back.
* **ResetTo** -- *Reset to defaults*. For a mod that saves, as *Set*. For a mod without a
  save routine only its running value, with no value kept in ReDefinition, so its own config stands
  at the next start; nothing is written where it holds the default already.
* **Correct** -- a value a requirement puts right. Where a choice for every save is kept, or
  the mod keeps the value for the game as a whole, it is a new choice; a per-save value with
  no choice kept is put right in the loaded save only.
* **TakeFromMod** -- a change made in the own window of a per-save mod (the hooks on TUFX and
  Distant Object) to a kept choice: the choice follows. A value an installed mod forbids does
  not become the choice; the kept one goes straight back into the save.
* **ReapplyStored** -- what is kept, handed to the mods where it is not there yet, and what is
  marked to be put back. At every scene change, on the main menu's tick, at camera changes,
  and for one mod after it has read its own file.
* **ReapplyWaiting** -- the mods a value could not reach because they could not take it then,
  asked again on the add-on's two-second tick in every scene until they can.
* **RestoreBackup** -- every setting back to what its mod had before ReDefinition.
* **SetEnabled(false)** -- the bundling off: what the mods saved stays; what only `bundled.cfg`
  kept is dropped.

## Values from before ReDefinition

Before a setting is first changed, the value its mod had is kept, once and for good -- for a
per-save setting once per save, keyed by the save's folder (`BundledSetting.Context`). That
value is written to `bundled.cfg` before the mod gets the new one, since a mod that saves on
its own could otherwise keep the new value with nothing on disk to put back; where the file
cannot be written, the mod keeps its value. For an `AtEveryStart` mod, which keeps nothing
in files of its own, the value goes into `bundled.cfg` with the next save. A
restore or a release marks each kept value to be put back once, with the bundling on or off:
a value for the game as a whole at once, a per-save value in its save the next time that
save loads. Once it is in its mod, and saved where the mod saves, it is no longer kept; a new
choice before then unmarks it. Each is tried once per scene load.

A per-save value put back is a change to its save like one made in the mod's own window:
TUFX keeps it in the game, which KSP writes when it saves, so a quicksave from before brings
back what it holds; Distant Object writes the save's own file at once.

## A mod that cannot take a value yet

`BundledSetting.Applicable` says whether a mod can take a value now: EVE before its quality
config is loaded, TUFX before its profiles are or before a save is loaded, a mod with
`ready` whose member holds nothing. A value set then is kept and reaches the mod on the next
chance (`ReapplyWaiting`). `Read` returns null while a mod's objects are not there to ask:
nothing read then counts as its value from before ReDefinition.

## When the add-on hands values over

`BundledSettingsAddon`:

* **before a scene is requested** -- values, then the follow-ups that must run before the
  scene loads (Parallax's scatter renormalisation);
* **when a scene has loaded, and when it is ready** -- after the mods' own per-scene
  applies; requirements are enforced at both, and when KSP's settings are applied. KSP's `GameEvents` call handlers from the last subscribed to the first, and these
  subscribe at the start of the game, so they run after the mods' own handlers in the same
  frame;
* **at camera changes** -- TUFX applies its profile at every change of camera mode;
* **every two seconds** -- in the main menu everything (EVE sets up its managers late there),
  elsewhere only the mods that are waiting;
* **at the end of a frame** -- follow-ups that should run once however many settings asked for
  them (EVE's cloud rebuild, Parallax's material updates, KSP's settings event).

Values already in place are left alone and nothing is said.

## `bundled.cfg`

`GameData/ReDefinition/PluginData/bundled.cfg`:

```
REDEFINITION_BUNDLED
{
    enabled = True                  // bundle the other mods here
    asked = scatterer,eve,...       // mods the main menu's question covered
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

A file that cannot be read is set aside as `bundled.cfg.unreadable` and the store starts
afresh; the main menu's question comes again. There is no format number: nothing is kept for
earlier builds of ReDefinition before its first release.

## Following the mods' own windows

While the settings window is open, `SettingsWindow.SyncFromMods` reads, four times a second,
the rows shown, every setting of a mod whose own window is open, and all of KSP's once its
own settings screen has applied; the rest as the window opens and after *Apply*. A value
changed elsewhere shows in its row; a row the player has changed and not applied is left
alone.
