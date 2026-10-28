# Migration

What happens to a player's settings when a new release changes them: each change is a step, and a
player's file goes through the steps it has not been through, once, when the game starts.

## Start here

| File | Read it for |
|---|---|
| `src/Migration/Migrations.cs` | The steps, by release: `Released` and `Unreleased`. A change to a setting that a player's file holds adds a step here. |
| `src/Migration/SettingsStep.cs` | The kinds of step: a removed, renamed or converted setting, a changed standard, a value kept on purpose. |
| `src/Migration/SettingsMigration.cs` | Running the steps on a file's values, keeping the file as it was, and putting an older file back for an older release. |
| `src/Migration/MigrationNotice.cs` | What the steps changed, kept for the one notice that tells the player and can take the old values back. |
| `src/Migration/surface.txt` | The surface of the build as it stands: every setting's standard, the registrations, the profiles, the proxy's ini. |
| `src/Migration/surface.shipped.txt` | The same for the last release. |

## What it does

- `OwnSettings.Load` reads `settings.cfg` as text, runs the steps the file has not been through, writes the file
  under the release it came from (`settings.cfg.bak-<release>`) and notes what changed.
- A file from a newer release than the running one is put back as it was before that release changed it, and the
  newer file stays as `settings.cfg.bak-newer`.
- `tools/Surface` reads the surface from a build and its files. `SurfaceTests` holds the written surface to the build
  and holds the steps to the last release's surface: a standard that changed or a setting that went must have a step.
- A release is only ever compared with the one before it. A setting renamed twice between two releases needs one step.

## Change a setting

1. Change it in the code or in a registration.
2. Add the step to `Unreleased` in `Migrations.cs` that says what a player's file does: a changed standard follows
   the new one where the player kept the old one, a removed setting leaves the file.
3. Write the surface anew: `REDEFINITION_UPDATE_SURFACE=1 dotnet test --filter TheWrittenSurface`.
4. At the release, move the steps into `Released` and write `surface.shipped.txt` from the release's build.

## Every commit says what it changed

- A commit message ends with `Connectors: unchanged` or `Connectors: changed: <what, and what that does to a player's file>`.
  The connectors are what a player or another mod sees: the settings and their standards, the registrations, the
  profiles, the proxy's ini, the catalogue of other mods' code. `surface.txt` lists them.
- `.githooks/commit-msg` checks the line against the staged files (`tools/hooks/check_connectors.sh`). `unchanged` is
  refused where `surface.txt` changes. `changed` needs `Migrations.cs` in the commit, or the words "no step needed"
  and the reason. The build sets `core.hooksPath`.
- `.github/workflows/connectors.yml` makes the same check for pushed commits, so a commit made without the hook is found.
- What the files do not show -- a setting that keeps its key and standard but means something else -- is for the
  line to say; it cannot be checked.
