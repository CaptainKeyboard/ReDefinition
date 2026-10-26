# Comment review

DocLock cannot tell whether a comment is true or helps. This file records the reviews that do. Each review
draws a random sample of documented elements (`doclock sample`), half from files whose comments were rewritten
since the first documentation commit. Independent readers compare each comment with its code and score it from
0 to 2 for four things: **accurate**, **needed**, **compact** and **readable**. They correct a comment that is
wrong or stale.

## 2026-10-26, after the rewrite of the 918 flagged comments

Sample: 240 elements in 142 files, seed 7, drawn against b778c03. Six reviewers, one per set of files.

| Score | Elements | What it means |
|---|---|---|
| Accurate 2 | 216 (90 %) | Every statement is true of the code. |
| Accurate 1 | 24 (10 %) | One detail was stale, vague, left a case out, or could not be verified. |
| Accurate 0 | 0 | No comment said something untrue. |
| Needed 0 | 16 (7 %) | The comment only repeats the name or the code. |

Of the 24 comments with one stale or vague detail, 18 were corrected during the review. Six stay:
four cite third-party code that cannot be read here (decompiled KSP, Parallax, Community Shaders), one holds a
measurement that cannot be repeated without the game, and one named this file before it existed.

What went wrong in the 24:

* A list that lost a case: formats, kinds, number types, nodes, conditions. The code gained one, the comment did
  not.
* A name that moved: a label in the settings window, a script that moved, a set of functions that write a field.
* A mutable field described by its first assignment, while a later one changes it.
* A block comment above two neighbouring functions, one sentence of which belonged to the other.

Corrected in the review: the Dashboard's `thrustHtml`, `rich`, `tsigned`, `rollOf` and `setRoll`, `FlyByWire.Steer`,
`Json.WriteValue`, `MenuTelemetry.LoadFraction`, `IStoreHost.Adjust`, `SettingRegistration`,
`RequirementRegistration`, `KspSettingsSection.Before`, the settings window's layout request,
`FrameGeneration::generatedLastPresent`, the proxy ini's DLSS dialog name, `EditorEngineer.madeFrom`,
`DocumentedElsewhere`, `Main` of the refactoring tool and the file name in `benchmark_matrix.py`'s header.

## What the numbers say

About one comment in ten has a detail that no longer matches. Most are an omission, not a wrong statement. At
17 000 elements that is some 1 700 comments with a stale detail somewhere. The lock marks a comment dirty when
its code changes, so it catches new drift. It cannot catch the drift that was already there when the lock was
made. Reviews of fresh samples are how that drift is found and brought down. Sample again after large changes.

About one comment in fourteen only repeats the name. They are harmless and mostly on small members, tests and
fakes. The lint cannot find them all, because a comment that restates the name in other words passes it.
