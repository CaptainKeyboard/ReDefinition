# Connecting your mod: the first setting

**For:** authors of a mod that already has settings of its own.
**You need:** your mod and ReDefinition installed in the same KSP.
**You get:** one of your settings in ReDefinition's window, saved and reset like every
other, in about fifteen minutes.

Nothing about your mod has to change. You add one config file that tells ReDefinition
where your setting lives. You write no code and take no dependency: without
ReDefinition, KSP reads that file and nothing happens.

## 1. Pick the value you want to hand over

Any field or property your mod already keeps a setting in will do. For example, say
your mod holds a switch like this:

```csharp
namespace MyMod
{
    public static class Settings
    {
        public static bool fancyEffects = true;
    }
}
```

That is your mod as it already is. ReDefinition reaches such a value through
reflection, so it stays yours.

## 2. Describe it in a config file

Put a `.cfg` file anywhere in your mod's folder under `GameData`. For the example
above it says:

```
MOD_SETTINGS
{
    name = mymod
    title = MyMod
    detect = MyMod.Settings

    SETTING
    {
        name = fancyEffects
        member = MyMod.Settings.fancyEffects
        title = Fancy effects
        row = Effects
        default = True
    }
}
```

What the keys say about your mod:

* `name` is your mod's id. ReDefinition keeps the setting as `mymod.fancyEffects`.
  Change that id after a release and the player's stored choice is lost.
* `title` is your mod's name as the window shows it beside the row. Without it, the row
  says `mymod`.
* `detect` names a type that exists only with your mod. If it is not loaded, your
  file does nothing.
* `member` is where your value lives, written as in C#.
* `row` puts it in the *Effects* tab. A setting without `row` is kept and reset, but has
  no row in the window.
* `default` is the value your release ships. *Reset to defaults* sets it, and the
  profiles start from it.

## 3. See it in the game

Start KSP, open ReDefinition from the toolbar, and go to the *Effects* tab. Your row
is there, with your mod's name beside it.

Change it and press *Apply*. ReDefinition writes the value into your field.

## 4. Let your own mod save it

*Apply* already keeps the value: with no `save` key, ReDefinition holds it and sets it
at every start and scene change. Your mod's own file knows nothing of it.

If your mod saves its settings itself, name the method your own window saves with:

```
    save = MyMod.Settings.Save
```

Now *Apply* sets the field and calls that method, exactly as if the player had used
your own window. Your file and ReDefinition's window then always agree.

## 5. Let the profiles set it

The player picks a profile from Low to Max, and every bundled mod follows. If your
setting costs frame time, say so, and say what the lower tiers should set:

```
    SETTING
    {
        name = fancyEffects
        member = MyMod.Settings.fancyEffects
        title = Fancy effects
        row = Effects
        kind = Quality
        default = True
    }

    PROFILE
    {
        name = low
        fancyEffects = False
    }
```

High is your own default, so it needs no block. A tier without a block of its own keeps
that default, so here Medium, Ultra and Max leave the switch on. Write
`kind = Taste` for a setting that decides how the game looks rather than what it costs.
No `PROFILE` block touches one.

## 6. Find out why the row is missing

Nearly every problem lands in `KSP.log` behind `[ReDefinition]`, naming your mod and
the setting. The one silent case is `detect`: where that type is not loaded, your file
does nothing and says nothing.

The usual mistakes:

| What went wrong | What to do |
|---|---|
| `MyMod.Settings has no member fancyEffects` | Check the path against your build. Names are looked up in your mod's folder only. |
| `its default 'yes' is no value of its Boolean` | Write the value as a config file writes it: `True` for a `bool`, `0.5` for a number. |
| nothing in the log at all | `detect` names a type your build renamed, or one of another mod. |

If you need more, *Find out why something is missing* in
[registering-a-mod.md](registering-a-mod.md) shows what the game itself lists.

## Where to go from here

* Key bindings, your own window, per-save values, builds that differ, requirements:
  [registering-a-mod.md](registering-a-mod.md).
* Every key of the format: [registration-reference.md](registration-reference.md).
* A registration to copy:
  [examples/registration-template.cfg](examples/registration-template.cfg). A real one,
  for a mod ReDefinition does not bundle:
  [examples/Trajectories.cfg](examples/Trajectories.cfg).
* The frame's state, motion vectors, drawing after the upscaler, Direct3D 12 compute
  passes: [shared-foundation.md](shared-foundation.md).
