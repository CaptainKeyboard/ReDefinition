# Add a setting

**For:** a developer who adds a setting to ReDefinition's own features, or to a bundled mod.
**You need:** the repository, and for a bundled mod the mod's code or a decompile of its DLL.
**You get:** the files to edit for each kind of setting, in order, and the tests that hold
them.

A setting is one value with a control and a default. ReDefinition keeps two kinds. The first
belongs to ReDefinition's own features (the upscaler, raytracing, the dashboard) and lives in
its own settings file. The second belongs to another mod and lives in that mod, which a
registration names. Pick the section that matches.

## A setting of ReDefinition's own

The example is `DashboardNetwork`, a switch that the dashboard reads every frame.

1. Add the value to `src/Modules/OwnSettings.cs` as a public field with its default, for
   example `public bool DashboardNetwork = true;`. The default there is what *Reset* sets.
2. Add its entry to `OwnSettings.Fields` in the same file: `Bool("dashboardNetwork", ...)`,
   `Float(...)`, `Enum(...)` or a `Field<string>`. The key is the one in `settings.cfg`, and
   the table decides what is saved, compared and copied.
3. Decide who reads the value. If a feature reads it each time it needs it, pass `true` as
   `live`, and read it through `ReDefinitionAddon.Live`. Then `OwnSettings.CopyLive` and
   `OwnSettings.ChangeLive` carry it, and you write no setter.
4. If something has to happen when the value changes, leave `live` off and add three things in
   `src/Modules`. Add a private field and its load line in `ReDefinitionAddon.LoadSettings`
   (`src/Modules/ReDefinitionAddon.Settings.cs`). Add a line to `ReDefinitionAddon.Collect`.
   Add a setter in `src/Modules/ReDefinitionAddon.Setters.cs` and call it from
   `ReDefinitionAddon.ApplyChanges`.
5. Add the row to the module in `src/Modules/OurModules.cs`: a `ModuleSetting` with `Key`,
   `Title`, `Tooltip`, `Order`, `Control`, `Read` and `Write`. `Read` and `Write` convert
   between the settings object and the text of the value. A slider also needs `Min`, `Max` and
   `StepsPerUnit`. A list needs `Choices`.
6. Set where the row appears. `Row` names the tab, `Section` the heading, and `Detail` puts a
   graphics row under *Detail*. `Interactable` greys the row out while it cannot apply.
7. If the player's *Reset* must keep the value, copy it in `OwnSettingsReset.From`
   (`src/Modules/OwnSettingsReset.cs`). The dashboard key is kept there.
8. If a profile should set it, name its key in a `MODULE` node of
   `GameData/ReDefinition/Profiles/ReDefinition-Profiles.cfg`, in each of the five tiers. A
   value may differ by card: `K (Rtx20 Rtx30), Default`. `ModuleProfiles.CanSet` refuses a
   `Taste` setting, a `Detail` row and any row outside `SettingCategory.General`.
9. Hold it in a test in `tests/ReDefinition.Tests`.
   `OwnSettingsTests.EveryFieldCountsInTheSnapshot` already fails when step 2 is missing. Add a
   case to `OurModulesTests` for a profile value or a row, or to `DashboardTests` for a
   dashboard setting.

A developer's flag can also be a debug switch in
`src/Diagnostics/ReDefinitionAddon.DiagnosticsOptions.cs`. That is optional.

## A setting of another mod

The example is Firefly's re-entry glow strength in
`GameData/ReDefinition/Mods/Firefly.cfg`. The format of the node is in
[the registration reference](../modders/registration-reference.md). This section covers the
steps around it.

1. Find where the mod keeps the value, and read its code to see when the mod reads it. That
   decides `takesEffect`: `Live`, `NextScene` or `Restart`.
2. Open the mod's file in `GameData/ReDefinition/Mods`. Add a `SETTING` node inside its
   `MOD_SETTINGS` node, with `name`, `member`, `default`, `title`, `tooltip`, `kind` and `row`.
   If the value is a number, add `min` and `max`.
3. If the value is a quality setting, add it to the `PROFILE` blocks of the tiers that change
   it. High is the mod's own default and needs no line unless it raises the value.
4. If no member path reaches the value, the node needs a behaviour, which is code in
   `src/Settings/Behaviours`.
5. Add the setting to the inventory in `docs/reference/mod-defaults.md`, with its source.
6. Run `dotnet test`. `ShippedRegistrationTests.EveryShippedRegistrationReadsWithoutAProblem`
   reads every file in `GameData/ReDefinition/Mods` and fails on the first problem.
7. With the mod installed, run `tools/checks/check_bundled_mods.ps1` with
   `powershell -NoProfile -File`. It builds each registration against the installed DLL
   and reports a member it cannot find.
8. If the setting relies on something in the mod's code that may change in a later version,
   record it in `ForeignAssumptions` (`src/Core/ForeignAssumptions.cs`) and run
   `tools/checks/check_foreign_code.ps1`. [The settings store](flow-setting.md) says how
   a value then reaches the mod.

## Where to change it

* A new row in the window: `src/Modules/OurModules.cs` and `src/Modules/OwnSettings.cs` for
  ReDefinition's own, and the mod's `.cfg` in `GameData/ReDefinition/Mods` for another mod.
* A profile's value: `ReDefinition-Profiles.cfg` for the own `MODULE` nodes, the `PROFILE`
  blocks of the registration for another mod.
* What a change does at once: the setter in `src/Modules/ReDefinitionAddon.Setters.cs`.

## What can go wrong

* The value is neither saved nor seen as changed. The field is missing from
  `OwnSettings.Fields`, and `OwnSettingsTests.EveryFieldCountsInTheSnapshot` fails.
* The window does not open. A row has no `Read`, no `Write` or no `Choices` for a list, and
  `OurModulesTests.EveryRowOutsideKeysHasAControlTheWindowBuilds` fails.
* A change in the window never takes effect. The field is not live and has no line in
  `ReDefinitionAddon.ApplyChanges`.
* A profile holds a value the setting cannot take. `ModuleProfiles.Values` reports it as
  `Profile '<name>', module '<module>': ...` and leaves it out.
* A registration has a problem. `KSP.log` has `[ReDefinition]` lines that name the mod and the
  setting, and the diagnostics window lists them under *Other mods*, *Registrations*.
* The mod's version has moved on. `ModDefaults` reports `Defaults for <mod> were read from
  version ...`, and the values are still used.
